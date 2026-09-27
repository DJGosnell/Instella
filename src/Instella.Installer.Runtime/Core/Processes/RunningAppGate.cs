using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Logging;
using Instella.Core.Platform;

namespace Instella.Installer.Runtime.Core.Processes;

/// <summary>
/// A process that has a file of the installation open or loaded. <see cref="CanClose"/> is
/// false for Explorer, services and critical system processes, which are never closed.
/// <see cref="StartTime"/> (a FILETIME, 0 when unknown) guards against a reused PID.
/// </summary>
internal sealed record LockingProcess(int Id, string Name, bool CanClose, long StartTime = 0)
{
    public override string ToString() => $"{Name} (PID {Id})";
}

/// <summary>Finds the processes that hold any of a set of files.</summary>
internal interface ILockingProcessFinder
{
    IReadOnlyList<LockingProcess> Find(IReadOnlyList<string> files);
}

/// <summary>Restart Manager on Windows; nothing elsewhere (the name-based check still runs).</summary>
internal sealed class DefaultLockingProcessFinder : ILockingProcessFinder
{
    public static readonly DefaultLockingProcessFinder Instance = new();

    public IReadOnlyList<LockingProcess> Find(IReadOnlyList<string> files) =>
        OperatingSystem.IsWindows() ? RestartManager.FindLockingProcesses(files) : [];
}

/// <summary>What the user chose when programs are using the installation's files.</summary>
internal enum AppRunningChoice
{
    Cancel,
    Retry,
    CloseAutomatically,
}

/// <summary>Asks the user what to do about <paramref name="programs"/> using <paramref name="appName"/>'s files.</summary>
internal delegate AppRunningChoice AppRunningPrompt(string appName, IReadOnlyList<string> programs);

/// <summary>
/// Makes sure nothing holds the files of an installation before uninstall, upgrade, repair
/// or update touches them. Every process that has a file under the install folder open or
/// loaded counts (Restart Manager), plus copies of the app's executable running from there,
/// whatever the process is called. This process, and other processes of the same program
/// (an elevated child's parent), never count.
/// </summary>
internal sealed class RunningAppGate
{
    private readonly IPlatformServices _platform;
    private readonly IInstellaLogger _log;
    private readonly ILockingProcessFinder _finder;
    private readonly Instella.Core.FileSystem.IFileSystem _fs;

    /// <param name="platform">Lists running processes.</param>
    /// <param name="log">Installer log.</param>
    /// <param name="finder">Finds programs using files; null uses Restart Manager.</param>
    /// <param name="fs">Lists the installation's files; null uses the real disk (the harness passes its own).</param>
    public RunningAppGate(IPlatformServices platform, IInstellaLogger log, ILockingProcessFinder? finder = null,
        Instella.Core.FileSystem.IFileSystem? fs = null)
    {
        _platform = platform;
        _log = log;
        _finder = finder ?? DefaultLockingProcessFinder.Instance;
        _fs = fs ?? Instella.Core.FileSystem.RealFileSystem.Instance;
    }

    /// <summary>How long a program gets to close after being asked, before it is ended.</summary>
    public TimeSpan CloseTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>The processes using files under <paramref name="installPath"/>.</summary>
    public IReadOnlyList<LockingProcess> FindBlockers(string installPath, string executableName)
    {
        var self = Environment.ProcessId;
        var selfPath = Environment.ProcessPath;
        var found = new Dictionary<int, LockingProcess>();

        if (_fs.DirectoryExists(installPath))
        {
            try
            {
                var files = _fs.EnumerateFiles(installPath, "*", recursive: true).ToList();
                foreach (var p in _finder.Find(files))
                    if (p.Id != self && !IsSameProgram(p.Id, selfPath))
                        found.TryAdd(p.Id, p);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                _log.Warn($"could not list the programs using '{installPath}': {ex.Message}");
            }
        }

        foreach (var p in _platform.GetRunningProcesses(Path.GetFileNameWithoutExtension(executableName), installPath))
        {
            using (p)
                if (p.Id != self) found.TryAdd(p.Id, new LockingProcess(p.Id, p.ProcessName, CanClose: true));
        }
        return found.Values.ToList();
    }

    /// <summary>
    /// Returns true once no closable program uses the installation. <paramref name="forceClose"/>
    /// closes them without asking; otherwise <paramref name="prompt"/> decides, and without a
    /// prompt (silent) the answer is no.
    /// </summary>
    public async Task<bool> EnsureClosedAsync(
        string appName, string installPath, string executableName, bool forceClose, AppRunningPrompt? prompt,
        CancellationToken ct)
    {
        while (true)
        {
            var blockers = FindBlockers(installPath, executableName);
            foreach (var b in blockers.Where(b => !b.CanClose))
                _log.Info($"{b} has files open in '{installPath}'; it is not closed");
            var closable = blockers.Where(b => b.CanClose).ToList();
            if (closable.Count == 0) return true;

            _log.Info($"files in '{installPath}' are in use by {string.Join(", ", closable)}");
            var choice = forceClose ? AppRunningChoice.CloseAutomatically
                : prompt?.Invoke(appName, closable.Select(b => b.ToString()).ToList()) ?? AppRunningChoice.Cancel;
            if (choice == AppRunningChoice.Cancel) return false;
            if (choice == AppRunningChoice.Retry) continue;

            await CloseAsync(closable, ct);
            var left = FindBlockers(installPath, executableName).Where(b => b.CanClose).ToList();
            if (left.Count == 0) return true;
            _log.Warn($"still running: {string.Join(", ", left)}");
            if (forceClose || prompt is null) return false;
        }
    }

    /// <summary>Asks each program to close, then ends the ones still running after <see cref="CloseTimeout"/>.</summary>
    public Task CloseAsync(IEnumerable<LockingProcess> blockers, CancellationToken ct) =>
        Task.WhenAll(blockers.Where(b => b.CanClose).Select(async b =>
        {
            using var process = Open(b);
            if (process is null) return;
            _log.Info($"closing {b}");
            var result = await _platform.TerminateProcessAsync(process, CloseTimeout, ct);
            if (!result.Success) _log.Warn($"could not close {b}: {result.Error}");
        }));

    /// <summary>
    /// The Windows prompt: Continue closes the programs, Try Again checks again after the user
    /// closed them, Cancel gives up.
    /// </summary>
    public static AppRunningChoice MessageBoxPrompt(string appName, IReadOnlyList<string> programs)
    {
        if (!OperatingSystem.IsWindows()) return AppRunningChoice.Cancel;
        var list = string.Join("\n", programs.Select(p => "    " + p));
        var result = UI.Windows.Win32.MessageBoxW(0,
            $"These programs are using {appName}'s files:\n\n{list}\n\n" +
            "Click Continue to close them automatically (unsaved work in them may be lost), " +
            "or close them yourself and click Try Again.",
            $"Close {appName} to continue",
            UI.Windows.MB.CANCELTRYCONTINUE | UI.Windows.MB.ICONWARNING | UI.Windows.MB.TOPMOST);
        return result switch
        {
            UI.Windows.IDRESULT.TRYAGAIN => AppRunningChoice.Retry,
            UI.Windows.IDRESULT.CONTINUE => AppRunningChoice.CloseAutomatically,
            _ => AppRunningChoice.Cancel,
        };
    }

    private static Process? Open(LockingProcess b)
    {
        Process? p = null;
        try
        {
            p = Process.GetProcessById(b.Id);
            if (b.StartTime != 0 && p.StartTime.ToFileTimeUtc() != b.StartTime)
            {
                p.Dispose();   // the PID now belongs to another process
                return null;
            }
            return p;
        }
        catch (ArgumentException) { p?.Dispose(); return null; }            // already exited
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return p;   // start time not readable (another user, elevated): still try to close it
        }
    }

    private static bool IsSameProgram(int pid, string? selfPath)
    {
        if (selfPath is null) return false;
        try
        {
            using var p = Process.GetProcessById(pid);
            return string.Equals(p.MainModule?.FileName, selfPath, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
