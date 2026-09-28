using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.FileSystem;
using Instella.Core.Installation;
using Instella.Core.Logging;
using Instella.Core.Platform;
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Runtime.Core;
using Instella.Installer.Runtime.Core.Processes;
using Instella.Installer.Runtime.Installation.BuiltIn;

namespace Instella.Installer.Runtime.Runners;

/// <summary>
/// Drives the <see cref="InstallerMode.Uninstall"/> path. Reads the installed
/// manifest from <c>--path</c> (or the running binary's directory), reverses
/// each platform-integration that was recorded, deletes tracked files, and
/// best-effort removes empty subdirectories. Replaces
/// <c>InstallationEngine.UninstallAsync</c>.
/// </summary>
internal sealed class UninstallModeRunner
{
    private readonly FrozenConfig _config;
    private readonly IInstellaLogger _log;
    private readonly IPlatformServices _platform;
    private readonly IFileSystem _fileSystem;

    public UninstallModeRunner(FrozenConfig config, IInstellaLogger log, IPlatformServices platform, IFileSystem fileSystem)
    {
        _config = config;
        _log = log;
        _platform = platform;
        _fileSystem = fileSystem;
    }

    /// <summary>Test seam: how long to wait for another Instella process on the folder.</summary>
    internal TimeSpan? LockTimeout { get; init; }

    /// <summary>Errors and the confirmation; null shows message boxes (stderr when silent).</summary>
    internal IUserMessages? Messages { get; init; }

    public async Task<InstellaExitCode> RunAsync(DispatchResult dispatch, CancellationToken ct)
    {
        var messages = Messages ?? new UserMessages(dispatch.IsSilent);
        var title = $"Uninstall {_config.AppName}";
        // Every failure is logged and shown; a GUI user sees no stderr.
        InstellaExitCode Fail(InstellaExitCode code, string text)
        {
            _log.Error($"uninstall: {text}");
            messages.Error(title, UserMessages.WithLog(text));
            return code;
        }

        var installPath = dispatch.InstallPath ?? ResolveInstallPathFromSelf();
        if (string.IsNullOrEmpty(installPath))
            return Fail(InstellaExitCode.UninstallManifestMissing, "Could not find the installation to remove; use --path.");

        // Held until the cleanup copy starts: nothing else may recover or update this folder
        // while it is being removed.
        await using var held = await Core.Transactions.InstallRootLock.AcquireOrReportAsync(
            installPath, dispatch.IsSilent, LockTimeout, _config.AppName, _log, ct);
        if (held is null) return Fail(InstellaExitCode.InstallationBusy, Core.Transactions.InstallRootLock.BusyMessage(_config.AppName));

        // Finish or undo an interrupted update first, so the manifest read below describes
        // the files that are actually there.
        try
        {
            await Core.Transactions.InstallTransaction.RecoverAsync(_fileSystem, installPath, ct, _log);
        }
        catch (UnsupportedJournalException ex)
        {
            return Fail(InstellaExitCode.UpdateRollbackFailed, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail(InstellaExitCode.UninstallGeneralFailure,
                $"Could not recover an interrupted update in '{installPath}': {ex.Message}");
        }

        var writer = new InstallManifestWriter(_fileSystem);
        var manifest = await writer.ReadAsync(installPath, ct);
        if (manifest is null)
            return Fail(InstellaExitCode.UninstallManifestMissing, $"No installation of {_config.AppName} was found in '{installPath}'.");

        // Interactive uninstall shows a Yes/No confirmation (per the
        // 2026-04-17 "Uninstall UI stays minimal" decision). Silent uninstall
        // skips the prompt so scripted/managed removals stay unattended.
        if (!dispatch.IsSilent && OperatingSystem.IsWindows())
        {
            if (!messages.Confirm($"Uninstall {manifest.AppName}", $"Are you sure you want to uninstall {manifest.AppName}?"))
            {
                _log.Info("uninstall: user cancelled at the confirmation dialog");
                return InstellaExitCode.UserCancelled;
            }
        }

        // Nothing may hold the app's files while they are deleted: a running app made the
        // uninstall skip every DLL it had loaded and left them behind.
        var prompt = dispatch.IsSilent ? null
            : AppRunningPrompt ?? (OperatingSystem.IsWindows() ? RunningAppGate.MessageBoxPrompt : null);
        var gate = new RunningAppGate(_platform, _log, ProcessFinder, _fileSystem);
        if (!await gate.EnsureClosedAsync(manifest.AppName, installPath, manifest.ExecutableName, dispatch.ForceClose, prompt, ct))
        {
            if (prompt is not null && !dispatch.ForceClose)
            {
                _log.Info("uninstall: user cancelled while programs were using the app's files");
                return InstellaExitCode.UserCancelled;
            }
            return Fail(InstellaExitCode.UninstallFilesLocked,
                $"{manifest.AppName}'s files are in use. Close the programs using them, or pass --force-close.");
        }

        _log.Info($"uninstall: {manifest.AppName} v{manifest.Version} from {installPath}");

        try
        {
            await RunUninstallHooksAsync(manifest, installPath, dispatch, prompt, ct);
            await Migrations.MigrationPipeline.RemoveAdoptedItemsAsync(
                manifest, installPath, _config.AppManagedRunValuesOrEmpty, _platform, _log, ct);
            await RemoveIntegrationAsync(manifest, ct);
            var locked = await DeleteFilesAsync(installPath, manifest, ct);

            // What this process could not delete (its own running .exe, files in use) is
            // left to a guarded cleanup run from a temporary copy.
            var remaining = await DeleteOwnedFilesAsync(installPath, ct);
            remaining.AddRange(locked);
            await held.DisposeAsync();
            if (remaining.Count > 0)
                await ScheduleCleanupAsync(installPath, manifest.AppId, remaining, ct);
            else
                await _fileSystem.DeleteDirectoryAsync(installPath, recursive: false, ct);

            if (locked.Count > 0)
            {
                var preview = string.Join(", ", locked.Take(5));
                if (locked.Count > 5) preview += $", +{locked.Count - 5} more";
                _log.Warn($"uninstall: {locked.Count} file(s) in use and skipped: {preview}");
                return InstellaExitCode.UninstallFilesLocked;
            }

            return InstellaExitCode.Success;
        }
        catch (OperationCanceledException)
        {
            return InstellaExitCode.UserCancelled;
        }
        catch (Exception ex)
        {
            _log.Error($"uninstall: {ex.Message}", ex);
            messages.Error(title, UserMessages.WithLog($"{_config.AppName} could not be removed completely: {ex.Message}"));
            return InstellaExitCode.UninstallGeneralFailure;
        }
    }

    /// <summary>Test hook: finds processes using the app's files; null uses Restart Manager.</summary>
    internal ILockingProcessFinder? ProcessFinder { get; init; }

    /// <summary>Test hook: the seams uninstall migrations use (known folders, processes, programs); null uses the host's.</summary>
    internal Migrations.MigrationRuntime? MigrationRuntime { get; init; }

    /// <summary>Test hook: where uninstall migrations find the known folders; null uses the host's.</summary>
    internal Migrations.KnownFolderResolver? KnownFolders { get; init; }

    /// <summary>Test hook: closes programs for uninstall migrations; null asks them through the platform.</summary>
    internal IProcessCloser? ProcessCloser { get; init; }

    /// <summary>Starts the app's uninstall handler and migration programs; null starts real processes.</summary>
    internal Migrations.IProgramRunner? Programs { get; init; }

    /// <summary>Test hook: asks about programs using the app's files; null shows a message box.</summary>
    internal AppRunningPrompt? AppRunningPrompt { get; init; }

    /// <summary>Test hook: starts the cleanup process; null uses <see cref="LaunchCleanupCopy"/>.</summary>
    internal Action<string, string>? CleanupLauncher { get; init; }

    /// <summary>
    /// Deletes Instella's own files (stub, app icon, state folder). Returns the relative paths
    /// that could not be deleted — typically the running stub on Windows.
    /// </summary>
    private async Task<List<string>> DeleteOwnedFilesAsync(string installPath, CancellationToken ct)
    {
        var remaining = new List<string>();
        foreach (var rel in new[] { InstellaOwnedPaths.StubFileName, InstellaOwnedPaths.AppIcon, InstellaOwnedPaths.InstalledManifest })
        {
            var path = SafePath.Combine(installPath, rel);
            if (_fileSystem.Exists(path) && !(await _fileSystem.DeleteFileAsync(path, ct)).Success)
                remaining.Add(rel);
        }
        var state = Path.Combine(installPath, InstellaOwnedPaths.StateDirectory);
        if (remaining.Count == 0 && _fileSystem.DirectoryExists(state))
            await _fileSystem.DeleteDirectoryAsync(state, recursive: true, ct);
        return remaining;
    }

    /// <summary>
    /// Writes the tombstone that authorises exactly one cleanup of exactly
    /// <paramref name="remaining"/>, then starts cleanup from a temporary copy of this program.
    /// </summary>
    private async Task ScheduleCleanupAsync(string installPath, string appId, List<string> remaining, CancellationToken ct)
    {
        var nonce = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        var tombstone = new UninstallTombstone(appId, Path.GetFullPath(installPath), nonce, DateTimeOffset.UtcNow, remaining);
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(tombstone, CleanupJsonContext.Default.UninstallTombstone);
        var path = SafePath.Combine(installPath, UninstallTombstone.RelativePath);
        await _fileSystem.CreateDirectoryAsync(Path.GetDirectoryName(path)!, ct);
        await _fileSystem.WriteAllBytesAsync(path, bytes, ct);

        try
        {
            (CleanupLauncher ?? LaunchCleanupCopy)(installPath, nonce);
            _log.Info($"uninstall: cleanup of {remaining.Count} remaining file(s) scheduled");
        }
        catch (Exception ex)
        {
            // Not fatal: the files stay behind, and the log says which.
            _log.Warn($"uninstall: could not start cleanup ({ex.Message}); left: {string.Join(", ", remaining)}");
        }
    }

    /// <summary>
    /// Copies this program to <c>%TEMP%\Instella\cleanup\{nonce}.exe</c> and starts
    /// <c>--cleanup --path {root} --token {nonce} --parent-pid {pid}</c>. Running from a copy
    /// is what lets the original be deleted.
    /// </summary>
    private static void LaunchCleanupCopy(string installPath, string nonce)
    {
        var self = Environment.ProcessPath ?? throw new InvalidOperationException("cannot determine the running program");
        Directory.CreateDirectory(CleanupModeRunner.TempCopyDirectory);
        var copy = Path.Combine(CleanupModeRunner.TempCopyDirectory, nonce + Path.GetExtension(self));
        File.Copy(self, copy, overwrite: true);

        var psi = new System.Diagnostics.ProcessStartInfo(copy)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = CleanupModeRunner.TempCopyDirectory,
        };
        foreach (var a in new[] { "--cleanup", "--path", installPath, "--token", nonce, "--parent-pid",
                     Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), "--silent" })
            psi.ArgumentList.Add(a);
        System.Diagnostics.Process.Start(psi)?.Dispose();
    }

    private async Task RemoveIntegrationAsync(InstalledManifest manifest, CancellationToken ct)
    {
        var executablePath = Path.Combine(manifest.InstallDirectory, manifest.ExecutableName);

        if (manifest.HasDesktopShortcut)
        {
            await _platform.RemoveShortcutAsync(new ShortcutInfo(
                manifest.AppName, executablePath, null, null, ShortcutLocation.Desktop, PerUser: manifest.InstalledPerUser), ct);
        }

        if (manifest.HasStartMenuShortcut)
        {
            await _platform.RemoveShortcutAsync(new ShortcutInfo(
                manifest.AppName, executablePath, null, null, ShortcutLocation.StartMenu, PerUser: manifest.InstalledPerUser), ct);
        }

        if (manifest.FileAssociations is { Count: > 0 })
        {
            foreach (var ext in manifest.FileAssociations)
                await _platform.UnregisterFileAssociationAsync(ext, manifest.AppId, manifest.InstalledPerUser, ct);
        }

        if (manifest.AddedToPath)
            await _platform.RemoveFromPathAsync(manifest.InstallDirectory, manifest.InstalledPerUser, ct);

        if (manifest.HasAutoStart)
            await _platform.RemoveAutoStartAsync(manifest.AppId, manifest.InstalledPerUser, ct);

        if (manifest.HasUninstallEntry)
            await _platform.UnregisterUninstallEntryAsync(manifest.AppId, manifest.InstalledPerUser, ct);

        // v3 manifests carry the full registry-writes list: values first, then the keys
        // Instella created (deepest first), and a key only if nothing else is left in it.
        if (manifest.Registry is { Count: > 0 } entries)
        {
            foreach (var entry in entries.Where(e => !e.IsKey))
            {
                ct.ThrowIfCancellationRequested();
                var ok = await _platform.DeleteRegistryValueAsync(entry.Hive, entry.KeyPath, entry.ValueName, entry.PerUser, ct);
                if (!ok.Success)
                    _log.Warn($"uninstall: could not delete registry value {entry.KeyPath}\\{entry.ValueName}: {ok.Error}");
            }
            foreach (var key in entries.Where(e => e.IsKey).OrderByDescending(e => e.KeyPath.Count(c => c == '\\')))
            {
                var deleted = await _platform.DeleteRegistryKeyIfEmptyAsync(key.Hive, key.KeyPath, key.PerUser, ct);
                if (!deleted.Success)
                    _log.Info($"uninstall: registry key {key.KeyPath} left in place ({deleted.Error})");
            }
        }

        await ReverseTrackedItemsAsync(manifest, ct);
    }

    /// <summary>Replays what custom steps tracked, newest first.</summary>
    private async Task ReverseTrackedItemsAsync(InstalledManifest manifest, CancellationToken ct)
    {
        var items = manifest.TrackedItems ?? [];
        for (var i = items.Count - 1; i >= 0; i--)
        {
            ct.ThrowIfCancellationRequested();
            var item = items[i];
            var ok = item.Kind switch
            {
                "file" => !_fileSystem.Exists(item.Path) || (await _fileSystem.DeleteFileAsync(item.Path, ct)).Success,
                "directory" => !_fileSystem.DirectoryExists(item.Path)
                               || (await _fileSystem.DeleteDirectoryAsync(item.Path, item.Recursive, ct)).Success,
                "registry-value" => (await _platform.DeleteRegistryValueAsync(item.Hive, item.Path, item.ValueName ?? "", manifest.InstalledPerUser, ct)).Success,
                "registry-key" => (await _platform.DeleteRegistryKeyAsync(item.Hive, item.Path, manifest.InstalledPerUser, ct)).Success,
                "path-entry" => (await _platform.RemoveFromPathAsync(item.Path, manifest.InstalledPerUser, ct)).Success,
                _ => true,
            };
            if (!ok)
                _log.Warn($"uninstall: could not undo tracked {item.Kind} '{item.Path}'");
        }
    }

    /// <summary>
    /// Runs the uninstall migrations (<c>Order</c>, then id), then every custom step's
    /// <c>OnUninstall</c> in reverse stage order, before Instella's own unregistration: uninstall
    /// mirrors install in reverse, and migrations ran last on install. A failing migration or hook
    /// is logged; it does not stop the uninstall.
    /// </summary>
    private async Task RunUninstallHooksAsync(InstalledManifest manifest, string installPath, DispatchResult dispatch,
        AppRunningPrompt? prompt, CancellationToken ct)
    {
        var migrations = Migrations.MigrationPipeline.ForUninstall(_config.MigrationsOrEmpty);
        var hooks = _config.UserSteps
            .Select((step, index) => (step, index))
            .Where(x => x.step.OnUninstall is not null)
            .OrderByDescending(x => x.step.Stage)
            .ThenByDescending(x => x.index)
            .ToList();

        var options = new InstallOptions
        {
            InstallPath = installPath,
            CreateDesktopShortcut = manifest.HasDesktopShortcut,
            CreateStartMenuShortcut = manifest.HasStartMenuShortcut,
            AddToPath = manifest.AddedToPath,
            ConfigureAutoStart = manifest.HasAutoStart,
            RegisterFileAssociations = manifest.FileAssociations is { Count: > 0 },
            Elevation = manifest.InstalledPerUser ? Instella.Core.Manifest.ElevationMode.PerUser : Instella.Core.Manifest.ElevationMode.SystemWide,
        };
        var context = InstallContextFactory.Create(
            _config, InstallerMode.Uninstall, installPath, options, _platform, _fileSystem, _log,
            existing: manifest, cli: dispatch.CliOrEmpty);
        context.Migrations = MigrationRuntime ?? new Migrations.MigrationRuntime
        {
            Folders = KnownFolders ?? Migrations.KnownFolderResolver.Host,
            ProcessFinder = ProcessFinder ?? DefaultLockingProcessFinder.Instance,
            ProcessCloser = ProcessCloser,
            Programs = Programs ?? Migrations.ProcessProgramRunner.Instance,
            Prompt = prompt,
            ForceClose = dispatch.ForceClose,
        };

        // An install that stopped mid-way may have left migration changes to undo, before anything is removed.
        await Migrations.MigrationUndo.RecoverAsync(context, ct);

        // The app's own code first, while everything Instella set up is still in place.
        await RunAppUninstallAsync(manifest, installPath, context, ct);
        if (hooks.Count == 0 && migrations.Count == 0) return;

        foreach (var migration in migrations)
            await Migrations.MigrationExecution.RunAsync(migration, context, ct);

        foreach (var (step, _) in hooks)
        {
            try
            {
                _log.Info($"uninstall: running OnUninstall of step '{step.Name}'");
                await step.OnUninstall!(context, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Warn($"uninstall: OnUninstall of step '{step.Name}' threw: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Runs the app's upgrade program with <c>--instella-uninstall</c> when its declaration opts in
    /// (<c>handlesUninstall</c>). Only a program whose bytes match the installed manifest's record runs.
    /// A failure is a warning: the uninstall always continues.
    /// </summary>
    private async Task RunAppUninstallAsync(InstalledManifest manifest, string installPath, Installation.InstallContext context,
        CancellationToken ct)
    {
        try
        {
            var runner = new AppUpgrade.AppUpgradeRunner(_fileSystem, context.Migrations.Programs, _log);
            var result = await runner.RunAsync(new AppUpgrade.AppUpgradeRequest
            {
                Mode = AppUpgradeLaunchMode.Uninstall,
                From = manifest.Version,
                To = null,
                Scope = manifest.InstalledPerUser ? InstallationScope.PerUser : InstallationScope.SystemWide,
                InstallPath = installPath,
                AppId = manifest.AppId,
                Platform = manifest.Platform,
                InstalledFiles = manifest.Files.Select(f => f.RelativePath).ToList(),
                ExpectedHashes = manifest.Files
                    .GroupBy(f => f.RelativePath.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First().Sha256, StringComparer.OrdinalIgnoreCase),
            }, progress: null, ct);
            if (!result.Success)
                _log.Warn($"uninstall: the app's uninstall handler did not succeed ({result.Message}); continuing");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warn($"uninstall: the app's uninstall handler could not run ({ex.Message}); continuing");
        }
    }

    private async Task<List<string>> DeleteFilesAsync(string installPath, InstalledManifest manifest, CancellationToken ct)
    {
        var locked = new List<string>();

        foreach (var file in manifest.Files)
        {
            ct.ThrowIfCancellationRequested();
            string path;
            try
            {
                path = SafePath.Combine(installPath, file.RelativePath);
            }
            catch (UnsafePathException ex)
            {
                // A manifest is data on disk: an entry that would leave the folder is never deleted.
                _log.Warn($"uninstall: skipping manifest entry '{file.RelativePath}': {ex.Message}");
                continue;
            }
            if (!_fileSystem.Exists(path)) continue;

            var result = await _fileSystem.DeleteFileAsync(path, ct);
            if (!result.Success)
            {
                _log.Warn($"uninstall: could not delete {file.RelativePath}: {result.Error?.Message ?? "unknown error"}");
                locked.Add(await MoveToPendingDeleteAsync(installPath, file.RelativePath, ct) ?? file.RelativePath);
            }
        }

        RemoveEmptySubdirectoriesBottomUp(installPath);

        return locked;
    }

    /// <summary>
    /// Moves a file that could not be deleted into <see cref="InstellaOwnedPaths.PendingDeleteDirectory"/>
    /// (Windows lets a loaded DLL be renamed, not deleted), so the install folder holds only
    /// Instella's own leftovers, which cleanup and the next install remove. Returns the new
    /// relative path, or null when the file could not be moved either.
    /// </summary>
    private async Task<string?> MoveToPendingDeleteAsync(string installPath, string relativePath, CancellationToken ct)
    {
        var rel = InstellaOwnedPaths.PendingDeleteDirectory + "/" + Guid.NewGuid().ToString("N") + "-" + Path.GetFileName(relativePath);
        var dest = SafePath.Combine(installPath, rel);
        await _fileSystem.CreateDirectoryAsync(Path.GetDirectoryName(dest)!, ct);
        var moved = await _fileSystem.MoveFileAsync(Path.Combine(installPath, relativePath), dest, overwrite: false, ct);
        if (!moved.Success) return null;
        _log.Info($"uninstall: moved {relativePath} to {rel} for cleanup");
        return rel;
    }

    private static void RemoveEmptySubdirectoriesBottomUp(string root)
    {
        if (!Directory.Exists(root)) return;

        var subdirs = Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
            .OrderByDescending(d => d.Length)
            .ToList();

        foreach (var dir in subdirs)
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                if (Directory.EnumerateFileSystemEntries(dir).Any()) continue;
                Directory.Delete(dir);
            }
            catch
            {
                // Best-effort.
            }
        }
    }

    private static string? ResolveInstallPathFromSelf()
    {
        try
        {
            var selfPath = Environment.ProcessPath
                           ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(selfPath)) return null;
            return Path.GetDirectoryName(selfPath);
        }
        catch
        {
            return null;
        }
    }
}
