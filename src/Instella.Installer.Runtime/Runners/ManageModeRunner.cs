using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.FileSystem;
using Instella.Core.Installation;
using Instella.Core.Logging;
using Instella.Core.Diff;
using Instella.Core.Platform;
using Instella.Core.Wire;
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Runtime.Core;
using Instella.Installer.Runtime.Core.Update;
using Instella.Installer.Runtime.UI.Windows;
using Instella.Core.Update;
using Instella.Core.Utilities;

namespace Instella.Installer.Runtime.Runners;

/// <summary>
/// Launches the interactive Windows manager window (Uninstall / Update /
/// Repair / Close) and returns the user's choice. The production
/// <see cref="ManageModeRunner"/> uses <see cref="ManageModeRunner.DefaultManagerUiLauncher"/>
/// (which shows a real <see cref="Win32ManagerUI"/> on Windows and is a no-op
/// returning <see cref="ManagerAction.None"/> elsewhere); tests inject a stub so
/// the action-dispatch logic can be verified on any host without spinning up a
/// native window.
/// </summary>
internal delegate ManagerAction ManagerUiLauncher(
    string appName,
    string version,
    string installPath,
    string? serverUrl,
    bool updateAvailable,
    bool repairAvailable);

/// <summary>
/// Hands an uninstall request off to the real uninstall pipeline. The default
/// implementation drives <see cref="UninstallModeRunner"/>; tests inject a
/// recording stub because the real uninstaller shows a confirmation dialog and
/// starts a detached cleanup process from a temporary copy of the stub — neither
/// of which is safe to run inside a unit test.
/// </summary>
internal delegate Task<InstellaExitCode> UninstallHandoff(DispatchResult dispatch, CancellationToken ct);

/// <summary>
/// Runs the Manage window's Check for Updates (<paramref name="repair"/> false) or Repair
/// (<paramref name="repair"/> true) in-process. Tests inject a recording stub; the default
/// talks to the update server and runs <see cref="UpdaterEngine"/>.
/// </summary>
internal delegate Task<InstellaExitCode> UpdateHandoff(InstalledManifest manifest, string installPath, bool repair, CancellationToken ct);

/// <summary>
/// Drives the <see cref="InstallerMode.Manage"/> path — what runs when the
/// user double-clicks the staged <c>instella.exe</c> from inside an install
/// directory. On interactive Windows sessions the manager UI is shown
/// (Uninstall / Update / Repair / Close); silent invocations and non-Windows
/// platforms fall back to a headless status report so scripted callers and
/// CI environments stay unattended.
/// </summary>
/// <remarks>
/// The Win32 UI launch and the uninstall hand-off are injected as delegates so
/// the action-dispatch logic stays unit-testable on any host (see the internal
/// constructor). Production code uses the public constructor, which wires the
/// real <see cref="Win32ManagerUI"/> and <see cref="UninstallModeRunner"/>.
/// Repair re-verifies every installed file against the installed version's signed release
/// and replaces any that differ; Check for Updates asks the server and, when an update is
/// available, runs the update engine in-process. Both need the installation's
/// update server and are hidden without one.
/// </remarks>
internal sealed class ManageModeRunner
{
    private readonly FrozenConfig _config;
    private readonly IInstellaLogger _log;
    private readonly IPlatformServices _platform;
    private readonly IFileSystem _fileSystem;
    private readonly ManagerUiLauncher _uiLauncher;
    private readonly UninstallHandoff _uninstallHandoff;
    private readonly UpdateHandoff _updateHandoff;

    public ManageModeRunner(FrozenConfig config, IInstellaLogger log, IPlatformServices platform, IFileSystem fileSystem)
        : this(config, log, platform, fileSystem, DefaultManagerUiLauncher, uninstallHandoff: null, updateHandoff: null)
    {
    }

    internal ManageModeRunner(
        FrozenConfig config,
        IInstellaLogger log,
        IPlatformServices platform,
        IFileSystem fileSystem,
        ManagerUiLauncher uiLauncher,
        UninstallHandoff? uninstallHandoff,
        UpdateHandoff? updateHandoff = null)
    {
        _config = config;
        _log = log;
        _platform = platform;
        _fileSystem = fileSystem;
        _uiLauncher = uiLauncher;
        // The default hand-off references instance fields, so it can't be a
        // constructor-chain argument — bind it here once the fields are set.
        _uninstallHandoff = uninstallHandoff ?? DefaultUninstallHandoffAsync;
        _updateHandoff = updateHandoff ?? DefaultUpdateHandoffAsync;
    }

    /// <summary>
    /// Elevation for machine-wide installations: the Manage window opens unelevated and
    /// relaunches elevated only for a mutating action. Null means none available.
    /// </summary>
    internal Core.Elevation.IElevationService? Elevation { get; init; }

    /// <summary>The folder the running stub is in; null uses the process's own folder.</summary>
    internal Func<string?>? StubDirectory { get; init; }

    /// <summary>Errors, notices and questions; null shows message boxes.</summary>
    internal IUserMessages? Messages { get; init; }

    /// <summary>Handler for the update check (tests); null uses a default client.</summary>
    internal System.Net.Http.HttpMessageHandler? HttpHandler { get; init; }

    private IUserMessages Dialogs => _messages ??= Messages ?? new UserMessages(silent: false);
    private IUserMessages? _messages;

    /// <summary>
    /// The installation is not the one this stub belongs to: its actions run unelevated or
    /// not at all, never through a UAC prompt (target binding).
    /// </summary>
    private bool _foreign;
    private string _installPath = "";
    private Core.Transactions.InstallRootLock? _held;

    /// <summary>Test seam: how long to wait for another Instella process on the folder.</summary>
    internal TimeSpan? LockTimeout { get; init; }

    public async Task<InstellaExitCode> RunAsync(DispatchResult dispatch, CancellationToken ct)
    {
        var installPath = dispatch.InstallPath ?? ResolveInstallPathFromSelf();
        if (string.IsNullOrEmpty(installPath))
        {
            _log.Info($"manage: {_config.AppName} (no install location detected)");
            return InstellaExitCode.Success;
        }
        _installPath = installPath;
        var stubDir = (StubDirectory ?? InstallPaths.StubDirectory)();
        _foreign = !(stubDir is not null
                     && InstallPaths.TryNormalize(installPath, requireRooted: false, out var target, out _)
                     && InstallPaths.TryNormalize(stubDir, requireRooted: false, out var own, out _)
                     && InstallPaths.SameFolder(target, own));

        // One Instella process per install folder. Released before an elevated relaunch:
        // the child takes it itself, and a parent waiting for its child must not hold it.
        await using var held = await Core.Transactions.InstallRootLock.AcquireOrReportAsync(
            installPath, dispatch.IsSilent, LockTimeout, _config.AppName, _log, ct);
        if (held is null)
        {
            if (!dispatch.IsSilent)
                ShowMessage(_config.AppName, UserMessages.WithLog(Core.Transactions.InstallRootLock.BusyMessage(_config.AppName)), error: true);
            return InstellaExitCode.InstallationBusy;
        }
        _held = held;

        // Finish or undo an interrupted update first, so the manifest read below describes
        // the files that are actually there.
        try
        {
            await Core.Transactions.InstallTransaction.RecoverAsync(_fileSystem, installPath, ct, _log);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Error($"could not recover an interrupted update in '{installPath}': {ex.Message}");
            return InstellaExitCode.UpdateRollbackFailed;
        }

        var writer = new InstallManifestWriter(_fileSystem);
        var manifest = await writer.ReadAsync(installPath, ct);
        if (manifest is null)
        {
            _log.Info($"manage: {_config.AppName} (no manifest at {installPath})");
            return InstellaExitCode.Success;
        }

        _log.Info($"manage: {manifest.AppName} v{manifest.Version} @ {installPath}");
        _log.Info($"manage: installed on {manifest.InstalledAt:yyyy-MM-dd HH:mm:ss} UTC ({(manifest.InstalledPerUser ? "per-user" : "system-wide")})");
        _log.Info($"manage: {manifest.Files.Count} tracked file(s)");

        // Silent invocations stay headless: the status lines above are the
        // whole contract for scripted/managed callers.
        if (dispatch.IsSilent)
            return InstellaExitCode.Success;

        // The default launcher returns None on non-Windows platforms
        // (interactive manage is Windows-only today), which dispatches to a
        // clean Success — the same headless outcome as a silent run.
        var hasServer = !string.IsNullOrEmpty(manifest.ServerUrl);
        var action = _uiLauncher(
            manifest.AppName,
            manifest.Version.ToString(),
            installPath,
            manifest.ServerUrl,
            updateAvailable: hasServer,
            repairAvailable: hasServer);

        return await DispatchManagerActionAsync(action, manifest, installPath, ct);
    }

    private async Task<InstellaExitCode> DispatchManagerActionAsync(
        ManagerAction action, InstalledManifest manifest, string installPath, CancellationToken ct)
    {
        switch (action)
        {
            case ManagerAction.Uninstall when NeedsElevation(manifest):
                _log.Info("manage: user chose Uninstall of a machine-wide installation — relaunching elevated");
                return await RelaunchElevatedAsync(["--uninstall", "--path", installPath], ct);

            case ManagerAction.Uninstall:
                _log.Info("manage: user chose Uninstall — handing off to the uninstall pipeline");
                var uninstall = new DispatchResult(
                    DispatchKind.Mode, InstallerMode.Uninstall, installPath, IsSilent: false);
                return await _uninstallHandoff(uninstall, ct);

            case ManagerAction.Update:
                _log.Info("manage: user chose Check for Updates");
                return await _updateHandoff(manifest, installPath, repair: false, ct);

            case ManagerAction.Repair:
                _log.Info("manage: user chose Repair");
                return await _updateHandoff(manifest, installPath, repair: true, ct);

            case ManagerAction.None:
            default:
                _log.Info("manage: manager closed with no action");
                return InstellaExitCode.Success;
        }
    }

    /// <summary>
    /// Production manager-UI launcher: shows the real <see cref="Win32ManagerUI"/>
    /// on Windows; on every other platform it returns
    /// <see cref="ManagerAction.None"/> so the runner falls back to a clean
    /// headless Success (interactive manage is Windows-only).
    /// </summary>
    internal static ManagerAction DefaultManagerUiLauncher(
        string appName, string version, string installPath, string? serverUrl,
        bool updateAvailable, bool repairAvailable)
    {
        if (!OperatingSystem.IsWindows())
            return ManagerAction.None;

        var ui = new Win32ManagerUI(appName, version, installPath, serverUrl, updateAvailable, repairAvailable);
        return ui.Run();
    }

    private Task<InstellaExitCode> DefaultUninstallHandoffAsync(DispatchResult dispatch, CancellationToken ct)
        => new UninstallModeRunner(_config, _log, _platform, _fileSystem).RunAsync(dispatch, ct);

    /// <summary>
    /// Production update/repair: repair pins the installed version; an update first asks
    /// <c>check-update</c> which version to go to. Results are shown in a message box.
    /// </summary>
    internal async Task<InstellaExitCode> DefaultUpdateHandoffAsync(
        InstalledManifest manifest, string installPath, bool repair, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(manifest.ServerUrl))
            return InstellaExitCode.UsageInvalidArgs;
        if (ServerUrlPolicy.Check(manifest.ServerUrl, manifest.AllowInsecureServer) is { } urlProblem)
        {
            _log.Error($"manage: {urlProblem}");
            ShowMessage(manifest.AppName, $"The update server address is not allowed: {urlProblem}", error: true);
            return InstellaExitCode.UpdateGeneralFailure;
        }

        var arch = manifest.Architecture ?? ArchitectureExtensions.Current;
        var target = manifest.Version;
        var usePatch = false;
        string? patchSha = null;
        if (!repair)
        {
            CheckUpdateResponse? check;
            try
            {
                using var http = ServerHttp.Create(new Uri(manifest.ServerUrl), manifest.DownloadToken, "Instella-Manager/1.0",
                    TimeSpan.FromSeconds(30), HttpHandler);
                check = await http.GetFromJsonAsync(
                    ApiRoutes.ForCheckUpdate(new Uri(manifest.ServerUrl), manifest.AppId, manifest.Version,
                        manifest.Platform, arch, manifest.Channel ?? "stable"),
                    WireJsonContext.Default.CheckUpdateResponse, ct);
            }
            // A timeout or a reply that isn't JSON (a captive portal) is reported, not a crash.
            catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or NotSupportedException
                                       || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                var reason = ex is TaskCanceledException ? "the server did not answer in time"
                    : ex is HttpRequestException ? ex.Message
                    : "the server's reply was not an update check";
                _log.Error($"manage: update check failed: {ex.Message}");
                ShowMessage(manifest.AppName, UserMessages.WithLog($"Could not check for updates: {reason}."), error: true);
                return InstellaExitCode.UpdateServerUnreachable;
            }

            if (check is not { UpdateAvailable: true, Version: { } v } || !AppVersions.TryParse(v, out var newer))
            {
                ShowMessage(manifest.AppName, $"{manifest.AppName} {manifest.Version} is up to date.", error: false);
                return InstellaExitCode.Success;
            }
            if (!Confirm(manifest.AppName, $"Update {manifest.AppName} from {manifest.Version} to {newer}?"))
                return InstellaExitCode.UserCancelled;
            target = newer;
            usePatch = check.PatchAvailable;
            patchSha = check.PatchSha256;
        }

        var args = new UpdaterArgs
        {
            AppPath = installPath,
            AppExecutable = manifest.ExecutableName,
            FromVersion = manifest.Version,
            ToVersion = target,
            Channel = manifest.Channel ?? "stable",
            UsePatch = usePatch,
            PatchSha256 = patchSha,
            Repair = repair,
            Restart = false,
        };
        using var downloader = new HttpUpdateDownloader(
            ServerHttp.Create(new Uri(manifest.ServerUrl), manifest.DownloadToken, "Instella-Updater/1.0", TimeSpan.FromMinutes(30), HttpHandler),
            ownsHttp: true, manifest.ServerUrl, manifest.AppId, manifest.Version, target, manifest.Platform, arch, patchSha);
        if (NeedsElevation(manifest))
            return await RelaunchElevatedAsync(args.ToArgumentList(), ct);

        var engine = new UpdaterEngine(args, downloader, _platform, _fileSystem, BsDiffEngine.Instance);
        engine.LogMessage += (_, msg) => _log.Info(msg);
        var result = await engine.RunAsync(ct);

        ShowMessage(manifest.AppName, result.Success
            ? repair ? $"{manifest.AppName} was repaired." : $"{manifest.AppName} was updated to {target}."
            : $"{(repair ? "Repair" : "Update")} failed: {result.Error}", error: !result.Success);
        return result.ExitCode;
    }

    private bool NeedsElevation(InstalledManifest manifest) =>
        !manifest.InstalledPerUser && Elevation is { IsElevated: false };

    private async Task<InstellaExitCode> RelaunchElevatedAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (_foreign)
        {
            var message = ElevationGate.ForeignMachineInstallMessage(_installPath);
            _log.Error($"manage: {message}");
            ShowMessage(_config.AppName, message, error: true);
            return InstellaExitCode.InsufficientPrivileges;
        }
        if (_held is not null) await _held.DisposeAsync();
        var code = await Elevation!.RelaunchElevatedAsync([.. args, "--elevated-child"], ct);
        if (code is { } exit) return (InstellaExitCode)exit;
        _log.Warn("manage: the administrator prompt was declined");
        return InstellaExitCode.InsufficientPrivileges;
    }

    private void ShowMessage(string title, string text, bool error)
    {
        if (error) Dialogs.Error(title, text);
        else Dialogs.Info(title, text);
    }

    private bool Confirm(string title, string text) => Dialogs.Confirm(title, text);

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
