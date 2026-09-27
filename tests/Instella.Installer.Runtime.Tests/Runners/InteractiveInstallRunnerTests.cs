using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.FileSystem;
using Instella.Core.Installation;
using Instella.Core.Logging;
using Instella.Core.Platform;
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Runtime.Installation;
using Instella.Installer.Runtime.Runners;
using Instella.Installer.Runtime.UI.Widgets;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Runners;

/// <summary>
/// Exercises <see cref="InteractiveInstallRunner"/> without a real window.
/// Pure-logic cases pin the scope/elevation + install-path-resolver behaviour,
/// which must match the silent path; the fake-host cases drive the threading
/// orchestration (pipeline-runs-once, Back-lock-while-running, terminal-lock,
/// exit-code mapping) end to end via an injected <see cref="IInteractiveHost"/>.
/// </summary>
[TestFixture]
public sealed class InteractiveInstallRunnerTests
{
    private static FrozenConfig MinimalConfig(
        Instella.Core.Manifest.ElevationMode? elevation = null,
        Func<InstallContext, string>? installPathResolver = null)
    {
        var b = new InstallerBuilder();
        b.WithApp("Interactive Test", "com.example.interactive", new Version(1, 0, 0));
        if (elevation is { } e) b.WithElevation(e);
        if (installPathResolver is not null) b.WithInstallPath(installPathResolver);
        var installer = (InstellaInstallerImpl)b.Build();
        return installer.ConfigForTests;
    }

    private static InteractiveInstallRunner NewRunner(FrozenConfig config, InteractiveHostFactory? factory = null)
        => new(config, new NullLogger(), new NoOpPlatformServices(PlatformDetector.Current),
            RealFileSystem.Instance, factory);

    // ---- Pure-logic: scope / elevation -------------------------------------

    [Test]
    public void BuildOptions_carriesConfiguredElevation_andResolvesSystemWideScope()
    {
        var runner = NewRunner(MinimalConfig(Instella.Core.Manifest.ElevationMode.SystemWide));

        var options = runner.BuildOptionsFromState(new PageState(), @"C:\Program Files\App");

        Assert.That(options.Elevation, Is.EqualTo(Instella.Core.Manifest.ElevationMode.SystemWide));
        Assert.That(InstallContextFactory.ResolveScope(options), Is.EqualTo(InstallationScope.SystemWide));
    }

    [Test]
    public void BuildOptions_perUserElevation_resolvesPerUserScope()
    {
        var runner = NewRunner(MinimalConfig(Instella.Core.Manifest.ElevationMode.PerUser));

        var options = runner.BuildOptionsFromState(new PageState(), @"C:\Program Files\App");

        Assert.That(InstallContextFactory.ResolveScope(options), Is.EqualTo(InstallationScope.PerUser));
    }

    // ---- Pure-logic: install-path resolver ---------------------------------

    [Test]
    public void ResolveDefaultInstallPath_usesResolver_whenNoExplicitPath()
    {
        var runner = NewRunner(MinimalConfig(installPathResolver: _ => @"C:\Resolved\Path"));
        var dispatch = new DispatchResult(DispatchKind.Mode, InstallerMode.FirstInstall, null, IsSilent: false);

        Assert.That(runner.ResolveDefaultInstallPath(dispatch), Is.EqualTo(@"C:\Resolved\Path"));
    }

    [Test]
    public void ResolveDefaultInstallPath_explicitPathWins_overResolver()
    {
        var runner = NewRunner(MinimalConfig(installPathResolver: _ => @"C:\Resolved\Path"));
        var dispatch = new DispatchResult(DispatchKind.Mode, InstallerMode.FirstInstall, @"C:\Explicit", IsSilent: false);

        Assert.That(runner.ResolveDefaultInstallPath(dispatch), Is.EqualTo(@"C:\Explicit"));
    }

    // ---- Pure-logic: chosen path + exit-code mapping -----------------------

    [Test]
    public void ResolveChosenInstallPath_prefersStateValue_fallsBackWhenEmpty()
    {
        var state = new PageState();
        state.Set(InteractivePageStateKeys.InstallPath, @"C:\User\Picked");
        Assert.That(InteractiveInstallRunner.ResolveChosenInstallPath(state, @"C:\Fallback"), Is.EqualTo(@"C:\User\Picked"));
        Assert.That(InteractiveInstallRunner.ResolveChosenInstallPath(new PageState(), @"C:\Fallback"), Is.EqualTo(@"C:\Fallback"));
    }

    [Test]
    public void MapExitCode_coversAllOutcomes()
    {
        Assert.Multiple(() =>
        {
            Assert.That(InteractiveInstallRunner.MapExitCode(
                InteractiveHostOutcome.Cancelled, InteractiveInstallRunner.PipelineOutcome.NotStarted, null),
                Is.EqualTo(InstellaExitCode.UserCancelled));
            Assert.That(InteractiveInstallRunner.MapExitCode(
                InteractiveHostOutcome.Completed, InteractiveInstallRunner.PipelineOutcome.Succeeded, null),
                Is.EqualTo(InstellaExitCode.Success));
            Assert.That(InteractiveInstallRunner.MapExitCode(
                InteractiveHostOutcome.Completed, InteractiveInstallRunner.PipelineOutcome.Failed, "boom"),
                Is.EqualTo(InstellaExitCode.InstallGeneralFailure));
            Assert.That(InteractiveInstallRunner.MapExitCode(
                InteractiveHostOutcome.Completed, InteractiveInstallRunner.PipelineOutcome.Cancelled, null),
                Is.EqualTo(InstellaExitCode.UserCancelled));
        });
    }

    // ---- Orchestration via fake host ---------------------------------------

    [Test]
    public async Task Run_hostCancelsBeforeProgress_returnsUserCancelled_pipelineNeverStarts()
    {
        FakeHost? captured = null;
        InteractiveHostFactory factory = (title, pages, states) =>
            captured = new FakeHost(states, FindProgress(pages), InteractiveHostOutcome.Cancelled, advanceToProgress: false);
        var runner = NewRunner(MinimalConfig(), factory);
        var dispatch = new DispatchResult(DispatchKind.Mode, InstallerMode.FirstInstall, @"C:\Temp\App", IsSilent: false);

        var exit = await runner.RunAsync(dispatch, CancellationToken.None);

        Assert.That(exit, Is.EqualTo(InstellaExitCode.UserCancelled));
        Assert.That(captured!.BackLockCount, Is.EqualTo(0), "pipeline never started, so Back was never locked");
    }

    [Test]
    public async Task Run_reachesProgress_noPayload_failsGracefully_andRunsPipelineExactlyOnce()
    {
        // MinimalConfig has no embedded payload and no server, so the pipeline
        // fails fast with a clear error — enough to drive the completion handler,
        // the navigation locks, and the exit-code mapping without a real install.
        FakeHost? captured = null;
        InteractiveHostFactory factory = (title, pages, states) =>
            captured = new FakeHost(states, FindProgress(pages), InteractiveHostOutcome.Completed, advanceToProgress: true);
        var runner = NewRunner(MinimalConfig(), factory);
        var dispatch = new DispatchResult(DispatchKind.Mode, InstallerMode.FirstInstall, @"C:\Temp\App", IsSilent: false);

        var exit = await runner.RunAsync(dispatch, CancellationToken.None);

        Assert.That(exit, Is.EqualTo(InstellaExitCode.InstallGeneralFailure));
        // The fake raises PageChanged for the Progress page twice; the runner's
        // pipelineTask null-check must start the pipeline (and lock Back) once.
        Assert.That(captured!.BackLockCount, Is.EqualTo(1), "pipeline must start exactly once");
        Assert.That(captured.NavigationLocked, Is.True, "navigation is locked at terminal state");
    }

    // ---- Early checks: an older installer over a newer installation ---------

    [Test]
    public async Task NewerVersionInstalled_IsReportedOnTheFirstWindow_NotAfterTheWizard()
    {
        // Hand test: the 1.2.0 installer over 1.4.0 walked through the whole wizard and only
        // failed on the Progress page ("A newer version (1.4.0) is already installed").
        var dir = TempDir();
        await WriteInstalledManifestAsync(dir, new Version(2, 0, 0));
        var windows = new List<IReadOnlyList<PageSpec>>();
        InteractiveHostFactory factory = (title, pages, states) =>
        {
            windows.Add(pages);
            return new FakeHost(states, FindProgress(pages), InteractiveHostOutcome.Completed, advanceToProgress: false);
        };
        var runner = NewRunner(MinimalConfig(), factory);
        var dispatch = new DispatchResult(DispatchKind.Mode, InstallerMode.FirstInstall, dir, IsSilent: false);

        var exit = await runner.RunAsync(dispatch, CancellationToken.None);

        Assert.That(exit, Is.EqualTo(InstellaExitCode.UsageInvalidArgs));
        Assert.That(windows, Has.Count.EqualTo(1));
        Assert.That(windows[0], Has.Count.EqualTo(1), "one page saying why, not the wizard");
        Assert.That(FindProgress(windows[0]), Is.EqualTo(-1));
        Assert.That(PageText(windows[0][0]), Does.Contain("2.0.0").And.Contain("1.0.0"));
        // Hand test: the reason (with its path and --allow-downgrade) could not be copied.
        Assert.That(windows[0][0].Widgets.OfType<ScrollableText>().Single().Text, Does.Contain("--allow-downgrade"),
            "the reason is in a read-only text box, so it can be selected and copied");
    }

    [Test]
    public async Task OptionsPage_RefusesAFolderHoldingANewerInstallation()
    {
        var empty = TempDir();
        var newer = TempDir();
        await WriteInstalledManifestAsync(newer, new Version(2, 0, 0));
        string? refusal = null;
        InteractiveHostFactory factory = (title, pages, states) =>
        {
            var options = FindPage(pages, InteractivePageIds.Options);
            return new FakeHost(states, FindProgress(pages), InteractiveHostOutcome.Cancelled, advanceToProgress: false)
            {
                OnRun = host =>
                {
                    states[options].Set(InteractivePageStateKeys.InstallPath, newer);
                    refusal = host.BeforeLeave?.Invoke(options);
                },
            };
        };
        var runner = NewRunner(MinimalConfig(), factory);
        var dispatch = new DispatchResult(DispatchKind.Mode, InstallerMode.FirstInstall, empty, IsSilent: false);

        await runner.RunAsync(dispatch, CancellationToken.None);

        Assert.That(refusal, Does.Contain("2.0.0"), "leaving the options page with that folder is blocked");
    }

    [TestCase("apps\\QuickNotes")]
    [TestCase("")]
    public async Task OptionsPage_BlocksAFolderThatIsNotAFullPath(string typed)
    {
        string? refusal = null;
        InteractiveHostFactory factory = (title, pages, states) =>
        {
            var options = FindPage(pages, InteractivePageIds.Options);
            return new FakeHost(states, FindProgress(pages), InteractiveHostOutcome.Cancelled, advanceToProgress: false)
            {
                OnRun = host =>
                {
                    states[options].Set(InteractivePageStateKeys.InstallPath, typed.Length == 0 ? "   " : typed);
                    refusal = host.BeforeLeave?.Invoke(options);
                },
            };
        };
        var dispatch = new DispatchResult(DispatchKind.Mode, InstallerMode.FirstInstall, TempDir(), IsSilent: false);

        await NewRunner(MinimalConfig(), factory).RunAsync(dispatch, CancellationToken.None);

        Assert.That(refusal, Does.StartWith("Enter a full folder path, for example"));
    }

    [Test]
    public async Task OptionsPage_ForAnExistingInstallation_BlocksAnotherFolder()
    {
        // The existing installation is upgraded in place, never copied somewhere else.
        var existing = TempDir();
        await WriteInstalledManifestAsync(existing, new Version(0, 9, 0));
        var elsewhere = TempDir();
        string? refusal = null, same = null;
        InteractiveHostFactory factory = (title, pages, states) =>
        {
            var options = FindPage(pages, InteractivePageIds.Options);
            return new FakeHost(states, FindProgress(pages), InteractiveHostOutcome.Cancelled, advanceToProgress: false)
            {
                OnRun = host =>
                {
                    states[options].Set(InteractivePageStateKeys.InstallPath, elsewhere);
                    refusal = host.BeforeLeave?.Invoke(options);
                    states[options].Set(InteractivePageStateKeys.InstallPath, existing + "\\");
                    same = host.BeforeLeave?.Invoke(options);
                },
            };
        };
        var dispatch = new DispatchResult(DispatchKind.Mode, InstallerMode.FirstInstall, existing, IsSilent: false,
            Existing: new ExistingInstallation(existing, InstallationScope.PerUser, new Version(0, 9, 0), "HKCU"));

        await NewRunner(MinimalConfig(), factory).RunAsync(dispatch, CancellationToken.None);

        Assert.That(refusal, Does.Contain("is already installed in").And.Contain("Uninstall it first"));
        Assert.That(same, Is.Null, "the same folder, however spelled, is fine");
    }

    [Test]
    public async Task WithInstallPath_Throwing_Exits10_WithoutACrash()
    {
        var config = MinimalConfig(installPathResolver: _ => throw new InvalidOperationException("no drive D:"));
        InteractiveHostFactory factory = (title, pages, states) =>
            new FakeHost(states, FindProgress(pages), InteractiveHostOutcome.Cancelled, advanceToProgress: false);

        var exit = await NewRunner(config, factory).RunAsync(
            new DispatchResult(DispatchKind.Mode, InstallerMode.FirstInstall, null, IsSilent: false), CancellationToken.None);

        Assert.That(exit, Is.EqualTo(InstellaExitCode.InstallGeneralFailure));
    }

    // ---- the pages follow the mode the install will run in -----------------------

    private static FrozenConfig ConfigWithModePages()
    {
        var b = new InstallerBuilder();
        b.WithApp("Interactive Test", "com.example.interactive", new Version(1, 0, 0));
        b.AddPage("license", Pages.License("terms"));
        b.AddPage("whats-new", p => p.Heading("What's new").InModes(InstallerMode.Upgrade));
        b.AddPage("repair-note", p => p.Heading("Repair").InModes(InstallerMode.Repair));
        return ((InstellaInstallerImpl)b.Build()).ConfigForTests;
    }

    private static async Task<List<string>> ShownPageIdsAsync(string dir)
    {
        var shown = new List<string>();
        InteractiveHostFactory factory = (title, pages, states) =>
        {
            shown.AddRange(pages.Select(p => p.Id));
            return new FakeHost(states, FindProgress(pages), InteractiveHostOutcome.Cancelled, advanceToProgress: false);
        };
        await NewRunner(ConfigWithModePages(), factory).RunAsync(
            new DispatchResult(DispatchKind.Mode, InstallerMode.FirstInstall, dir, IsSilent: false), CancellationToken.None);
        return shown;
    }

    [Test]
    public async Task OverAnOlderInstall_TheUpgradePagesShow_AndTheLicenceDoesNot()
    {
        var dir = TempDir();
        await WriteInstalledManifestAsync(dir, new Version(0, 9, 0));

        var shown = await ShownPageIdsAsync(dir);

        Assert.That(shown, Does.Contain("whats-new"));
        Assert.That(shown, Does.Not.Contain("license").And.Not.Contain("repair-note"));
    }

    [Test]
    public async Task OverTheSameVersion_TheRepairPagesShow()
    {
        var dir = TempDir();
        await WriteInstalledManifestAsync(dir, new Version(1, 0, 0));

        var shown = await ShownPageIdsAsync(dir);

        Assert.That(shown, Does.Contain("repair-note"));
        Assert.That(shown, Does.Not.Contain("license").And.Not.Contain("whats-new"));
    }

    [Test]
    public async Task InAnEmptyFolder_TheFirstInstallPagesShow()
    {
        var shown = await ShownPageIdsAsync(TempDir());

        Assert.That(shown, Does.Contain("license"));
        Assert.That(shown, Does.Not.Contain("whats-new").And.Not.Contain("repair-note"));
    }

    [Test]
    public async Task PageHooks_SeeThePredictedMode()
    {
        var dir = TempDir();
        await WriteInstalledManifestAsync(dir, new Version(0, 9, 0));
        InstallerMode? seen = null;
        var b = new InstallerBuilder();
        b.WithApp("Interactive Test", "com.example.interactive", new Version(1, 0, 0));
        b.AddPage("probe", p => p.Heading("x")
            .InModes(InstallerMode.FirstInstall, InstallerMode.Upgrade, InstallerMode.Repair)
            .When(ctx => { seen = ctx.Mode; return true; }));
        InteractiveHostFactory factory = (title, pages, states) =>
            new FakeHost(states, FindProgress(pages), InteractiveHostOutcome.Cancelled, advanceToProgress: false);

        await NewRunner(((InstellaInstallerImpl)b.Build()).ConfigForTests, factory).RunAsync(
            new DispatchResult(DispatchKind.Mode, InstallerMode.FirstInstall, dir, IsSilent: false), CancellationToken.None);

        Assert.That(seen, Is.EqualTo(InstallerMode.Upgrade));
    }

    [Test]
    public async Task OptionsPage_BlocksAFolderHoldingAnUnregisteredCopy()
    {
        var empty = TempDir();
        var copy = TempDir();
        await WriteInstalledManifestAsync(copy, new Version(0, 9, 0));
        string? refusal = null, fresh = null;
        InteractiveHostFactory factory = (title, pages, states) =>
        {
            var options = FindPage(pages, InteractivePageIds.Options);
            return new FakeHost(states, FindProgress(pages), InteractiveHostOutcome.Cancelled, advanceToProgress: false)
            {
                OnRun = host =>
                {
                    states[options].Set(InteractivePageStateKeys.InstallPath, copy);
                    refusal = host.BeforeLeave?.Invoke(options);
                    states[options].Set(InteractivePageStateKeys.InstallPath, System.IO.Path.Combine(empty, "sub"));
                    fresh = host.BeforeLeave?.Invoke(options);
                },
            };
        };
        var dispatch = new DispatchResult(DispatchKind.Mode, InstallerMode.FirstInstall, empty, IsSilent: false);

        await NewRunner(MinimalConfig(), factory).RunAsync(dispatch, CancellationToken.None);

        Assert.That(refusal, Is.EqualTo(
            "Interactive Test is already installed in that folder. Choose another folder, or run the uninstaller in it."));
        Assert.That(fresh, Is.Null, "another empty folder keeps the first-install mode");
    }

    [Test]
    public async Task OptionsPage_OverAnUnregisteredInstall_BlocksMovingAway()
    {
        // --path at a copy with no registry entry: the Upgrade pages are shown, so an empty
        // folder (a first install) can't be chosen instead.
        var copy = TempDir();
        await WriteInstalledManifestAsync(copy, new Version(0, 9, 0));
        string? refusal = null;
        InteractiveHostFactory factory = (title, pages, states) =>
        {
            var options = FindPage(pages, InteractivePageIds.Options);
            return new FakeHost(states, FindProgress(pages), InteractiveHostOutcome.Cancelled, advanceToProgress: false)
            {
                OnRun = host =>
                {
                    states[options].Set(InteractivePageStateKeys.InstallPath, TempDir());
                    refusal = host.BeforeLeave?.Invoke(options);
                },
            };
        };

        await NewRunner(MinimalConfig(), factory).RunAsync(
            new DispatchResult(DispatchKind.Mode, InstallerMode.FirstInstall, copy, IsSilent: false), CancellationToken.None);

        Assert.That(refusal, Does.Contain("is already installed in " + copy));
    }

    [Test]
    public async Task PredictMode_FollowsTheResolverTable_AndChangesNothing()
    {
        var config = MinimalConfig();
        var dispatch = new DispatchResult(DispatchKind.Mode, InstallerMode.FirstInstall, null, IsSilent: false);
        var missing = System.IO.Path.Combine(TempDir(), "missing");
        var leftovers = TempDir();
        var tombstone = System.IO.Path.Combine(leftovers, ".instella", "tombstone");
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(tombstone)!);
        System.IO.File.WriteAllText(tombstone, "x");
        var older = TempDir();
        await WriteInstalledManifestAsync(older, new Version(0, 9, 0));
        var same = TempDir();
        await WriteInstalledManifestAsync(same, new Version(1, 0, 0));
        var newer = TempDir();
        await WriteInstalledManifestAsync(newer, new Version(2, 0, 0));
        var unrelated = TempDir();
        System.IO.File.WriteAllText(System.IO.Path.Combine(unrelated, "notes.txt"), "x");

        async Task<InstallerMode?> Predict(string path, DispatchResult d) =>
            (await InstallModeResolver.PredictModeAsync(config, RealFileSystem.Instance, path, d, CancellationToken.None))?.Mode;

        Assert.That(await Predict(missing, dispatch), Is.EqualTo(InstallerMode.FirstInstall));
        Assert.That(await Predict(leftovers, dispatch), Is.EqualTo(InstallerMode.FirstInstall));
        Assert.That(await Predict(older, dispatch), Is.EqualTo(InstallerMode.Upgrade));
        Assert.That(await Predict(same, dispatch), Is.EqualTo(InstallerMode.Repair));
        Assert.That(await Predict(newer, dispatch), Is.Null);
        Assert.That(await Predict(newer, dispatch with { AllowDowngrade = true }), Is.EqualTo(InstallerMode.Upgrade));
        Assert.That(await Predict(unrelated, dispatch), Is.Null);
        Assert.That(await Predict(unrelated, dispatch with { Force = true }), Is.EqualTo(InstallerMode.FirstInstall));
        Assert.That(System.IO.File.Exists(tombstone), "a prediction deletes nothing");
    }

    // ---- a lite installer's download failures exit like a silent one -------------------

    [Test]
    public async Task LiteDownload_NetworkFailure_Exits21()
    {
        var exit = await RunLiteAsync(_ => throw new System.Net.Http.HttpRequestException("connection refused"));

        Assert.That(exit, Is.EqualTo(InstellaExitCode.UpdateServerUnreachable));
    }

    [Test]
    public async Task LiteDownload_TrustFailure_Exits12()
    {
        // The server answers the signed-release request with nothing to verify.
        var exit = await RunLiteAsync(_ => new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new System.Net.Http.StringContent("null", System.Text.Encoding.UTF8, "application/json"),
        });

        Assert.That(exit, Is.EqualTo(InstellaExitCode.InstallIntegrityFailed));
    }

    private static async Task<InstellaExitCode> RunLiteAsync(Func<System.Net.Http.HttpRequestMessage, System.Net.Http.HttpResponseMessage> respond)
    {
        using var key = Instella.Core.Trust.ReleaseKeys.Generate();
        var b = new InstallerBuilder();
        b.WithApp("Interactive Test", "com.example.interactive", new Version(1, 0, 0))
            .WithServer("https://updates.example.com")
            .WithPublisherKey(Instella.Core.Trust.ReleaseKeys.PublicKeyOf(key).PublicKey);
        var config = ((InstellaInstallerImpl)b.Build()).ConfigForTests;
        InteractiveHostFactory factory = (title, pages, states) =>
            new FakeHost(states, FindProgress(pages), InteractiveHostOutcome.Completed, advanceToProgress: true);
        var runner = new InteractiveInstallRunner(config, new NullLogger(), new NoOpPlatformServices(PlatformDetector.Current),
            RealFileSystem.Instance, factory) { HttpHandler = new DelegateHandler(respond) };

        return await runner.RunAsync(
            new DispatchResult(DispatchKind.Mode, InstallerMode.FirstInstall, TempDir(), IsSilent: false), CancellationToken.None);
    }

    private sealed class DelegateHandler(Func<System.Net.Http.HttpRequestMessage, System.Net.Http.HttpResponseMessage> respond)
        : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }

    [Test]
    public async Task LaunchAfterFinish_GoesThroughTheAppLauncher()
    {
        var dir = TempDir();
        await WriteInstalledManifestAsync(dir, new Version(1, 0, 0));
        var launcher = new RecordingLauncher();
        var runner = new InteractiveInstallRunner(MinimalConfig(), new NullLogger(), new NoOpPlatformServices(PlatformDetector.Current),
            RealFileSystem.Instance, null) { AppLauncher = launcher };

        await runner.LaunchInstalledAppAsync(dir, CancellationToken.None);

        Assert.That(launcher.Launches, Is.EqualTo(new[] { (System.IO.Path.Combine(dir, "App.exe"), dir) }));
    }

    private sealed class RecordingLauncher : Instella.Installer.Runtime.Core.Processes.IAppLauncher
    {
        public List<(string Exe, string Dir)> Launches { get; } = [];
        public void Launch(string exePath, string workingDirectory) => Launches.Add((exePath, workingDirectory));
    }

    private static string TempDir()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "instella-interactive-tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        return dir;
    }

    private static Task WriteInstalledManifestAsync(string dir, Version version) =>
        new InstallManifestWriter(RealFileSystem.Instance).WriteAsync(dir, new InstalledManifest
        {
            AppName = "Interactive Test", AppId = "com.example.interactive", Version = version, InstallDirectory = dir,
            ExecutableName = "App.exe", InstalledAt = DateTime.UtcNow, Files = [],
        }, CancellationToken.None);

    private static string PageText(PageSpec page) => string.Join("\n", System.Linq.Enumerable.Select(page.Widgets, w => w switch
    {
        Heading h => h.Text,
        Paragraph p => p.Text,
        ScrollableText s => s.Text,
        _ => "",
    }));

    private static int FindPage(IReadOnlyList<PageSpec> pages, string id)
    {
        for (int i = 0; i < pages.Count; i++)
            if (pages[i].Id == id) return i;
        return -1;
    }

    private static int FindProgress(IReadOnlyList<PageSpec> pages)
    {
        for (int i = 0; i < pages.Count; i++)
            if (pages[i].Id == InteractivePageIds.Progress) return i;
        return -1;
    }

    /// <summary>
    /// Test double for <see cref="IInteractiveHost"/> that emulates the message
    /// loop: it raises <see cref="PageChanged"/> for the Progress page (when
    /// configured to advance), then drains posted actions until the pipeline
    /// signals <c>CanFinish</c>, mirroring how the real host's WndProc drains
    /// <c>PostToUiThread</c> work.
    /// </summary>
    private sealed class FakeHost : IInteractiveHost
    {
        private readonly IReadOnlyList<PageState> _states;
        private readonly int _progressIndex;
        private readonly InteractiveHostOutcome _outcome;
        private readonly bool _advanceToProgress;
        private readonly Queue<Action> _posted = new();
        private readonly object _gate = new();

        public bool NavigationLocked;
        public int BackLockCount;

        public FakeHost(IReadOnlyList<PageState> states, int progressIndex, InteractiveHostOutcome outcome, bool advanceToProgress)
        {
            _states = states;
            _progressIndex = progressIndex;
            _outcome = outcome;
            _advanceToProgress = advanceToProgress;
        }

        public event Action<int>? PageChanged;

        public void PostToUiThread(Action action)
        {
            lock (_gate) _posted.Enqueue(action);
        }

        public void LockNavigation() => NavigationLocked = true;
        public void LockBackNavigation() => BackLockCount++;
        public Func<bool>? CancelInterceptor { get; set; }
        public Func<int, string?>? BeforeLeave { get; set; }
        public int CloseCount;
        public void Close() => CloseCount++;

        /// <summary>Runs first inside <see cref="Run"/>, as if the user acted on the open window.</summary>
        public Action<FakeHost>? OnRun { get; init; }

        public InteractiveHostOutcome Run()
        {
            OnRun?.Invoke(this);
            if (_advanceToProgress)
            {
                // Raise PageChanged for the Progress page twice; the runner's
                // pipelineTask null-check must still start the pipeline once.
                PageChanged?.Invoke(_progressIndex);
                PageChanged?.Invoke(_progressIndex);

                // Drain posted work until the pipeline reports CanFinish (or give up).
                for (int i = 0; i < 600; i++)
                {
                    Drain();
                    if (_states[_progressIndex].Bool(InteractivePageStateKeys.CanFinish)) break;
                    Thread.Sleep(5);
                }
                Drain();
            }
            return _outcome;
        }

        private void Drain()
        {
            while (true)
            {
                Action? action;
                lock (_gate)
                {
                    if (_posted.Count == 0) return;
                    action = _posted.Dequeue();
                }
                action();
            }
        }

        public void Dispose() { }
    }

    private sealed class NullLogger : IInstellaLogger
    {
        public bool IsEnabled(InstellaLogLevel level) => true;
        public void Trace(string m) { }
        public void Debug(string m) { }
        public void Info(string m) { }
        public void Warn(string m) { }
        public void Error(string m, Exception? e = null) { }
        public IDisposable Scope(string s) => NullScope.Instance;
        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
