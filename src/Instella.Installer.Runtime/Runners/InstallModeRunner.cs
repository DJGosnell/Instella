using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.FileSystem;
using Instella.Core.Installation;
using Instella.Core.Logging;
using Instella.Core.Manifest;
using Instella.Core.Platform;
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Runtime.Core;
using Instella.Installer.Runtime.Core.Processes;
using Instella.Installer.Runtime.Installation;
using Instella.Installer.Runtime.Installation.BuiltIn;
using Instella.Installer.Runtime.UI.Widgets;

namespace Instella.Installer.Runtime.Runners;

/// <summary>
/// Drives the install path — first install, upgrade or repair, decided by
/// <see cref="InstallModeResolver"/> once the install path is known: resolves the
/// payload source (embedded-archive for offline installers, server download
/// for lite installers), constructs an <see cref="InstallContext"/>, runs the
/// merged (built-in + user) step list through <see cref="StepExecutor"/>, and
/// maps the result to an <see cref="InstellaExitCode"/>.
/// </summary>
internal sealed class InstallModeRunner
{
    private readonly FrozenConfig _config;
    private readonly IInstellaLogger _log;
    private readonly IPlatformServices _platform;
    private readonly IFileSystem _fileSystem;

    public InstallModeRunner(FrozenConfig config, IInstellaLogger log, IPlatformServices platform, IFileSystem fileSystem)
    {
        _config = config;
        _log = log;
        _platform = platform;
        _fileSystem = fileSystem;
    }

    /// <summary>Injected services (HTTP handler for lite downloads); null uses the defaults.</summary>
    internal InstallerServices? Services { get; init; }

    /// <summary>Test hook: finds processes using the app's files; null uses Restart Manager.</summary>
    internal ILockingProcessFinder? ProcessFinder { get; init; }

    /// <summary>Test seam: how long to wait for another Instella process on the folder.</summary>
    internal TimeSpan? LockTimeout { get; init; }

    public async Task<InstellaExitCode> RunAsync(DispatchResult dispatch, CancellationToken ct)
    {
        _log.Info($"install: {_config.AppName} v{_config.AppVersion} ({(dispatch.IsSilent ? "silent" : "interactive")})");

        // Non-silent installs go through the interactive wizard, which exists only on
        // Windows. Elsewhere a double-click must not install silently: it explains
        // and exits 40. Silent installs use the headless flow below.
        if (!dispatch.IsSilent)
        {
            if (!OperatingSystem.IsWindows())
            {
                const string message = "interactive UI is not available on this platform; re-run with --silent to install";
                _log.Error($"install: {message}");
                Console.Error.WriteLine($"error: {message}");
                return InstellaExitCode.UsageInvalidArgs;
            }
            var interactiveRunner = new InteractiveInstallRunner(_config, _log, _platform, _fileSystem,
                Services?.HostFactory, Services?.HostFactory is null ? null : (_, _) => AppRunningChoice.Cancel)
            {
                HttpHandler = Services?.HttpHandler,
                ProcessFinder = Services?.ProcessFinder,
                AppLauncher = Services?.AppLauncher,
                Programs = Services?.Programs,
            };
            return await interactiveRunner.RunAsync(dispatch, ct);
        }

        var scope = dispatch.Scope ?? InstallationScope.PerUser;
        var installPath = dispatch.InstallPath ?? InstallPaths.Default(_config, _platform, scope);
        var options = new InstallOptions
        {
            InstallPath = installPath,
            CreateDesktopShortcut = _config.Shortcuts?.Desktop ?? false,
            CreateStartMenuShortcut = _config.Shortcuts?.StartMenu ?? false,
            AddToPath = _config.PathRegistration,
            ConfigureAutoStart = _config.AutoStart,
            RegisterFileAssociations = _config.FileAssociations.Count > 0,
            Elevation = InstallPaths.ElevationFor(scope),
        };

        // Allow the install-path resolver, if any, to override the computed path. It gets a
        // provisional context; the real one is built once the mode is known. Author code: a
        // throw or an unusable folder is a configuration error (exit 10), never a crash.
        if (dispatch.InstallPath is null && _config.InstallPathResolver is { } resolver)
        {
            string? resolved;
            try
            {
                var provisional = InstallContextFactory.Create(
                    _config, InstallerMode.FirstInstall, installPath, options, _platform, _fileSystem, _log);
                resolved = resolver(provisional);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Error($"install: WithInstallPath threw: {ex.Message}");
                return InstellaExitCode.InstallGeneralFailure;
            }
            if (!string.IsNullOrEmpty(resolved))
            {
                if (!InstallPaths.TryNormalize(resolved, requireRooted: false, out var normalized, out var problem))
                {
                    _log.Error($"install: WithInstallPath returned an unusable folder: {problem}");
                    return InstellaExitCode.InstallGeneralFailure;
                }
                installPath = normalized;
                options = options with { InstallPath = normalized };
            }
        }

        // One Instella process per install folder, from the mode decision to the last step.
        await using var held = await Core.Transactions.InstallRootLock.AcquireOrReportAsync(
            installPath, silent: true, LockTimeout, _config.AppName, _log, ct);
        if (held is null) return InstellaExitCode.InstallationBusy;

        Stream? payload = null;
        string? tempDownloadPath = null;
        try
        {
            var (mode, existing) = await InstallModeResolver.ResolveAsync(_config, _fileSystem, installPath, dispatch, _log, ct);
            // An upgrade keeps the choices of the installation it replaces.
            options = InstallModeResolver.FollowExisting(options, _config, existing);
            if (existing is not null)
            {
                _log.Info($"install: {mode} of v{existing.Version} in '{installPath}'");
                if (!await new RunningAppGate(_platform, _log, ProcessFinder ?? Services?.ProcessFinder, _fileSystem).EnsureClosedAsync(
                        existing.AppName, installPath, existing.ExecutableName, dispatch.ForceClose, prompt: null, ct))
                {
                    _log.Error($"install: {existing.AppName}'s files are in use; close the programs using them or pass --force-close");
                    return InstellaExitCode.UpdateAppCouldNotClose;
                }
            }

            // Silent means "answer the pages without a UI, and fail if they cannot be
            // answered", checked before anything is downloaded or written.
            var pages = new Dictionary<string, PageState>(StringComparer.Ordinal);
            var context = InstallContextFactory.Create(
                _config, mode, installPath, options, _platform, _fileSystem, _log,
                existing: existing, cli: dispatch.CliOrEmpty, pages: pages);
            await PageFlow.ResolveSilentlyAsync(_config, mode, dispatch.CliOrEmpty, context, pages, ct);

            payload = Services?.Payload?.Invoke() ?? OpenEmbeddedPayload();
            if (payload is null)
            {
                if (string.IsNullOrEmpty(_config.ServerUrl))
                {
                    _log.Error("install: no embedded payload and no WithServer(url) configured");
                    return InstellaExitCode.InstallGeneralFailure;
                }
                tempDownloadPath = Path.Combine(Path.GetTempPath(), $"instella-download-{Guid.NewGuid():N}.zip");
                using var http = Instella.Core.Wire.ServerHttp.Create(new Uri(_config.ServerUrl), _config.DownloadToken,
                    "Instella-Installer/1.0", TimeSpan.FromMinutes(30), Services?.HttpHandler);
                payload = await ServerPayloadDownloader.DownloadAsync(_config, tempDownloadPath, _log, http, ct);
            }
            context.PayloadArchive = payload;

            var builtIns = new List<IInstallStepExecution>(OfflineInstallRunner.BuildDefaultSteps());
            if (_config.RegistryWrites.Count > 0)
            {
                // Slot the registry-writes step after the structural Register
                // steps (shortcut/ARP stub/etc.) so the keys exist only after
                // files are in place. Ordered before WriteManifest (Finalize),
                // so a registry write failure still rolls back cleanly.
                builtIns.Add(new WriteRegistrySpecsStep(_config.RegistryWrites));
            }

            var steps = StepOrdering.BuildOrderedSteps(builtIns, _config.UserSteps, _config.MigrationsOrEmpty);
            // Silent: migrations close programs only with --force-close, as the upgrade gate does.
            context.Migrations = new Migrations.MigrationRuntime
            {
                Folders = Services?.KnownFolders ?? Migrations.KnownFolderResolver.Host,
                ProcessFinder = ProcessFinder ?? Services?.ProcessFinder ?? DefaultLockingProcessFinder.Instance,
                ProcessCloser = Services?.ProcessCloser,
                ForceClose = dispatch.ForceClose,
                Programs = Services?.Programs ?? Migrations.ProcessProgramRunner.Instance,
            };
            // An earlier run of an installer on this folder may have stopped mid-install: finish its migrations' undo.
            await Migrations.MigrationUndo.RecoverAsync(context, ct);

            var executor = new StepExecutor(steps);
            var result = await executor.ExecuteAsync(context, progress: null, ct);

            if (!result.Success)
            {
                if (ct.IsCancellationRequested) return InstellaExitCode.UserCancelled;
                return InstallFailureExit.For(result);
            }

            return InstellaExitCode.Success;
        }
        catch (OperationCanceledException)
        {
            return InstellaExitCode.UserCancelled;
        }
        catch (InstallRefusedException ex)
        {
            _log.Error($"install: {ex.Message}");
            return ex.ExitCode;
        }
        catch (Instella.Core.Internal.FooterIntegrityException ex)
        {
            _log.Error($"install: the installer file is damaged; download it again ({ex.Message})");
            return InstellaExitCode.InstallIntegrityFailed;
        }
        catch (SilentMissingStateException ex)
        {
            _log.Error($"install: {ex.Message}");
            Console.Error.WriteLine($"error: {ex.Message}");
            return InstellaExitCode.InstallSilentMissingState;
        }
        catch (HttpRequestException ex)
        {
            _log.Error($"install: server unreachable: {ex.Message}");
            return InstellaExitCode.UpdateServerUnreachable;
        }
        catch (Instella.Core.Trust.UpdateTrustException ex)
        {
            _log.Error($"install: the downloaded payload failed verification: {ex.Message}");
            return InstellaExitCode.InstallIntegrityFailed;
        }
        catch (Exception ex)
        {
            _log.Error($"install: {ex.Message}", ex);
            return InstellaExitCode.InstallGeneralFailure;
        }
        finally
        {
            payload?.Dispose();
            if (tempDownloadPath is not null && File.Exists(tempDownloadPath))
            {
                try { File.Delete(tempDownloadPath); } catch { /* best-effort */ }
            }
        }
    }

    private static Stream? OpenEmbeddedPayload()
    {
        try
        {
            return EmbeddedResources.OpenAppendedArchive();
        }
        catch (Instella.Core.Internal.FooterIntegrityException)
        {
            throw; // a damaged installer is fatal (exit 12), never a reason to download
        }
        catch (IOException)
        {
            return null;
        }
    }

}
