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
using Instella.Core.Platform;
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Runtime.Core;
using Instella.Installer.Runtime.Core.Processes;
using Instella.Installer.Runtime.Installation;
using Instella.Installer.Runtime.Installation.BuiltIn;
using Instella.Installer.Runtime.UI.Widgets;
using Instella.Installer.Runtime.UI.Windows.Widgets;

namespace Instella.Installer.Runtime.Runners;

/// <summary>Outcome of running an interactive widget host — platform-neutral.</summary>
internal enum InteractiveHostOutcome
{
    /// <summary>User reached and confirmed the final page (Finish).</summary>
    Completed,
    /// <summary>User cancelled or closed the window.</summary>
    Cancelled,
}

/// <summary>
/// Abstraction over the native widget host that <see cref="InteractiveInstallRunner"/>
/// drives. Production wraps <see cref="Win32WidgetHost"/>; tests inject a fake so the
/// runner's orchestration (pipeline-runs-once, progress marshaling, completion +
/// exit-code mapping) can be exercised on any host without a real window.
/// </summary>
internal interface IInteractiveHost : IDisposable
{
    /// <summary>Fires after the host navigates to a new page (receives the new index).</summary>
    event Action<int>? PageChanged;
    /// <summary>Marshal <paramref name="action"/> onto the host's UI thread.</summary>
    void PostToUiThread(Action action);
    /// <summary>Disable Back + Cancel (terminal install state — Finish only).</summary>
    void LockNavigation();
    /// <summary>Disable Back only, leaving Cancel enabled (install in progress).</summary>
    void LockBackNavigation();
    /// <summary>Show the window and run its message loop until completed/cancelled.</summary>
    InteractiveHostOutcome Run();
    /// <summary>
    /// Consulted on Cancel / window close; returning true keeps the window open (the
    /// runner is rolling back and closes it with <see cref="Close"/> when done).
    /// </summary>
    Func<bool>? CancelInterceptor { set; }
    /// <summary>Consulted before leaving a page through Next; a non-null message blocks.</summary>
    Func<int, string?>? BeforeLeave { set; }
    /// <summary>Close the window from any thread.</summary>
    void Close();
}

/// <summary>Creates an <see cref="IInteractiveHost"/> for the given pages.</summary>
internal delegate IInteractiveHost InteractiveHostFactory(
    string title, IReadOnlyList<PageSpec> pages, IReadOnlyList<PageState> states);

/// <summary>
/// Drives the interactive (non-silent) first-install / upgrade / repair
/// path by launching an <see cref="IInteractiveHost"/>, collecting user
/// input on the synthetic Options page, then running the standard step
/// pipeline with progress plumbed into the Progress page's
/// <see cref="Progress"/> + <see cref="StatusLine"/> widgets. Cancel +
/// rollback + error display live on the same Progress page — successful
/// installs unlock the "Finish" button; failures also unlock it so the
/// user can read the error in the status line and close the window.
/// </summary>
/// <remarks>
/// <para>Windows-only: the default host factory is a <see cref="Win32WidgetHost"/>
/// wrapper. The GTK and Cocoa page panels lack the state-reactive
/// <see cref="Progress"/> / <see cref="StatusLine"/> wiring that
/// <see cref="Win32PagePanel"/> has, so other platforms install with --silent.</para>
/// <para>Thread model: the message loop owns the UI thread. The step
/// pipeline runs on a <c>Task.Run</c> background thread. All
/// <see cref="PageState"/> writes are marshalled back to the UI thread via
/// <see cref="IInteractiveHost.PostToUiThread"/> so
/// <see cref="PageState.StateChanged"/> handlers that call Win32 APIs run
/// where the HWNDs expect.</para>
/// </remarks>
internal sealed class InteractiveInstallRunner
{
    private readonly FrozenConfig _config;
    private readonly IInstellaLogger _log;
    private readonly IPlatformServices _platform;
    private readonly IFileSystem _fileSystem;
    // null = use the default Win32 host (Windows-only). Tests inject a fake.
    private readonly InteractiveHostFactory? _hostFactory;
    private readonly AppRunningPrompt _appRunningPrompt;
    // The install scope: the configured one until RunAsync takes the resolved one
    // from the dispatch.
    private InstallationScope _scope;

    public InteractiveInstallRunner(
        FrozenConfig config,
        IInstellaLogger log,
        IPlatformServices platform,
        IFileSystem fileSystem)
        : this(config, log, platform, fileSystem, hostFactory: null)
    {
    }

    internal InteractiveInstallRunner(
        FrozenConfig config,
        IInstellaLogger log,
        IPlatformServices platform,
        IFileSystem fileSystem,
        InteractiveHostFactory? hostFactory,
        AppRunningPrompt? appRunningPrompt = null)
    {
        _config = config;
        _log = log;
        _platform = platform;
        _fileSystem = fileSystem;
        _hostFactory = hostFactory;
        _appRunningPrompt = appRunningPrompt ?? RunningAppGate.MessageBoxPrompt;
        _scope = config.Elevation == Instella.Core.Manifest.ElevationMode.SystemWide
            ? InstallationScope.SystemWide
            : InstallationScope.PerUser;
    }

    public Task<InstellaExitCode> RunAsync(DispatchResult dispatch, CancellationToken ct)
    {
        _scope = dispatch.Scope ?? _scope;
        // Injected (test) factory bypasses the OS gate — the fake host is
        // host-platform-agnostic.
        if (_hostFactory is { } injected)
            return RunInteractiveAsync(dispatch, injected, ct);

        if (!OperatingSystem.IsWindows())
        {
            _log.Error("interactive install UI is currently Windows-only; pass --silent --path <dir> for a headless install on other platforms.");
            return Task.FromResult(InstellaExitCode.UsageUnknownMode);
        }

        return RunInteractiveAsync(dispatch, CreateDefaultWin32Host, ct);
    }

    /// <summary>
    /// A one-page window saying why nothing will be installed; Close ends the installer. The
    /// reason is in a read-only text box so its path and flag can be selected and copied.
    /// </summary>
    private void ShowRefusal(InteractiveHostFactory hostFactory, string message)
    {
        var page = InteractivePages.BuildPage(InteractivePageIds.Refused, p => p
            .Heading($"{_config.AppName} {_config.AppVersion} cannot be installed")
            .ScrollableText(message)) with { ContinueLabel = "&Close" };
        using var host = hostFactory(_config.AppName, [page], [new PageState()]);
        host.Run();
    }

    internal static IInteractiveHost CreateDefaultWin32Host(
        string title, IReadOnlyList<PageSpec> pages, IReadOnlyList<PageState> states)
        => new Win32InteractiveHost(title, pages, states);

    private async Task<InstellaExitCode> RunInteractiveAsync(
        DispatchResult dispatch, InteractiveHostFactory hostFactory, CancellationToken ct)
    {
        string defaultInstallPath;
        try
        {
            defaultInstallPath = ResolveDefaultInstallPath(dispatch);
        }
        catch (InstallRefusedException ex)
        {
            // A configuration error in the installer (WithInstallPath): exit 10.
            _log.Error($"install: {ex.Message}");
            ShowRefusal(hostFactory, ex.Message);
            return ex.ExitCode;
        }

        // An older installer over a newer installation (or over another app) is refused on the
        // first window, not on the Progress page after the user has clicked through the wizard.
        if (await InstallModeResolver.PrecheckAsync(_config, _fileSystem, defaultInstallPath, dispatch, ct) is { } early)
        {
            _log.Error($"install: {early.Message}");
            ShowRefusal(hostFactory, early.Message);
            return early.ExitCode;
        }

        var cli = dispatch.CliOrEmpty;
        var pageStates = new Dictionary<string, PageState>(StringComparer.Ordinal);

        // The pages, their hooks and the Options defaults follow the mode the install will
        // really run in. The folder can't change it later: Options refuses a folder that would.
        // A folder that will be refused at install time keeps the dispatcher's mode.
        var predicted = await InstallModeResolver.PredictModeAsync(_config, _fileSystem, defaultInstallPath, dispatch, ct);
        var pageMode = predicted?.Mode ?? dispatch.Mode;
        // The installation being upgraded or repaired, if any: its choices are the Options defaults.
        var existingManifest = predicted?.Existing;

        // Page hooks (When / OnEnter / OnLeave) run against a context rooted at the default
        // path; the pipeline gets its own once the user has chosen the real one. Both share
        // the same page-state dictionary, so ctx.Pages is the final answers.
        var hookContext = InstallContextFactory.Create(
            _config, pageMode, defaultInstallPath,
            InstallModeResolver.FollowExisting(BuildDefaultOptions(defaultInstallPath), _config, existingManifest),
            _platform, _fileSystem, _log, existing: existingManifest, cli: cli, pages: pageStates);
        var pages = InteractivePages.BuildHappyPathPages(_config, pageMode, defaultInstallPath, dispatch.Existing, existingManifest)
            .Where(p => !IsUserPage(p) || p.When is not { } when
                        || UserCode.Run(() => when(hookContext), $"page '{p.Id}' When", _log, onError: true))
            .ToList();
        var states = BuildPageStates(pages, cli);
        for (var i = 0; i < pages.Count; i++)
            pageStates[pages[i].Id] = states[i];

        var progressPageIdx = FindProgressIndex(pages);
        if (progressPageIdx < 0)
            return InstellaExitCode.InstallGeneralFailure;

        using var pipelineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task<ExecutionResult>? pipelineTask = null;
        var pipelineOutcome = PipelineOutcome.NotStarted;
        string? pipelineError = null;
        InstellaExitCode? refusedExit = null;
        InstellaExitCode? failureExit = null;
        string? installedPath = null;
        var cancelRequested = false;

        var progressState = states[progressPageIdx];
        using var host = hostFactory(_config.AppName, pages, states);

        // User pages: OnEnter on arrival; OnValidate then OnLeave before moving on.
        host.PageChanged += pageIdx =>
        {
            if (IsUserPage(pages[pageIdx]) && pages[pageIdx].OnEnter is { } onEnter)
                RunHook(() => onEnter(hookContext, ct), $"page '{pages[pageIdx].Id}' OnEnter");
        };
        host.BeforeLeave = pageIdx =>
        {
            var page = pages[pageIdx];
            // The same check for a folder picked on the Options page: a quick manifest read.
            if (page.Id == InteractivePageIds.Options)
            {
                var typed = ResolveChosenInstallPath(states[pageIdx], defaultInstallPath);
                if (!InstallPaths.TryNormalize(typed, requireRooted: true, out var chosen, out _))
                    return $@"Enter a full folder path, for example C:\Apps\{_config.AppName}.";
                // The existing installation is upgraded in place, never copied elsewhere.
                if (dispatch.Existing is { } existing && !InstallPaths.SameFolder(chosen, existing.Path))
                    return $"{_config.AppName} is already installed in {existing.Path}. Uninstall it first to install it somewhere else.";
                if (InstallModeResolver.PrecheckAsync(_config, _fileSystem, chosen, dispatch, ct)
                        .GetAwaiter().GetResult() is { } refused)
                    return refused.Message;
                // The pages shown were chosen for the default folder's mode; another folder
                // must not change it.
                if (!InstallPaths.SameFolder(chosen, defaultInstallPath))
                {
                    if (existingManifest is not null)
                        return $"{_config.AppName} is already installed in {defaultInstallPath}. Uninstall it first to install it somewhere else.";
                    if (InstallModeResolver.PredictModeAsync(_config, _fileSystem, chosen, dispatch, ct)
                            .GetAwaiter().GetResult() is { Existing: not null })
                        return $"{_config.AppName} is already installed in that folder. Choose another folder, or run the uninstaller in it.";
                }
            }
            if (!IsUserPage(page)) return null;
            if (PageFlow.BlockReason(page, states[pageIdx]) is { } reason) return reason;
            if (page.OnLeave is { } onLeave)
                RunHook(() => onLeave(hookContext, ct), $"page '{page.Id}' OnLeave");
            return null;
        };

        // Cancel during the install never abandons a half-done system: it cancels the
        // pipeline, shows "Rolling back…", and the window closes once rollback finishes.
        host.CancelInterceptor = () =>
        {
            if (pipelineTask is null || pipelineTask.IsCompleted) return false;
            if (!cancelRequested)
            {
                cancelRequested = true;
                pipelineCts.Cancel();
                host.LockNavigation();
                progressState.Set(InteractivePageStateKeys.Status, "Cancelling — rolling back changes…");
            }
            return true;
        };

        var optionsState = progressPageIdx - 1 >= 0 ? states[progressPageIdx - 1] : progressState;

        host.PageChanged += pageIdx =>
        {
            // User has just arrived on a new page. Fire the pipeline the first
            // time the Progress page becomes active; subsequent visits are
            // ignored via the pipelineTask null-check, so the pipeline runs
            // exactly once per install.
            if (pageIdx != progressPageIdx || pipelineTask is not null) return;

            // Back to the Options page mid-install is nonsensical (the pipeline
            // ignores it and the Progress bar would visually reset), so disable
            // Back now. Cancel stays enabled so the install can still be aborted.
            host.LockBackNavigation();

            var typedInstallPath = ResolveChosenInstallPath(optionsState, defaultInstallPath);
            // Leaving the Options page already required a full path (BeforeLeave).
            var chosenInstallPath = InstallPaths.TryNormalize(typedInstallPath, requireRooted: true, out var normalizedChoice, out _)
                ? normalizedChoice
                : typedInstallPath;
            installedPath = chosenInstallPath;
            var installOptions = BuildOptionsFromState(optionsState, chosenInstallPath);

            pipelineTask = Task.Run(async () =>
            {
                ExecutionResult result;
                // One Instella process per install folder, for the whole pipeline.
                Core.Transactions.InstallRootLock? held = null;
                try
                {
                    held = await Core.Transactions.InstallRootLock.AcquireOrReportAsync(
                        chosenInstallPath, silent: false, null, _config.AppName, _log, pipelineCts.Token);
                    if (held is null)
                        throw new InstallRefusedException(InstellaExitCode.InstallationBusy,
                            Core.Transactions.InstallRootLock.BusyMessage(_config.AppName));
                    var (mode, existing) = await InstallModeResolver.ResolveAsync(
                        _config, _fileSystem, chosenInstallPath, dispatch, _log, pipelineCts.Token);
                    if (existing is not null && !await EnsureAppClosedAsync(existing, chosenInstallPath, dispatch, pipelineCts.Token))
                        throw new InstallRefusedException(InstellaExitCode.UpdateAppCouldNotClose,
                            $"{existing.AppName} is still running. Close it and run the installer again.");

                    var context = InstallContextFactory.Create(
                        _config, mode, chosenInstallPath, installOptions,
                        _platform, _fileSystem, _log, existing: existing, cli: cli, pages: pageStates,
                        allowElevationPrompt: true);
                    // Migrations that close programs ask, as the upgrade gate does.
                    context.Migrations = new Migrations.MigrationRuntime
                    {
                        ProcessFinder = ProcessFinder ?? DefaultLockingProcessFinder.Instance,
                        Prompt = _appRunningPrompt,
                        ForceClose = dispatch.ForceClose,
                    };
                    result = await RunStepPipelineAsync(context, host, progressState, pipelineCts.Token);
                }
                catch (InstallRefusedException ex)
                {
                    _log.Error($"install: {ex.Message}");
                    refusedExit = ex.ExitCode;
                    result = ExecutionResult.Fail(ex.Message, Array.Empty<StepExecutionRecord>(), Array.Empty<string>());
                }
                catch (OperationCanceledException)
                {
                    result = ExecutionResult.Fail("Installation cancelled", Array.Empty<StepExecutionRecord>(), Array.Empty<string>());
                }
                catch (HttpRequestException ex)
                {
                    // The same exit codes as a silent lite install: 21 network, 12 trust.
                    _log.Error($"install: server unreachable: {ex.Message}");
                    refusedExit = InstellaExitCode.UpdateServerUnreachable;
                    result = ExecutionResult.Fail($"Could not reach the download server: {ex.Message}",
                        Array.Empty<StepExecutionRecord>(), Array.Empty<string>());
                }
                catch (Exception ex) when (ex is Instella.Core.Trust.UpdateTrustException or Instella.Core.Internal.FooterIntegrityException)
                {
                    _log.Error($"install: the payload failed verification: {ex.Message}");
                    refusedExit = InstellaExitCode.InstallIntegrityFailed;
                    result = ExecutionResult.Fail($"The download could not be verified: {ex.Message}",
                        Array.Empty<StepExecutionRecord>(), Array.Empty<string>());
                }
                catch (Exception ex)
                {
                    _log.Error($"install: {ex.Message}", ex);
                    result = ExecutionResult.Fail(ex.Message, Array.Empty<StepExecutionRecord>(), Array.Empty<string>());
                }
                finally
                {
                    if (held is not null) await held.DisposeAsync();
                }

                host.PostToUiThread(() =>
                {
                    ApplyPipelineCompletion(progressState, result, pipelineCts.IsCancellationRequested,
                        outcome => pipelineOutcome = outcome,
                        err => pipelineError = err, _stepDisplayNames, Logging.InstellaLogInitializer.CurrentFilePath);
                    if (cancelRequested)
                    {
                        // Rollback is done: nothing left for the user to read.
                        host.Close();
                        return;
                    }
                    // Install is terminal — lock Back/Cancel so the user can
                    // only exit via Finish. Continue is already state-gated
                    // by the Progress page's ContinueWhen reading CanFinish.
                    host.LockNavigation();
                });

                return result;
            }, pipelineCts.Token);
        };

        var outcome = host.Run();

        // If the user clicked Cancel / closed the window before the pipeline
        // completed, cancel the token and wait briefly for rollback to finish
        // so we don't leave background work orphaned. Whether the completion
        // handler ran on the UI thread or not, we re-derive the outcome from
        // the awaited pipeline result below (the queued completion handler
        // may never drain because the message loop ended).
        if (pipelineTask is not null)
        {
            if (!pipelineTask.IsCompleted)
            {
                // Rollback runs on its own bounded token (StepExecutor.RollbackTimeout);
                // wait for it rather than exiting with the system half-changed.
                pipelineCts.Cancel();
                try { await pipelineTask; }
                catch { /* the exit code below is derived from the task state */ }
            }
            if (pipelineTask.IsCompletedSuccessfully)
            {
                var result = pipelineTask.Result;
                if (pipelineCts.IsCancellationRequested)
                {
                    pipelineOutcome = PipelineOutcome.Cancelled;
                }
                else if (result.Success)
                {
                    pipelineOutcome = PipelineOutcome.Succeeded;
                }
                else
                {
                    pipelineOutcome = PipelineOutcome.Failed;
                    pipelineError = result.Error;
                    failureExit = InstallFailureExit.For(result);
                }
            }
            else if (pipelineTask.IsFaulted)
            {
                pipelineOutcome = PipelineOutcome.Failed;
                pipelineError = pipelineTask.Exception?.GetBaseException().Message;
            }
            else if (pipelineTask.IsCanceled)
            {
                pipelineOutcome = PipelineOutcome.Cancelled;
            }
        }

        if (refusedExit is { } refused && pipelineOutcome == PipelineOutcome.Failed)
            return refused;
        if (failureExit is { } failed && pipelineOutcome == PipelineOutcome.Failed)
            return failed;
        if (pipelineOutcome == PipelineOutcome.Succeeded && installedPath is not null
            && progressState.Bool(InteractivePageStateKeys.LaunchApp))
            await LaunchInstalledAppAsync(installedPath, ct);
        return MapExitCode(outcome, pipelineOutcome, pipelineError);
    }

    /// <summary>
    /// Starts the installed app after Finish when "Launch ..." is ticked, through
    /// <see cref="IAppLauncher"/>: an elevated installer (a machine-wide install) must not hand
    /// its administrator rights to the app. A failure is logged, never fatal: the install
    /// itself succeeded.
    /// </summary>
    internal async Task LaunchInstalledAppAsync(string installPath, CancellationToken ct)
    {
        try
        {
            var manifest = await new InstallManifestWriter(_fileSystem).ReadAsync(installPath, ct);
            if (manifest is null) return;
            (AppLauncher ?? new Core.Processes.AppLauncher(_log)).Launch(Path.Combine(installPath, manifest.ExecutableName), installPath);
        }
        catch (Exception ex)
        {
            _log.Warn($"install: could not launch the app: {ex.Message}");
        }
    }

    /// <summary>Test hook: starts the app after Finish; null uses <see cref="Core.Processes.AppLauncher"/>.</summary>
    internal IAppLauncher? AppLauncher { get; init; }

    /// <summary>
    /// Makes sure no program uses the files of the installation being upgraded or repaired:
    /// <c>--force-close</c> closes them straight away; otherwise the user chooses Retry,
    /// Close automatically, or Cancel.
    /// </summary>
    private Task<bool> EnsureAppClosedAsync(
        InstalledManifest existing, string installPath, DispatchResult dispatch, CancellationToken ct) =>
        new RunningAppGate(_platform, _log, ProcessFinder, _fileSystem).EnsureClosedAsync(
            existing.AppName, installPath, existing.ExecutableName, dispatch.ForceClose, _appRunningPrompt, ct);

    /// <summary>Test hook: finds processes using the app's files; null uses Restart Manager.</summary>
    internal ILockingProcessFinder? ProcessFinder { get; init; }

    /// <summary>Handler for a lite installer's download; null uses a default client.</summary>
    internal HttpMessageHandler? HttpHandler { get; init; }

    /// <summary>
    /// Resolve the install path used to seed the Options folder picker:
    /// an explicit <c>--path</c> wins; otherwise a configured
    /// <c>WithInstallPath</c> resolver supplies the suggested default
    /// (the user can still override it in the picker); otherwise the
    /// platform default. Honors <c>WithInstallPath</c> on the interactive
    /// path, which a prior version ignored.
    /// </summary>
    internal string ResolveDefaultInstallPath(DispatchResult dispatch)
    {
        if (!string.IsNullOrEmpty(dispatch.InstallPath))
            return dispatch.InstallPath!;

        var platformDefault = InstallPaths.Default(_config, _platform, _scope);

        if (_config.InstallPathResolver is { } resolver)
        {
            var provisional = InstallContextFactory.Create(
                _config, dispatch.Mode, platformDefault, BuildDefaultOptions(platformDefault),
                _platform, _fileSystem, _log);
            string? resolved;
            try
            {
                resolved = resolver(provisional);
            }
            catch (Exception ex)
            {
                throw new InstallRefusedException(InstellaExitCode.InstallGeneralFailure,
                    $"WithInstallPath threw: {ex.Message}");
            }
            if (!string.IsNullOrEmpty(resolved))
            {
                if (!InstallPaths.TryNormalize(resolved, requireRooted: false, out var normalized, out var problem))
                    throw new InstallRefusedException(InstellaExitCode.InstallGeneralFailure,
                        $"WithInstallPath returned an unusable folder: {problem}");
                return normalized;
            }
        }

        return platformDefault;
    }

    private List<PageState> BuildPageStates(IReadOnlyList<PageSpec> pages, CliArgs cli)
    {
        var list = new List<PageState>(pages.Count);
        foreach (var page in pages)
        {
            var s = new PageState();
            PageFlow.InitializeState(page, s, cli, _config.DeclaredCliFlags);
            list.Add(s);
        }
        return list;
    }

    private bool IsUserPage(PageSpec page) => _config.Pages.Contains(page);

    /// <summary>Runs a page hook on the UI thread; a throwing hook is logged, not fatal to the wizard.</summary>
    private void RunHook(Func<Task> hook, string what)
    {
        try { hook().GetAwaiter().GetResult(); }
        catch (Exception ex) { _log.Error($"{what} threw: {ex.Message}", ex); }
    }

    private static int FindProgressIndex(IReadOnlyList<PageSpec> pages)
    {
        for (int i = 0; i < pages.Count; i++)
            if (pages[i].Id == InteractivePageIds.Progress) return i;
        return -1;
    }

    internal static string ResolveChosenInstallPath(PageState optionsState, string fallback)
    {
        var text = optionsState.Text(InteractivePageStateKeys.InstallPath, fallback);
        return string.IsNullOrEmpty(text) ? fallback : text;
    }

    internal InstallOptions BuildOptionsFromState(PageState optionsState, string installPath)
    {
        var desktop = optionsState.Bool(InteractivePageStateKeys.ShortcutDesktop, _config.Shortcuts?.Desktop ?? false);
        var startMenu = optionsState.Bool(InteractivePageStateKeys.ShortcutStartMenu, _config.Shortcuts?.StartMenu ?? false);
        return BuildOptions(installPath, desktop, startMenu);
    }

    private InstallOptions BuildDefaultOptions(string installPath) =>
        BuildOptions(installPath, _config.Shortcuts?.Desktop ?? false, _config.Shortcuts?.StartMenu ?? false);

    private InstallOptions BuildOptions(string installPath, bool desktopShortcut, bool startMenuShortcut)
    {
        return new InstallOptions
        {
            InstallPath = installPath,
            CreateDesktopShortcut = desktopShortcut,
            CreateStartMenuShortcut = startMenuShortcut,
            AddToPath = _config.PathRegistration,
            ConfigureAutoStart = _config.AutoStart,
            RegisterFileAssociations = _config.FileAssociations.Count > 0,
            Elevation = InstallPaths.ElevationFor(_scope),
        };
    }

    private async Task<ExecutionResult> RunStepPipelineAsync(InstallContext context, IInteractiveHost host, PageState progressState, CancellationToken ct)
    {
        Stream? payload = null;
        string? tempDownloadPath = null;
        try
        {
            payload = OpenEmbeddedPayload();
            if (payload is null)
            {
                if (string.IsNullOrEmpty(_config.ServerUrl))
                    return ExecutionResult.Fail("no embedded payload and no WithServer(url) configured",
                        Array.Empty<StepExecutionRecord>(), Array.Empty<string>());
                tempDownloadPath = Path.Combine(Path.GetTempPath(), $"instella-download-{Guid.NewGuid():N}.zip");
                using var http = Instella.Core.Wire.ServerHttp.Create(new Uri(_config.ServerUrl), _config.DownloadToken,
                    "Instella-Installer/1.0", TimeSpan.FromMinutes(30), HttpHandler);
                payload = await ServerPayloadDownloader.DownloadAsync(_config, tempDownloadPath, _log, http, ct);
            }
            context.PayloadArchive = payload;

            var builtIns = new List<IInstallStepExecution>(OfflineInstallRunner.BuildDefaultSteps());
            if (_config.RegistryWrites.Count > 0)
                builtIns.Add(new WriteRegistrySpecsStep(_config.RegistryWrites));

            var steps = StepOrdering.BuildOrderedSteps(builtIns, _config.UserSteps, _config.MigrationsOrEmpty);
            _stepDisplayNames = StepDisplayNames.Map(steps);
            var progressSink = new HostProgressSink(host, progressState, _stepDisplayNames);
            var executor = new StepExecutor(steps);
            return await executor.ExecuteAsync(context, progressSink, ct);
        }
        finally
        {
            payload?.Dispose();
            if (tempDownloadPath is not null && File.Exists(tempDownloadPath))
            {
                try { File.Delete(tempDownloadPath); } catch { }
            }
        }
    }

    // Step name to the text the wizard shows; set once the pipeline has its steps.
    private IReadOnlyDictionary<string, string>? _stepDisplayNames;

    private static Stream? OpenEmbeddedPayload()
    {
        try { return EmbeddedResources.OpenAppendedArchive(); }
        catch (Instella.Core.Internal.FooterIntegrityException) { throw; }
        catch (IOException) { return null; }
    }

    /// <summary>
    /// Called on the UI thread after the pipeline task finishes. Writes the
    /// outcome into the Progress page's <see cref="PageState"/> so the
    /// status text reads correctly and the "Finish" button unlocks.
    /// </summary>
    internal static void ApplyPipelineCompletion(
        PageState progressState,
        ExecutionResult result,
        bool cancelled,
        Action<PipelineOutcome> setOutcome,
        Action<string?> setError,
        IReadOnlyDictionary<string, string>? stepDisplayNames = null,
        string? logFilePath = null)
    {
        if (cancelled)
        {
            progressState.Set(InteractivePageStateKeys.Status, "Installation cancelled — rolling back completed steps.");
            setOutcome(PipelineOutcome.Cancelled);
        }
        else if (result.Success)
        {
            progressState.Set(InteractivePageStateKeys.Progress, 1.0);
            progressState.Set(InteractivePageStateKeys.Status, "Installation complete. Click Finish to exit.");
            setOutcome(PipelineOutcome.Succeeded);
        }
        else
        {
            var msg = result.Error ?? "installation failed";
            progressState.Set(InteractivePageStateKeys.Status, "Installation failed.");
            progressState.Set(InteractivePageStateKeys.ErrorDetails, ErrorDetails(result, msg, stepDisplayNames, logFilePath));
            progressState.Set(InteractivePageStateKeys.Failed, true);
            setOutcome(PipelineOutcome.Failed);
            setError(msg);
        }

        progressState.Set(InteractivePageStateKeys.CanFinish, true);
    }

    /// <summary>The Progress page's error details: the failed step, why, the rollback, the log.</summary>
    internal static string ErrorDetails(
        ExecutionResult result, string error, IReadOnlyDictionary<string, string>? stepDisplayNames, string? logFilePath)
    {
        var lines = new List<string>();
        var failed = result.Steps.LastOrDefault(s => s.Outcome == StepOutcome.Failed);
        if (failed is not null)
        {
            var name = stepDisplayNames is not null && stepDisplayNames.TryGetValue(failed.Name, out var display) ? display : failed.Name;
            lines.Add($"{name} failed: {error}");
        }
        else
        {
            lines.Add(error);
        }
        lines.Add("");
        if (result.Warnings.Count == 0)
        {
            lines.Add(result.Steps.Count > 0 ? "Changes were rolled back." : "Nothing was changed.");
        }
        else
        {
            lines.Add("Changes were rolled back, with these problems:");
            lines.AddRange(result.Warnings.Select(w => $"- {w}"));
        }
        if (!string.IsNullOrEmpty(logFilePath))
        {
            lines.Add("");
            lines.Add($"Log: {logFilePath}");
        }
        return string.Join("\n", lines);
    }

    internal static InstellaExitCode MapExitCode(InteractiveHostOutcome hostOutcome, PipelineOutcome pipeline, string? error)
    {
        if (hostOutcome == InteractiveHostOutcome.Cancelled && pipeline == PipelineOutcome.NotStarted)
            return InstellaExitCode.UserCancelled;
        return pipeline switch
        {
            PipelineOutcome.Succeeded => InstellaExitCode.Success,
            PipelineOutcome.Cancelled => InstellaExitCode.UserCancelled,
            PipelineOutcome.Failed => InstellaExitCode.InstallGeneralFailure,
            PipelineOutcome.NotStarted => InstellaExitCode.UserCancelled,
            _ => InstellaExitCode.InstallGeneralFailure,
        };
    }

    internal enum PipelineOutcome
    {
        NotStarted,
        Succeeded,
        Failed,
        Cancelled,
    }

    /// <summary>
    /// Receives <see cref="OverallProgress"/> notifications from the step
    /// executor (on a background thread) and marshals PageState writes onto
    /// the UI thread via <see cref="IInteractiveHost.PostToUiThread"/>.
    /// Coalescing is implicit — we always set the most recent progress /
    /// status, so pending messages in the queue represent the freshest
    /// state regardless of how many arrived back-to-back.
    /// </summary>
    internal sealed class HostProgressSink : IProgress<OverallProgress>
    {
        private readonly IInteractiveHost _host;
        private readonly PageState _state;
        private readonly IReadOnlyDictionary<string, string> _displayNames;

        public HostProgressSink(IInteractiveHost host, PageState state, IReadOnlyDictionary<string, string> displayNames)
        {
            _host = host;
            _state = state;
            _displayNames = displayNames;
        }

        public void Report(OverallProgress value)
        {
            var status = Format(value, _displayNames);
            _host.PostToUiThread(() =>
            {
                _state.Set(InteractivePageStateKeys.Progress, value.Fraction);
                _state.Set(InteractivePageStateKeys.Status, status);
            });
        }

        /// <summary>"Copying files…", plus " (detail)" when the step reports one.</summary>
        internal static string Format(OverallProgress value, IReadOnlyDictionary<string, string> displayNames)
        {
            if (value.StepName is null) return "Working…";
            var name = displayNames.TryGetValue(value.StepName, out var display) ? display : value.StepName;
            return value.Status is null ? $"{name}…" : $"{name}… ({value.Status})";
        }
    }

    /// <summary>
    /// Production <see cref="IInteractiveHost"/> backed by a real
    /// <see cref="Win32WidgetHost"/>. Only constructed on Windows (the
    /// default-host branch of <see cref="RunAsync"/> gates on the OS).
    /// </summary>
    private sealed class Win32InteractiveHost : IInteractiveHost
    {
        private readonly Win32WidgetHost _host;

        public Win32InteractiveHost(string title, IReadOnlyList<PageSpec> pages, IReadOnlyList<PageState> states)
            => _host = new Win32WidgetHost(title, pages, states);

        public event Action<int>? PageChanged
        {
            add => _host.PageChanged += value;
            remove => _host.PageChanged -= value;
        }

        public void PostToUiThread(Action action) => _host.PostToUiThread(action);
        public void LockNavigation() => _host.LockNavigation();
        public void LockBackNavigation() => _host.LockBackNavigation();
        public Func<bool>? CancelInterceptor { set => _host.CancelInterceptor = value; }
        public Func<int, string?>? BeforeLeave { set => _host.BeforeLeave = value; }
        public void Close() => _host.Close();

        public InteractiveHostOutcome Run() =>
            _host.Run() == Win32WidgetHostOutcome.Completed
                ? InteractiveHostOutcome.Completed
                : InteractiveHostOutcome.Cancelled;

        public void Dispose() => _host.Dispose();
    }
}
