using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Installation;
using Instella.Core.Logging;
using Instella.Core.Manifest;
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Runtime.Core;
using Instella.Installer.Runtime.Installation;
using Instella.Installer.Runtime.Runners;
using Instella.Installer.Runtime.UI.Widgets;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Installation;

/// <summary>
/// Routing + dispatch behaviour of the <c>--preview</c> short-circuit
/// in <see cref="InstellaInstallerImpl.RunAsync"/> and
/// <see cref="PreviewModeRunner"/>. The widget-host launch is stubbed via
/// <see cref="PreviewHostLauncher"/> so these tests run cross-platform without
/// spinning up a native window.
/// </summary>
[TestFixture]
public sealed class PreviewModeRunnerDispatchTests
{
    private static FrozenConfig MinimalConfig(bool previewEnabled = true)
    {
        var b = new InstallerBuilder();
        b.WithApp("Preview Test", "com.example.preview", new Version(1, 0, 0));
        if (previewEnabled) b.EnablePreview();
        var installer = (InstellaInstallerImpl)b.Build();
        return installer.ConfigForTests;
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

    [Test]
    public async Task PreviewWithoutOptIn_returnsUsageInvalidArgs()
    {
        var b = new InstallerBuilder();
        b.WithApp("Preview Test", "com.example.preview", new Version(1, 0, 0));
        // Intentionally no EnablePreview().
        var installer = b.Build();

        var exit = await installer.RunAsync(new[] { "--preview" }, CancellationToken.None);

        Assert.That(exit, Is.EqualTo((int)InstellaExitCode.UsageInvalidArgs));
    }

    [Test]
    public async Task EmitManifestBeatsPreview_whenBothPresent()
    {
        // Build an installer with preview enabled and pass --emit-manifest AND
        // --preview on the same command line. --emit-manifest must win so build
        // tooling isn't accidentally hijacked by a --preview in a parent's env.
        var b = new InstallerBuilder();
        b.WithApp("Preview Test", "com.example.preview", new Version(1, 0, 0));
        b.EnablePreview();
        var installer = b.Build();

        var tempManifest = Path.Combine(Path.GetTempPath(), $"preview-dispatch-{Guid.NewGuid():N}.json");
        try
        {
            var exit = await installer.RunAsync(
                new[] { "--emit-manifest", tempManifest, "--preview" },
                CancellationToken.None);

            Assert.That(exit, Is.EqualTo((int)InstellaExitCode.Success));
            Assert.That(File.Exists(tempManifest), Is.True, "--emit-manifest should still write the JSON file");
        }
        finally
        {
            if (File.Exists(tempManifest)) File.Delete(tempManifest);
        }
    }

    [Test]
    public async Task PreviewManage_returnsSuccess_withStdoutReport()
    {
        var config = MinimalConfig();
        var stdout = new StringWriter();
        PreviewHostLauncher launcher = (t, pages, states, ip) => PreviewHostOutcome.Completed;
        var runner = new PreviewModeRunner(config, new NullLogger(), stdout, launcher);

        var args = new PreviewCliArgs(
            PreviewFlagPresent: true,
            Mode: InstallerMode.Manage,
            Speed: PreviewSpeed.Fast,
            FailAtStep: null);

        var exit = await runner.RunAsync(args, CancellationToken.None);

        Assert.That(exit, Is.EqualTo(InstellaExitCode.Success));
        Assert.That(stdout.ToString(), Does.Contain("preview[manage]"));
        Assert.That(stdout.ToString(), Does.Contain("Preview Test"));
    }

    [Test]
    public async Task PreviewCleanup_returnsSuccess_withStdoutReport()
    {
        var config = MinimalConfig();
        var stdout = new StringWriter();
        PreviewHostLauncher launcher = (t, pages, states, ip) => PreviewHostOutcome.Completed;
        var runner = new PreviewModeRunner(config, new NullLogger(), stdout, launcher);

        var args = new PreviewCliArgs(true, InstallerMode.Cleanup, PreviewSpeed.Fast, null);

        var exit = await runner.RunAsync(args, CancellationToken.None);

        Assert.That(exit, Is.EqualTo(InstellaExitCode.Success));
        Assert.That(stdout.ToString(), Does.Contain("preview[cleanup]"));
    }

    [Test]
    public async Task PreviewWizard_hostCancels_returnsUserCancelled()
    {
        var config = MinimalConfig();
        var stdout = new StringWriter();
        PreviewHostLauncher launcher = (t, pages, states, ip) => PreviewHostOutcome.Cancelled;
        var runner = new PreviewModeRunner(config, new NullLogger(), stdout, launcher);

        var args = new PreviewCliArgs(true, InstallerMode.FirstInstall, PreviewSpeed.Fast, null);

        var exit = await runner.RunAsync(args, CancellationToken.None);

        Assert.That(exit, Is.EqualTo(InstellaExitCode.UserCancelled));
    }

    [Test]
    public async Task PreviewWizard_hostCompletes_runsSimulatedPipeline()
    {
        var config = MinimalConfig();
        var stdout = new StringWriter();
        var titleSeen = string.Empty;
        var isPreviewSeen = false;
        PreviewHostLauncher launcher = (title, pages, states, isPreview) =>
        {
            titleSeen = title;
            isPreviewSeen = isPreview;
            return PreviewHostOutcome.Completed;
        };
        var runner = new PreviewModeRunner(config, new NullLogger(), stdout, launcher);

        var args = new PreviewCliArgs(true, InstallerMode.FirstInstall, PreviewSpeed.Fast, null);

        var exit = await runner.RunAsync(args, CancellationToken.None);

        Assert.That(exit, Is.EqualTo(InstellaExitCode.Success));
        Assert.That(isPreviewSeen, Is.True);
        // PreviewModeRunner passes the raw app name; each widget host decorates
        // the title with PreviewMarker.DecorateTitle(title, IsPreview). If the runner
        // decorated it too, the window title would carry the preview suffix twice.
        Assert.That(titleSeen, Is.EqualTo("Preview Test"));
        Assert.That(titleSeen, Does.Not.Contain("PREVIEW"));
        Assert.That(stdout.ToString(), Does.Contain("simulating"));
    }

    [Test]
    public async Task PreviewFailInjection_maps_to_InstallGeneralFailure()
    {
        var config = MinimalConfig();
        var stdout = new StringWriter();
        PreviewHostLauncher launcher = (t, pages, states, ip) => PreviewHostOutcome.Completed;
        var runner = new PreviewModeRunner(config, new NullLogger(), stdout, launcher);

        // `extract-payload` is a known built-in step name; valid for install simulation.
        var args = new PreviewCliArgs(true, InstallerMode.FirstInstall, PreviewSpeed.Fast, "extract-payload");

        var exit = await runner.RunAsync(args, CancellationToken.None);

        Assert.That(exit, Is.EqualTo(InstellaExitCode.InstallGeneralFailure));
    }

    [Test]
    public async Task PreviewUnknownFailStep_returns_UsageInvalidArgs()
    {
        var config = MinimalConfig();
        var stdout = new StringWriter();
        PreviewHostLauncher launcher = (t, pages, states, ip) => PreviewHostOutcome.Completed;
        var runner = new PreviewModeRunner(config, new NullLogger(), stdout, launcher);

        var args = new PreviewCliArgs(true, InstallerMode.FirstInstall, PreviewSpeed.Fast, "does-not-exist");

        var exit = await runner.RunAsync(args, CancellationToken.None);

        Assert.That(exit, Is.EqualTo(InstellaExitCode.UsageInvalidArgs));
        Assert.That(stdout.ToString(), Does.Contain("does-not-exist"));
    }

    [Test]
    public void BuildPageSpecs_includes_welcomeAndCompleteSynthetic()
    {
        var config = MinimalConfig();
        var runner = new PreviewModeRunner(config, new NullLogger(), TextWriter.Null,
            (t, p, s, ip) => PreviewHostOutcome.Completed);

        var pages = runner.BuildPageSpecs(InstallerMode.FirstInstall);

        Assert.That(pages, Has.Count.GreaterThanOrEqualTo(2));
        Assert.That(pages[0].Id, Is.EqualTo("preview-welcome"));
        Assert.That(pages[^1].Id, Is.EqualTo("preview-complete"));
    }

    // Every wizard mode (Install / Upgrade / Repair / Update / Uninstall) routes
    // through the same preview dispatch, so one mode cannot drift from the others.
    [TestCase(InstallerMode.FirstInstall)]
    [TestCase(InstallerMode.Upgrade)]
    [TestCase(InstallerMode.Repair)]
    [TestCase(InstallerMode.Update)]
    [TestCase(InstallerMode.Uninstall)]
    public async Task PreviewWizard_allModes_succeed_whenHostCompletes(InstallerMode mode)
    {
        var config = MinimalConfig();
        var stdout = new StringWriter();
        PreviewHostLauncher launcher = (t, pages, states, ip) => PreviewHostOutcome.Completed;
        var runner = new PreviewModeRunner(config, new NullLogger(), stdout, launcher);

        var args = new PreviewCliArgs(true, mode, PreviewSpeed.Fast, null);

        var exit = await runner.RunAsync(args, CancellationToken.None);

        Assert.That(exit, Is.EqualTo(InstellaExitCode.Success));
    }

    // `.EnablePreview()` on the builder InstellaInstaller.Create() returns must not throw.
    [Test]
    public void EnablePreview_defaultBuilder_doesNotThrow()
    {
        InstallerBuilder builder = InstellaInstaller.Create();
        builder.WithApp("Preview", "com.example.preview", new Version(1, 0, 0));
        Assert.DoesNotThrow(() => builder.EnablePreview());
    }

    // BuildPageSpecs must pre-seed PageState from widget
    // defaults so ContinueWhen predicates see the initial values. Rather
    // than authoring a PageBuilder through its public API (Build() is
    // internal), construct the PageSpec directly.
    [Test]
    public void SeedWidgetDefaults_seedsTextInputAndCheckBox()
    {
        var page = PageSpecFromWidgets("p",
            new TextInput("Your name", "Alice") { Id = "name" },
            new CheckBox("Opt in", true) { Id = "optin" });

        var state = new PageState();
        PreviewModeRunner.SeedWidgetDefaults(page, state);

        Assert.That(state.Text("name"), Is.EqualTo("Alice"));
        Assert.That(state.Bool("optin"), Is.True);
    }

    [Test]
    public void SeedWidgetDefaults_skipsReadOnlyWidgets()
    {
        var page = PageSpecFromWidgets("p",
            new Heading("Hi"),
            new Paragraph("Welcome"));

        var state = new PageState();
        Assert.DoesNotThrow(() => PreviewModeRunner.SeedWidgetDefaults(page, state));
        // Heading / Paragraph have no Id — state remains empty.
        Assert.That(state.Text("headingText", "empty"), Is.EqualTo("empty"));
    }

    [Test]
    public void SeedWidgetDefaults_seedsFolderPickerAndDropdown()
    {
        var page = PageSpecFromWidgets("p",
            new FolderPicker("Install path", "C:\\Default") { Id = "installpath" },
            new Dropdown("Channel", new[] { "stable", "beta" }, "stable") { Id = "channel" });

        var state = new PageState();
        PreviewModeRunner.SeedWidgetDefaults(page, state);

        Assert.That(state.Text("installpath"), Is.EqualTo("C:\\Default"));
        Assert.That(state.Text("channel"), Is.EqualTo("stable"));
    }

    private static PageSpec PageSpecFromWidgets(string id, params Widget[] widgets)
    {
        return new PageSpec(
            Id: id,
            Widgets: widgets,
            ContinueWhen: null,
            OnEnter: null, OnLeave: null, OnValidate: null,
            AllowedModes: new HashSet<InstallerMode> { InstallerMode.FirstInstall },
            When: null);
    }
}
