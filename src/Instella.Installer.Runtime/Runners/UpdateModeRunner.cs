using System;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Diff;
using Instella.Core.FileSystem;
using Instella.Core.Installation;
using Instella.Core.Logging;
using Instella.Core.Platform;
using Instella.Core.Update;
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Runtime.Core;
using Instella.Installer.Runtime.Core.Processes;
using Instella.Installer.Runtime.Core.Update;

namespace Instella.Installer.Runtime.Runners;

/// <summary>
/// Drives the <see cref="InstallerMode.Update"/> path. Parses
/// <see cref="UpdaterArgs"/> from the tail of the CLI and delegates to the
/// existing <see cref="UpdaterEngine"/>; maps <c>UpdateResult</c> to
/// <see cref="InstellaExitCode"/>.
/// </summary>
internal sealed class UpdateModeRunner
{
    private readonly FrozenConfig _config;
    private readonly IInstellaLogger _log;
    private readonly IPlatformServices _platform;
    private readonly IFileSystem _fileSystem;

    public UpdateModeRunner(FrozenConfig config, IInstellaLogger log, IPlatformServices platform, IFileSystem fileSystem)
    {
        _config = config;
        _log = log;
        _platform = platform;
        _fileSystem = fileSystem;
    }

    /// <summary>Test hook: the window host; null picks the Win32 host for interactive runs.</summary>
    internal InteractiveHostFactory? HostFactory { get; init; }

    /// <summary>Test seam: how long to wait for another Instella process on the folder.</summary>
    internal TimeSpan? LockTimeout { get; init; }

    /// <summary>Test hook: restarts the app after the update; null uses <see cref="AppLauncher"/>.</summary>
    internal IAppLauncher? Launcher { get; init; }

    /// <summary>Injected services (HTTP handler); null uses the defaults.</summary>
    internal InstallerServices? Services { get; init; }

    /// <summary>Errors before the update window opens; null shows message boxes (stderr when silent).</summary>
    internal IUserMessages? Messages { get; init; }

    public async Task<InstellaExitCode> RunAsync(string[] args, CancellationToken ct)
    {
        var updaterArgs = UpdaterArgs.Parse(args);
        var messages = Messages ?? new UserMessages(updaterArgs?.Silent ?? true);
        var title = $"Update {_config.AppName}";
        // A failure before the update window opens is logged and shown; the window shows
        // the rest.
        InstellaExitCode Fail(InstellaExitCode code, string logText, string? userText = null)
        {
            _log.Error($"update: {logText}");
            messages.Error(title, UserMessages.WithLog(userText ?? logText));
            return code;
        }

        if (updaterArgs is null)
            return Fail(InstellaExitCode.UsageInvalidArgs,
                "invalid arguments. Required: --app-path, --app-exe, --from-version, --to-version");

        _log.Info($"update: {updaterArgs.AppPath} v{updaterArgs.FromVersion} -> v{updaterArgs.ToVersion}");

        // Second line of defence for target binding (RunAsync checks first): an updater only
        // updates the installation it belongs to.
        var stubDir = Services?.StubDirectoryOrDefault() ?? InstallPaths.StubDirectory();
        if (stubDir is null
            || !InstallPaths.TryNormalize(updaterArgs.AppPath, requireRooted: false, out var appPath, out _)
            || !InstallPaths.TryNormalize(stubDir, requireRooted: false, out var ownPath, out _)
            || !InstallPaths.SameFolder(appPath, ownPath))
        {
            return Fail(InstellaExitCode.UsageInvalidArgs,
                $"this updater only updates the installation it belongs to ({stubDir}), not '{updaterArgs.AppPath}'");
        }

        try
        {
            // The server and package id come from the installed manifest, never the command
            // line. The architecture is the one the build was installed as, not the
            // OS's: an x64 build under emulation on ARM64 keeps receiving x64 updates.
            var installed = await new InstallManifestWriter(_fileSystem).ReadAsync(updaterArgs.AppPath, ct);
            if (installed is null)
                return Fail(InstellaExitCode.UpdateGeneralFailure, $"no readable installed manifest in '{updaterArgs.AppPath}'");
            if (!string.Equals(installed.AppId, _config.AppId, StringComparison.Ordinal))
                return Fail(InstellaExitCode.UpdateGeneralFailure,
                    $"the installed manifest belongs to another app ('{installed.AppId}', not '{_config.AppId}')");
            if (string.IsNullOrEmpty(installed.ServerUrl))
                return Fail(InstellaExitCode.UpdateGeneralFailure, "the installation has no update server");
            if (Instella.Core.Wire.ServerUrlPolicy.Check(installed.ServerUrl, installed.AllowInsecureServer) is { } urlProblem)
                return Fail(InstellaExitCode.UpdateGeneralFailure, urlProblem);
            var architecture = installed.Architecture ?? ArchitectureExtensions.Current;
            var target = updaterArgs.Repair ? installed.Version : updaterArgs.ToVersion;

            // The token stored at install time, never one from the command line.
            var http = Instella.Core.Wire.ServerHttp.Create(new Uri(installed.ServerUrl), installed.DownloadToken,
                "Instella-Updater/1.0", TimeSpan.FromMinutes(30), Services?.HttpHandler);
            using var downloader = new HttpUpdateDownloader(http, ownsHttp: true, installed.ServerUrl, installed.AppId,
                installed.Version, target, installed.Platform, architecture, updaterArgs.PatchSha256);

            var engine = new UpdaterEngine(updaterArgs, downloader, _platform, _fileSystem, BsDiffEngine.Instance)
            {
                AppRunningPrompt = OperatingSystem.IsWindows() && !updaterArgs.Silent && Services?.HostFactory is null
                    ? RunningAppGate.MessageBoxPrompt
                    : null,
                ProcessFinder = Services?.ProcessFinder,
            };
            engine.LogMessage += (_, msg) => _log.Info(msg);

            // Interactive updates show a small progress window; silent ones run headless.
            var hostFactory = HostFactory ?? Services?.HostFactory ?? (OperatingSystem.IsWindows() && !updaterArgs.Silent
                ? InteractiveInstallRunner.CreateDefaultWin32Host
                : null);
            // One Instella process per install folder: held for the update, released
            // before the app is restarted.
            await using var held = await Core.Transactions.InstallRootLock.AcquireOrReportAsync(
                updaterArgs.AppPath, updaterArgs.Silent, LockTimeout, installed.AppName, _log, ct);
            if (held is null)
                return Fail(InstellaExitCode.InstallationBusy, "another Instella process is using the installation",
                    Core.Transactions.InstallRootLock.BusyMessage(installed.AppName));

            // The app is restarted without elevation, and without arguments: the post-update
            // marker carries them (UpdaterArgs.ExtraArgs).
            var launcher = Launcher ?? Services?.AppLauncher ?? new AppLauncher(_log);
            void Restart(UpdateResult r)
            {
                held.DisposeAsync().AsTask().GetAwaiter().GetResult();
                if (r.ExecutablePath is { } exe) launcher.Launch(exe, updaterArgs.AppPath);
            }

            UpdateResult result;
            if (hostFactory is null)
            {
                result = await engine.RunAsync(ct);
                if (result.Success && updaterArgs.Restart) Restart(result);
            }
            else
            {
                result = await UpdateWindow.RunAsync(engine, installed.AppName, target, updaterArgs.Repair, hostFactory, ct,
                    updaterArgs.Restart, updaterArgs.RestartCountdown, Restart);
            }
            return result.ExitCode;
        }
        catch (OperationCanceledException)
        {
            return InstellaExitCode.UserCancelled;
        }
        catch (System.Net.Http.HttpRequestException ex)
        {
            _log.Error($"update: server unreachable: {ex.Message}");
            return InstellaExitCode.UpdateServerUnreachable;
        }
        catch (Exception ex)
        {
            _log.Error($"update: {ex.Message}", ex);
            return InstellaExitCode.UpdateGeneralFailure;
        }
    }
}
