using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.FileSystem;
using Instella.Core.Installation;
using Instella.Core.Logging;

namespace Instella.Installer.Runtime.Runners;

/// <summary>
/// Written by a successful uninstall to <c>{root}/.instella/uninstall.tombstone</c>. It
/// authorises exactly one cleanup of exactly these files: <see cref="Remaining"/> lists what
/// the uninstaller could not delete itself (its own running .exe, files that were in use).
/// </summary>
internal sealed record UninstallTombstone(
    string AppId,
    string InstallRoot,
    string Nonce,
    DateTimeOffset CreatedAt,
    IReadOnlyList<string> Remaining)
{
    /// <summary>Tombstone path relative to the install root.</summary>
    public const string RelativePath = InstellaOwnedPaths.StateDirectory + "/uninstall.tombstone";
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(UninstallTombstone))]
internal partial class CleanupJsonContext : JsonSerializerContext;

/// <summary>
/// <c>--cleanup --path {root} --token {nonce} --parent-pid {pid}</c>: run from a
/// temporary copy of the stub after an uninstall, it deletes what the running uninstaller
/// could not delete itself. It refuses to act unless <see cref="ValidateCleanupTarget"/>
/// passes, deletes only the files the tombstone lists, then <c>.instella/</c> and empty
/// directories bottom-up. Files the user added are left in place.
/// </summary>
internal sealed class CleanupModeRunner
{
    /// <summary>Where uninstall puts the temporary copies that run cleanup.</summary>
    internal static string TempCopyDirectory => Path.Combine(Path.GetTempPath(), "Instella", "cleanup");

    private readonly IInstellaLogger _log;
    private readonly IReadOnlyList<TimeSpan> _retryDelays;
    private readonly TimeSpan _parentWait;

    public CleanupModeRunner(IInstellaLogger log)
        : this(log, [TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)], TimeSpan.FromSeconds(30))
    {
    }

    internal CleanupModeRunner(IInstellaLogger log, IReadOnlyList<TimeSpan> retryDelays, TimeSpan parentWait)
    {
        _log = log;
        _retryDelays = retryDelays;
        _parentWait = parentWait;
    }

    public async Task<InstellaExitCode> RunAsync(DispatchResult dispatch, string[] args, CancellationToken ct)
    {
        SweepOldTempCopies();

        var token = ValueOf(args, "--token");
        if (string.IsNullOrEmpty(dispatch.InstallPath) || string.IsNullOrEmpty(token))
        {
            _log.Error("cleanup: --path and --token are required (cleanup is started by uninstall, not by hand)");
            return InstellaExitCode.UsageInvalidArgs;
        }

        if (ValidateCleanupTarget(dispatch.InstallPath, token) is { } refusal)
        {
            _log.Error($"cleanup: {refusal}; nothing deleted");
            return InstellaExitCode.UsageInvalidArgs;
        }

        if (int.TryParse(ValueOf(args, "--parent-pid"), out var parentPid))
            await WaitForExitAsync(parentPid, _parentWait, ct);

        var root = Path.GetFullPath(dispatch.InstallPath);
        var tombstone = ReadTombstone(root)!;
        var left = new List<string>();
        foreach (var rel in tombstone.Remaining)
        {
            ct.ThrowIfCancellationRequested();
            string path;
            try { path = SafePath.Combine(root, rel); }
            catch (UnsafePathException) { _log.Warn($"cleanup: skipping unsafe tombstone entry '{rel}'"); continue; }
            if (!await DeleteWithRetryAsync(path, ct)) left.Add(rel);
        }

        // The tombstone itself goes with the state folder.
        TryDeleteDirectory(Path.Combine(root, InstellaOwnedPaths.StateDirectory), recursive: true);
        RemoveEmptyDirectoriesBottomUp(root);

        // Files cleanup could not delete are still Instella's; only the rest belongs to the user.
        var leftPaths = left.Select(rel => Path.GetFullPath(Path.Combine(root, rel))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var state = Path.Combine(root, InstellaOwnedPaths.StateDirectory) + Path.DirectorySeparatorChar;
        var userFiles = Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Count(f => !leftPaths.Contains(f) && !f.StartsWith(state, StringComparison.OrdinalIgnoreCase))
            : 0;
        if (left.Count > 0)
        {
            var later = left.All(rel => rel.StartsWith(InstellaOwnedPaths.StateDirectory + "/", StringComparison.Ordinal))
                ? $"; the next install into '{root}' removes them" : "";
            _log.Warn($"cleanup: still in use, could not delete: {string.Join(", ", left)}{later}");
        }
        if (userFiles > 0)
            _log.Info($"cleanup: left {userFiles} file(s) in '{root}' that were not installed by Instella");
        else if (left.Count == 0)
            _log.Info($"cleanup: '{root}' removed");
        return InstellaExitCode.Success;
    }

    /// <summary>
    /// Returns why cleanup must not touch <paramref name="path"/>, or null when it may: never a
    /// volume root, a protected folder or an ancestor of one, and only with a tombstone whose
    /// nonce equals <paramref name="token"/> (compared in constant time) and whose root is
    /// <paramref name="path"/>.
    /// </summary>
    internal static string? ValidateCleanupTarget(string path, string token)
    {
        string full;
        try { full = Path.GetFullPath(path).TrimEnd('\\', '/'); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "the path is not valid";
        }

        if (Core.PathGuards.RefusalFor(full, []) is { } refusal)
            return $"refusing to clean: {refusal}";

        var tombstone = ReadTombstone(full);
        if (tombstone is null) return "no uninstall tombstone";
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(tombstone.Nonce), Encoding.UTF8.GetBytes(token)))
            return "tombstone token mismatch";
        if (!Path.GetFullPath(tombstone.InstallRoot).TrimEnd('\\', '/').Equals(full, StringComparison.OrdinalIgnoreCase))
            return "tombstone root mismatch";
        return null;
    }

    internal static UninstallTombstone? ReadTombstone(string root)
    {
        var path = Path.Combine(root, UninstallTombstone.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize(File.ReadAllBytes(path), CleanupJsonContext.Default.UninstallTombstone)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private async Task<bool> DeleteWithRetryAsync(string path, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= _retryDelays.Count) return false;
                await Task.Delay(_retryDelays[attempt], ct);
            }
        }
    }

    private static async Task WaitForExitAsync(int pid, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            using var parent = Process.GetProcessById(pid);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            await parent.WaitForExitAsync(cts.Token);
        }
        catch (ArgumentException)
        {
            // Already gone.
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timed out: carry on; locked files are retried and reported.
        }
    }

    /// <summary>Removes empty directories bottom-up, the root last; a non-empty one stops its branch.</summary>
    private static void RemoveEmptyDirectoriesBottomUp(string root)
    {
        if (!Directory.Exists(root)) return;
        foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                     .OrderByDescending(d => d.Length).ToList())
        {
            if (!Directory.EnumerateFileSystemEntries(dir).Any())
                TryDeleteDirectory(dir, recursive: false);
        }
        if (!Directory.EnumerateFileSystemEntries(root).Any())
            TryDeleteDirectory(root, recursive: false);
    }

    private static void TryDeleteDirectory(string dir, bool recursive)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* left for the log */ }
    }

    /// <summary>
    /// The temporary copy cannot delete itself, so every run removes sibling copies older than
    /// a day: at most one small file per recent uninstall is ever left behind.
    /// </summary>
    private void SweepOldTempCopies()
    {
        try
        {
            if (!Directory.Exists(TempCopyDirectory)) return;
            var self = Environment.ProcessPath;
            foreach (var file in Directory.EnumerateFiles(TempCopyDirectory))
            {
                if (string.Equals(file, self, StringComparison.OrdinalIgnoreCase)) continue;
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < TimeSpan.FromHours(24)) continue;
                try { File.Delete(file); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* still running */ }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"cleanup: could not sweep old copies: {ex.Message}");
        }
    }

    private static string? ValueOf(string[] args, string name)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == name && i + 1 < args.Length) return args[i + 1];
            if (args[i].StartsWith(name + "=", StringComparison.Ordinal)) return args[i][(name.Length + 1)..];
        }
        return null;
    }
}
