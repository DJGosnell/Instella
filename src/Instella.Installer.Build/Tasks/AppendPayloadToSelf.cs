using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Instella.Core.Installation;
using Instella.Core.Internal;
using Microsoft.Build.Framework;
using Microsoft.Extensions.FileSystemGlobbing;
using MSBuildTask = Microsoft.Build.Utilities.Task;

namespace Instella.Installer.Build.Tasks;

/// <summary>
/// MSBuild task that turns the freshly published installer executable into the finished
/// installer: it appends an Instella offline payload (manifest JSON + zipped payload files)
/// to the file the user's <c>dotnet publish</c> produced.
/// </summary>
/// <remarks>
/// <para>In order:</para>
/// <list type="number">
/// <item>Refuse an already-signed exe (INSTELLA0201) and strip any payload a previous
///   publish appended, so republishing never stacks payloads.</item>
/// <item>Stamp the app icon into the exe when the manifest has a <c>.ico</c> icon and the
///   project did not set <c>ApplicationIcon</c> (Windows build hosts only).</item>
/// <item>Copy the exe to <see cref="StubOutputPath"/>, sign the copy with
///   <see cref="SignCommand"/>, and carry it in the payload as <c>.instella/instella[.exe]</c>:
///   installs stage that signed stub instead of truncating the installer.</item>
/// <item>Write the payload ZIP deterministically (sorted entries, fixed 1980-01-01 timestamps,
///   Unix permission bits in <c>ExternalAttributes</c>), append it with the resolved
///   manifest, and sign the finished installer.</item>
/// </list>
/// <para>Each item in <see cref="PayloadFiles"/> is an on-disk source (<c>Identity</c>) and a
/// zip-entry path (<c>TargetPath</c> metadata). When two items share a <c>TargetPath</c>
/// the later one wins and a warning is logged. A <c>payloadFilter</c> section in the
/// manifest is applied to the <c>TargetPath</c>s before zipping.</para>
/// <para>The emitted manifest is never modified: the resolved copy (executable name, icon
/// path) is what gets embedded. When nothing that goes into the installer changed since the
/// last run and the exe still carries that run's payload, the task does nothing
/// (<see cref="StampPath"/>).</para>
/// </remarks>
public sealed class AppendPayloadToSelf : MSBuildTask
{
    /// <summary>
    /// Path to the installer executable (the user's published output).
    /// Modified in place: payload + footer appended.
    /// </summary>
    [Required]
    public string InstallerExePath { get; set; } = string.Empty;

    /// <summary>
    /// Path to the manifest JSON file emitted by the installer's own <c>--emit-manifest</c>
    /// build-time flag. Read only.
    /// </summary>
    [Required]
    public string ManifestPath { get; set; } = string.Empty;

    /// <summary>
    /// Payload files to zip. Each item's <c>Identity</c> is the on-disk
    /// source path; each item's <c>TargetPath</c> metadata is the zip
    /// entry path (forward slashes, relative to zip root). Items with
    /// no <c>TargetPath</c> fall back to the source file's name
    /// (<c>%(Filename)%(Extension)</c>).
    /// </summary>
    [Required]
    public ITaskItem[] PayloadFiles { get; set; } = Array.Empty<ITaskItem>();

    /// <summary>
    /// Executable names of the payload projects (<c>AssemblyName</c> + the RID's executable
    /// extension). When the manifest has no <c>executableName</c>, a single name is written
    /// into it; several names without <c>WithExecutableName</c> fail the build (INSTELLA0102).
    /// </summary>
    public string[] PayloadExecutableNames { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Where to write the stub (the payload-less installer copy the payload carries). When
    /// empty, no stub is embedded and installs fall back to truncating the installer.
    /// </summary>
    public string StubOutputPath { get; set; } = string.Empty;

    /// <summary>
    /// Command that signs one file, with <c>{0}</c> for its path (<c>InstellaSignCommand</c>),
    /// run through the shell. Empty: nothing is signed.
    /// </summary>
    public string SignCommand { get; set; } = string.Empty;

    /// <summary>The project's <c>ApplicationIcon</c>; when set, the exe already has its icon and none is stamped.</summary>
    public string ApplicationIcon { get; set; } = string.Empty;

    /// <summary>
    /// Records what the last run appended, so an unchanged republish is a no-op. Empty: the
    /// task always runs.
    /// </summary>
    public string StampPath { get; set; } = string.Empty;

    /// <summary>Payload-relative path the app icon is embedded at (an Instella-owned path).</summary>
    public const string AppIconTargetPath = ".instella/app.ico";

    /// <summary>Every payload entry carries this timestamp, so identical inputs give identical bytes.</summary>
    internal static readonly DateTimeOffset EntryTimestamp = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Optional: where to write the intermediate payload ZIP. If
    /// unset, a temp path in the system temp directory is used and
    /// cleaned up on task completion.
    /// </summary>
    public string IntermediateArchivePath { get; set; } = string.Empty;

    /// <summary>Runs the task.</summary>
    public override bool Execute()
    {
        try
        {
            if (!File.Exists(InstallerExePath))
            {
                Log.LogError($"Installer exe not found: {InstallerExePath}");
                return false;
            }
            if (!File.Exists(ManifestPath))
            {
                Log.LogError($"Manifest file not found: {ManifestPath}");
                return false;
            }

            var entries = CollectEntries(PayloadFiles).ToList();
            var manifest = ResolveManifest(ManifestPath, PayloadExecutableNames, entries, out var iconSource);
            if (manifest is null)
                return false;

            var fingerprint = Fingerprint(manifest, entries);
            if (IsUpToDate(fingerprint))
            {
                Log.LogMessage(MessageImportance.High, $"Instella: {InstallerExePath} is up to date.");
                return true;
            }

            if (PayloadAppender.PrepareExe(InstallerExePath))
                Log.LogMessage(MessageImportance.Normal, "Instella: replaced the payload a previous publish appended.");

            StampIcon(iconSource);

            var isExe = InstallerExePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(StubOutputPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(StubOutputPath))!);
                File.Copy(InstallerExePath, StubOutputPath, overwrite: true);
                Sign(StubOutputPath);
                var stubEntry = InstellaOwnedPaths.StateDirectory + "/" + (isExe ? "instella.exe" : "instella");
                entries.RemoveAll(e => e.TargetPath == stubEntry);
                entries.Add(new PayloadEntry(StubOutputPath, stubEntry, Executable: true));
            }

            var workDir = Path.Combine(Path.GetTempPath(), $"instella-append-{Guid.NewGuid():N}");
            Directory.CreateDirectory(workDir);
            try
            {
                var archivePath = string.IsNullOrEmpty(IntermediateArchivePath)
                    ? Path.Combine(workDir, "payload.zip")
                    : IntermediateArchivePath;
                if (File.Exists(archivePath)) File.Delete(archivePath);

                var filter = ReadPayloadFilter(manifest);
                var effective = filter is null ? entries : ApplyFilter(entries, filter);
                var mainExe = (string?)manifest["executableName"];
                WriteZip(archivePath, effective, mainExe);

                var resolvedManifestPath = Path.Combine(workDir, "instella.json");
                File.WriteAllText(resolvedManifestPath, manifest.ToJsonString());
                PayloadAppender.AppendOfflinePayload(InstallerExePath, resolvedManifestPath, archivePath);
                Sign(InstallerExePath);

                WriteStamp(fingerprint);
                Log.LogMessage(
                    MessageImportance.High,
                    filter is null
                        ? $"Appended payload to {InstallerExePath} ({effective.Count} file(s))."
                        : $"Appended filtered payload to {InstallerExePath} ({effective.Count}/{entries.Count} file(s), filters: {filter.Include.Count} include / {filter.Exclude.Count} exclude).");
                return true;
            }
            finally
            {
                try { Directory.Delete(workDir, recursive: true); }
                catch (IOException) { /* best effort cleanup */ }
            }
        }
        catch (InstellaBuildException ex)
        {
            Log.LogError(null, ex.Code, null, null, 0, 0, 0, 0, ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            Log.LogErrorFromException(ex, showStackTrace: true);
            return false;
        }
    }

    private IReadOnlyList<PayloadEntry> CollectEntries(ITaskItem[] items)
    {
        // Collapse duplicate TargetPaths (last-write-wins) so the zip never
        // contains two entries with the same name. Warn so authors know a
        // multi-source payload is colliding.
        var byTarget = new Dictionary<string, PayloadEntry>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var source = item.GetMetadata("FullPath");
            if (string.IsNullOrEmpty(source))
                source = item.ItemSpec;
            if (!File.Exists(source))
            {
                Log.LogWarning($"Instella: payload source file '{source}' does not exist; skipping.");
                continue;
            }

            var target = NormalizeTargetPath(item.GetMetadata("TargetPath"));
            if (string.IsNullOrEmpty(target))
                target = Path.GetFileName(source);

            if (byTarget.TryGetValue(target, out var existing))
            {
                Log.LogWarning(
                    $"Instella: payload target path '{target}' is produced by multiple sources; '{source}' overwrites '{existing.Source}'.");
            }
            byTarget[target] = new PayloadEntry(source, target);
        }
        return byTarget.Values.ToArray();
    }

    /// <summary>
    /// Resolves, once and at build time, what the installer would otherwise have to guess:
    /// the main executable name, and the app icon, which is copied into the payload as
    /// <see cref="AppIconTargetPath"/> so installs never reference a build-machine path.
    /// Returns the resolved manifest; the file on disk is left as emitted.
    /// </summary>
    private System.Text.Json.Nodes.JsonObject? ResolveManifest(
        string manifestPath, string[] executableNames, List<PayloadEntry> entries, out string? iconSource)
    {
        iconSource = null;
        var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(manifestPath))?.AsObject();
        if (root is null)
        {
            Log.LogError($"Instella: manifest '{manifestPath}' is not a JSON object.");
            return null;
        }

        if (string.IsNullOrEmpty((string?)root["executableName"]))
        {
            var names = executableNames.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (names.Length > 1)
            {
                Log.LogError(null, "INSTELLA0102", null, null, 0, 0, 0, 0,
                    $"multiple payload projects ({string.Join(", ", names)}); call WithExecutableName(...) to choose the main executable.");
                return null;
            }
            if (names.Length == 1)
            {
                root["executableName"] = names[0];
            }
            else
            {
                Log.LogWarning(null, "INSTELLA0103", null, null, 0, 0, 0, 0,
                    "cannot tell which payload file is the app's executable; call WithExecutableName(...) (falls back to '<AppName>.exe').");
            }
        }

        if ((string?)root["iconPath"] is { Length: > 0 } icon && icon != AppIconTargetPath)
        {
            if (!File.Exists(icon))
            {
                Log.LogError(null, "INSTELLA0104", null, null, 0, 0, 0, 0, $"app icon '{icon}' does not exist.");
                return null;
            }
            entries.RemoveAll(e => e.TargetPath == AppIconTargetPath);
            entries.Add(new PayloadEntry(icon, AppIconTargetPath));
            root["iconPath"] = AppIconTargetPath;
            iconSource = icon;
        }

        return root;
    }

    /// <summary>
    /// The exe gets the app icon unless the project set its own
    /// <c>ApplicationIcon</c>. Only <c>.ico</c> files can be stamped, and only on Windows.
    /// </summary>
    private void StampIcon(string? iconSource)
    {
        if (iconSource is null || !string.IsNullOrEmpty(ApplicationIcon)
            || !InstallerExePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return;

        if (!iconSource.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
        {
            Log.LogMessage(MessageImportance.Normal,
                $"Instella: '{iconSource}' is not a .ico file, so it is not stamped into the exe; set <ApplicationIcon> for the exe icon.");
            return;
        }
        if (!PeIconStamper.IsSupported)
        {
            Log.LogWarning(null, "INSTELLA0205", null, null, 0, 0, 0, 0,
                "the exe icon can only be stamped on a Windows build host; set <ApplicationIcon> to embed it at compile time.");
            return;
        }
        PeIconStamper.Stamp(InstallerExePath, iconSource);
    }

    /// <summary>Runs <see cref="SignCommand"/> on <paramref name="path"/>.</summary>
    private void Sign(string path)
    {
        if (string.IsNullOrWhiteSpace(SignCommand))
            return;
        if (!SignCommand.Contains("{0}", StringComparison.Ordinal))
            throw new InstellaBuildException("INSTELLA0204", "InstellaSignCommand must contain {0} where the file path goes.");

        var command = SignCommand.Replace("{0}", path, StringComparison.Ordinal);
        // cmd /s strips exactly one pair of outer quotes and runs the rest verbatim, so the
        // user's own quoting survives; ArgumentList would escape it for the C runtime instead.
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/d /s /c \"" + command + "\"")
            : new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", command } };
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;

        Log.LogMessage(MessageImportance.High, $"Instella: signing {path}");
        using var process = Process.Start(psi)
            ?? throw new InstellaBuildException("INSTELLA0204", $"could not start the sign command for '{path}'.");
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (!string.IsNullOrWhiteSpace(stdout))
            Log.LogMessage(MessageImportance.Normal, stdout.TrimEnd());
        if (process.ExitCode != 0)
            throw new InstellaBuildException("INSTELLA0204",
                $"signing '{path}' failed (exit {process.ExitCode}): {stderr.Result.Trim()}");
    }

    /// <summary>
    /// Everything that decides the installer's bytes apart from the exe itself: the resolved
    /// manifest, each payload file's path and content hash, and the icon and sign settings.
    /// Content, not timestamps: the payload directory is republished (fresh timestamps) on
    /// every publish.
    /// </summary>
    private string Fingerprint(System.Text.Json.Nodes.JsonObject manifest, IReadOnlyList<PayloadEntry> entries)
    {
        var sb = new StringBuilder();
        sb.Append(manifest.ToJsonString()).Append('\n');
        sb.Append(SignCommand).Append('\n').Append(ApplicationIcon).Append('\n').Append(StubOutputPath).Append('\n');
        foreach (var entry in entries.OrderBy(e => e.TargetPath, StringComparer.Ordinal))
        {
            using var content = File.OpenRead(entry.Source);
            sb.Append(entry.TargetPath).Append('|').Append(Convert.ToHexStringLower(SHA256.HashData(content))).Append('\n');
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary>The stamp records the fingerprint and the payload hash that run wrote into the exe.</summary>
    private bool IsUpToDate(string fingerprint)
    {
        if (string.IsNullOrEmpty(StampPath) || !File.Exists(StampPath))
            return false;
        var lines = File.ReadAllLines(StampPath);
        if (lines.Length < 2 || lines[0] != fingerprint)
            return false;
        return CurrentPayloadHash() is { } current && current == lines[1];
    }

    private void WriteStamp(string fingerprint)
    {
        if (string.IsNullOrEmpty(StampPath) || CurrentPayloadHash() is not { } hash)
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(StampPath))!);
        File.WriteAllLines(StampPath, [fingerprint, hash]);
    }

    /// <summary>The payload hash in the exe's footer, or null when it carries no valid payload.</summary>
    private string? CurrentPayloadHash()
    {
        using var stream = new FileStream(InstallerExePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var footer = PayloadFooterReader.ReadFooter(stream, PayloadFooterReader.GetPayloadEndOffset(stream));
        return footer.IsValid ? Convert.ToHexStringLower(footer.PayloadSha256) : null;
    }

    private static string NormalizeTargetPath(string? targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
            return string.Empty;
        return targetPath.Replace('\\', '/').TrimStart('/');
    }

    private static IReadOnlyList<PayloadEntry> ApplyFilter(IReadOnlyList<PayloadEntry> entries, PayloadFilterSpec filter)
    {
        // Match case-insensitively on Windows so a preset like `**/*.pdb`
        // prunes a `Foo.PDB`/`Foo.Pdb` artifact the way the case-insensitive
        // NTFS file system would treat it; stay case-sensitive elsewhere to
        // match Linux/macOS file-name semantics.
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        // Matcher treats an empty include list as "nothing matches"; substitute
        // "**/*" so exclude-only filters still operate against every entry.
        var matcher = new Matcher(comparison);
        matcher.AddIncludePatterns(filter.Include.Count > 0 ? filter.Include : new[] { "**/*" });
        if (filter.Exclude.Count > 0)
            matcher.AddExcludePatterns(filter.Exclude);

        var result = matcher.Match("/", entries.Select(e => e.TargetPath));
        var matchedSet = new HashSet<string>(result.Files.Select(f => NormalizeTargetPath(f.Path)), comparer);
        return entries.Where(e => matchedSet.Contains(e.TargetPath) || e.Executable).ToArray();
    }

    /// <summary>
    /// Deflate ZIP: entries sorted by path, every timestamp 1980-01-01, and the
    /// Unix mode in <c>ExternalAttributes</c> — the source file's mode on a POSIX build host,
    /// otherwise 0755 for the main executable and the stub and 0644 for everything else.
    /// </summary>
    internal static void WriteZip(string archivePath, IReadOnlyList<PayloadEntry> entries, string? mainExecutable = null)
    {
        using var zipStream = File.Create(archivePath);
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Create);
        foreach (var entry in entries.OrderBy(e => e.TargetPath, StringComparer.Ordinal))
        {
            var zipEntry = zip.CreateEntry(entry.TargetPath, CompressionLevel.Optimal);
            zipEntry.LastWriteTime = EntryTimestamp;
            zipEntry.ExternalAttributes = (int)((RegularFileType | (uint)ModeOf(entry, mainExecutable)) << 16);
            using var source = File.OpenRead(entry.Source);
            using var destination = zipEntry.Open();
            source.CopyTo(destination);
        }
    }

    private const uint RegularFileType = 0x8000;   // S_IFREG

    private static UnixFileMode ModeOf(PayloadEntry entry, string? mainExecutable)
    {
        if (!OperatingSystem.IsWindows())
            return File.GetUnixFileMode(entry.Source);
        var executable = entry.Executable
                         || (mainExecutable is not null && string.Equals(entry.TargetPath, mainExecutable, StringComparison.OrdinalIgnoreCase));
        return executable ? ExecutableMode : RegularMode;
    }

    private const UnixFileMode ExecutableMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private const UnixFileMode RegularMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    private static PayloadFilterSpec? ReadPayloadFilter(System.Text.Json.Nodes.JsonObject manifest)
    {
        using var doc = JsonDocument.Parse(manifest.ToJsonString());
        if (!doc.RootElement.TryGetProperty("payloadFilter", out var filter))
            return null;
        if (filter.ValueKind != JsonValueKind.Object)
            return null;

        var include = ExtractGlobs(filter, "include");
        var exclude = ExtractGlobs(filter, "exclude");
        return include.Count == 0 && exclude.Count == 0
            ? null
            : new PayloadFilterSpec(include, exclude);
    }

    private static List<string> ExtractGlobs(JsonElement parent, string propertyName)
    {
        var list = new List<string>();
        if (!parent.TryGetProperty(propertyName, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var item in arr.EnumerateArray())
        {
            var glob = item.GetString();
            if (!string.IsNullOrWhiteSpace(glob))
                list.Add(glob);
        }
        return list;
    }

    /// <summary>One payload file. <paramref name="Executable"/> marks Instella's own executables (the stub), which filters never drop.</summary>
    internal readonly record struct PayloadEntry(string Source, string TargetPath, bool Executable = false);

    private sealed record PayloadFilterSpec(IReadOnlyList<string> Include, IReadOnlyList<string> Exclude);
}
