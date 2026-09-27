using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.FileSystem;
using Instella.Core.Installation;
using Instella.Core.Logging;
using Instella.Core.Manifest;
using Instella.Core.Platform;
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Runtime.Core;
using Instella.Installer.Runtime.Installation;
using Instella.Installer.Runtime.UI;
using Instella.Installer.Runtime.UI.Widgets;

namespace Instella.Installer.Runtime.Runners;

/// <summary>Outcome of a preview widget-host run — platform-neutral.</summary>
internal enum PreviewHostOutcome
{
    Completed,
    Cancelled,
}

/// <summary>
/// Platform-abstracted widget-host launcher. The production
/// <see cref="PreviewModeRunner"/> picks the right implementation
/// (Win32/GTK/Cocoa) based on <see cref="OperatingSystem"/>; tests inject a
/// stub so dispatch + simulated-step behavior can be verified on any host.
/// </summary>
internal delegate PreviewHostOutcome PreviewHostLauncher(
    string title,
    IReadOnlyList<PageSpec> pages,
    IReadOnlyList<PageState> pageStates,
    bool isPreview);

/// <summary>
/// Drives <c>--preview</c>. Routes by <see cref="PreviewCliArgs.Mode"/>: wizard
/// modes launch a widget host with <c>IsPreview=true</c> and then run the
/// <see cref="SimulatedStepExecutor"/> against the frozen step list; headless
/// modes (<c>manage</c> / <c>cleanup</c>) emit a synthesized stdout report.
/// </summary>
/// <remarks>
/// Constructed by <c>InstellaInstallerImpl.RunAsync</c> when
/// <c>--preview</c> arrives and <see cref="FrozenConfig.PreviewEnabled"/> is
/// set. Opt-in checking happens upstream so this runner can assume it's been
/// invited to run.
/// </remarks>
internal sealed class PreviewModeRunner
{
    private readonly FrozenConfig _config;
    private readonly IInstellaLogger _log;
    private readonly TextWriter _stdout;
    private readonly PreviewHostLauncher _hostLauncher;

    public PreviewModeRunner(FrozenConfig config, IInstellaLogger log)
        : this(config, log, Console.Out, DefaultHostLauncher)
    {
    }

    internal PreviewModeRunner(
        FrozenConfig config,
        IInstellaLogger log,
        TextWriter stdout,
        PreviewHostLauncher hostLauncher)
    {
        _config = config;
        _log = log;
        _stdout = stdout;
        _hostLauncher = hostLauncher;
    }

    public async Task<InstellaExitCode> RunAsync(PreviewCliArgs args, CancellationToken ct)
    {
        _log.Info($"preview: {_config.AppName} v{_config.AppVersion} mode={args.Mode} speed={args.Speed}");
        _stdout.WriteLine($"preview: {_config.AppName} v{_config.AppVersion} mode={args.Mode} speed={args.Speed}");
        if (!string.IsNullOrEmpty(args.FailAtStep))
            _stdout.WriteLine($"preview: failure will be injected at step '{args.FailAtStep}'");

        WarnOnMissingAssets();

        return args.Mode switch
        {
            InstallerMode.FirstInstall or InstallerMode.Upgrade or InstallerMode.Repair =>
                await RunWizardAsync(args, ct),
            InstallerMode.Update => await RunWizardAsync(args, ct),
            InstallerMode.Uninstall => await RunUninstallAsync(args, ct),
            InstallerMode.Manage => RunManage(),
            InstallerMode.Cleanup => RunCleanup(),
            _ => InstellaExitCode.UsageUnknownMode,
        };
    }

    private async Task<InstellaExitCode> RunWizardAsync(PreviewCliArgs args, CancellationToken ct)
    {
        var pages = BuildPageSpecs(args.Mode);
        var states = pages.Select(p =>
        {
            var s = new PageState();
            SeedWidgetDefaults(p, s);
            return s;
        }).ToList();
        // Pass the raw app name — each widget host applies PreviewMarker.DecorateTitle
        // itself from its own IsPreview flag, so decorating here would double-suffix
        // the title (e.g. "AppName — PREVIEW — PREVIEW").
        var hostOutcome = _hostLauncher(_config.AppName, pages, states, isPreview: true);

        if (hostOutcome == PreviewHostOutcome.Cancelled)
        {
            _log.Info("preview: user cancelled");
            _stdout.WriteLine("preview: user cancelled");
            return InstellaExitCode.UserCancelled;
        }

        var steps = PreviewStepLists.Build(_config, args.Mode);
        SimulatedStepExecutor executor;
        try
        {
            executor = new SimulatedStepExecutor(steps, args.Speed, args.FailAtStep);
        }
        catch (InvalidOperationException ex)
        {
            _stdout.WriteLine($"preview: {ex.Message}");
            _log.Error($"preview: {ex.Message}");
            return InstellaExitCode.UsageInvalidArgs;
        }

        var context = BuildContext(args.Mode);
        var progress = new ConsoleProgress(_stdout);
        _stdout.WriteLine($"preview: simulating {PreviewReport.DescribeModeSteps(args.Mode)}");

        var result = await executor.ExecuteAsync(context, progress, ct);
        return MapExitCode(result, args.Mode, ct);
    }

    private async Task<InstellaExitCode> RunUninstallAsync(PreviewCliArgs args, CancellationToken ct)
    {
        // Uninstall UX intentionally stays minimal (workflow.md decision from
        // 2026-04-17). Preview emulates that: no wizard, no custom pages —
        // just a single synthetic confirmation line to stdout followed by the
        // simulated step pipeline.
        _stdout.WriteLine("preview[uninstall]: simulating 'Remove this installation?' dialog (Yes)");

        var steps = PreviewStepLists.Build(_config, InstallerMode.Uninstall);
        SimulatedStepExecutor executor;
        try
        {
            executor = new SimulatedStepExecutor(steps, args.Speed, args.FailAtStep);
        }
        catch (InvalidOperationException ex)
        {
            _stdout.WriteLine($"preview: {ex.Message}");
            _log.Error($"preview: {ex.Message}");
            return InstellaExitCode.UsageInvalidArgs;
        }

        var context = BuildContext(InstallerMode.Uninstall);
        var progress = new ConsoleProgress(_stdout);

        var result = await executor.ExecuteAsync(context, progress, ct);
        return MapExitCode(result, InstallerMode.Uninstall, ct);
    }

    private InstellaExitCode RunManage()
    {
        _stdout.Write(PreviewReport.BuildManageReport(_config));
        return InstellaExitCode.Success;
    }

    private InstellaExitCode RunCleanup()
    {
        _stdout.Write(PreviewReport.BuildCleanupReport(_config));
        return InstellaExitCode.Success;
    }

    /// <summary>
    /// Map a <see cref="SimulatedStepExecutor"/> result into the install-mode
    /// exit-code scheme. Cancellation wins over reported failure to match
    /// real-install UX; injected failures surface as
    /// <see cref="InstellaExitCode.InstallGeneralFailure"/>.
    /// </summary>
    private static InstellaExitCode MapExitCode(ExecutionResult result, InstallerMode mode, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return InstellaExitCode.UserCancelled;
        if (!result.Success)
        {
            return mode == InstallerMode.Update
                ? InstellaExitCode.UpdateGeneralFailure
                : mode == InstallerMode.Uninstall
                    ? InstellaExitCode.UninstallGeneralFailure
                    : InstellaExitCode.InstallGeneralFailure;
        }
        return InstellaExitCode.Success;
    }

    /// <summary>
    /// Assemble the widget-host page list for wizard preview modes:
    /// synthetic Welcome → user's custom pages (filtered by
    /// <see cref="PageSpec.AllowedModes"/>) → synthetic Complete. Guarantees
    /// at least one page even when the user didn't author any (the widget
    /// host throws on zero pages).
    /// </summary>
    internal IReadOnlyList<PageSpec> BuildPageSpecs(InstallerMode mode)
    {
        var list = new List<PageSpec>
        {
            BuildPage("preview-welcome", p => p
                .Heading($"Welcome to the {_config.AppName} installer")
                .Paragraph($"Version {_config.AppVersion}. This window is running in PREVIEW mode — no changes will be made to your system.")
                .Paragraph("Use the Continue button below to walk through the installer pages as an end user would see them.")),
        };

        foreach (var page in _config.Pages)
        {
            if (page.AllowedModes.Contains(mode))
                list.Add(page);
        }

        list.Add(BuildPage("preview-complete", p => p
            .Heading("Preview complete")
            .Paragraph($"This is the page an end user would see after a successful {mode} run.")
            .Paragraph("Click Finish to exit preview.")));

        return list;
    }

    private static PageSpec BuildPage(string id, Action<PageBuilder> configure)
    {
        var b = new PageBuilder(id);
        configure(b);
        // Welcome/Complete pages are synthetic and always run in preview
        // regardless of mode — opt them into every mode explicitly.
        b.InModes(
            InstallerMode.FirstInstall,
            InstallerMode.Upgrade,
            InstallerMode.Repair,
            InstallerMode.Update,
            InstallerMode.Uninstall,
            InstallerMode.Manage,
            InstallerMode.Cleanup,
            InstallerMode.Recover);
        return b.Build();
    }

    private InstallContext BuildContext(InstallerMode mode)
    {
        var platform = new NoOpPlatformServices(PlatformDetector.Current);
        var manifest = _config.ToManifest();
        var options = new InstallOptions { InstallPath = "preview://install", Elevation = _config.Elevation };

        return new InstallContext
        {
            AppName = _config.AppName,
            AppId = _config.AppId,
            AppVersion = _config.AppVersion,
            InstallPath = "preview://install",
            Mode = mode,
            Scope = InstallationScope.PerUser,
            Manifest = manifest,
            Options = options,
            Platform = platform,
            FileSystem = RealFileSystem.Instance,
            Log = _log,
        };
    }

    /// <summary>
    /// Production widget-host launcher. Selects the native host for the
    /// current OS; unsupported platforms return
    /// <see cref="PreviewHostOutcome.Cancelled"/> after logging — preview is
    /// a developer tool, not something to bomb out over an unsupported host.
    /// </summary>
    internal static PreviewHostOutcome DefaultHostLauncher(
        string title,
        IReadOnlyList<PageSpec> pages,
        IReadOnlyList<PageState> pageStates,
        bool isPreview)
    {
        if (OperatingSystem.IsWindows())
            return LaunchWin32Host(title, pages, pageStates, isPreview);
        if (OperatingSystem.IsLinux())
            return LaunchGtkHost(title, pages, pageStates, isPreview);
        if (OperatingSystem.IsMacOS())
            return LaunchCocoaHost(title, pages, pageStates, isPreview);

        Console.WriteLine("preview: host platform is not supported — skipping widget host");
        return PreviewHostOutcome.Completed;
    }

    private static PreviewHostOutcome LaunchWin32Host(
        string title, IReadOnlyList<PageSpec> pages, IReadOnlyList<PageState> pageStates, bool isPreview)
    {
        using var host = new UI.Windows.Widgets.Win32WidgetHost(title, pages, pageStates) { IsPreview = isPreview };
        return host.Run() == UI.Windows.Widgets.Win32WidgetHostOutcome.Completed
            ? PreviewHostOutcome.Completed
            : PreviewHostOutcome.Cancelled;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static PreviewHostOutcome LaunchGtkHost(
        string title, IReadOnlyList<PageSpec> pages, IReadOnlyList<PageState> pageStates, bool isPreview)
    {
        if (!UI.Linux.Widgets.GtkWidgetHost.TryInitialize())
        {
            Console.WriteLine("preview: no display is available (GTK could not initialise); continuing without the wizard");
            return PreviewHostOutcome.Completed;
        }
        using var host = new UI.Linux.Widgets.GtkWidgetHost(title, pages, pageStates) { IsPreview = isPreview };
        return host.Run() == UI.Linux.Widgets.GtkWidgetHostOutcome.Completed
            ? PreviewHostOutcome.Completed
            : PreviewHostOutcome.Cancelled;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private static PreviewHostOutcome LaunchCocoaHost(
        string title, IReadOnlyList<PageSpec> pages, IReadOnlyList<PageState> pageStates, bool isPreview)
    {
        using var host = new UI.MacOS.Widgets.CocoaWidgetHost(title, pages, pageStates) { IsPreview = isPreview };
        return host.Run() == UI.MacOS.Widgets.CocoaWidgetHostOutcome.Completed
            ? PreviewHostOutcome.Completed
            : PreviewHostOutcome.Cancelled;
    }

    /// <summary>
    /// Seed <paramref name="state"/> with widget-declared defaults so that
    /// <c>ContinueWhen</c> predicates can observe the initial values before
    /// the user has interacted with any control. Without this, a page such as
    /// <c>.TextInput("path", "Label", "C:\\default")</c> + <c>.ContinueWhen(s =&gt; s.Text("path").Length &gt; 0)</c>
    /// would leave the Continue button disabled until the user edits the
    /// field — even though the control visibly contains "C:\\default".
    /// </summary>
    /// <remarks>
    /// Only input widgets with an <see cref="Widget.Id"/> and a declared
    /// default contribute. Read-only widgets (Heading, Paragraph, Progress,
    /// etc.) are skipped. This is deliberately additive — a user-authored
    /// <c>OnEnter</c> hook that also writes to <see cref="PageState"/> still
    /// runs and can override the seeded defaults via the normal state flow.
    /// </remarks>
    internal static void SeedWidgetDefaults(PageSpec page, PageState state)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(state);
        foreach (var widget in page.Widgets)
        {
            var id = widget.Id;
            if (string.IsNullOrEmpty(id)) continue;
            switch (widget)
            {
                case TextInput t: state.Set(id, t.Default); break;
                case CheckBox c: state.Set(id, c.Default); break;
                case RadioGroup rg when rg.Default is not null: state.Set(id, rg.Default); break;
                case Dropdown dd when dd.Default is not null: state.Set(id, dd.Default); break;
                case FolderPicker fp when fp.Default is not null: state.Set(id, fp.Default); break;
                case FilePicker fpick when fpick.Default is not null: state.Set(id, fpick.Default); break;
            }
        }
    }

    /// <summary>
    /// Emit warnings for <see cref="ImageSource"/>-backed assets whose files
    /// don't exist on the dev box. Preview never crashes on missing assets —
    /// finding them is the point — but the reviewer should see a clear
    /// warning so they know the missing asset is why the UI shows a placeholder.
    /// </summary>
    private void WarnOnMissingAssets()
    {
        if (_config.Icon is FileImageSource file && !File.Exists(file.Path))
        {
            _log.Warn($"preview: icon '{file.Path}' not found; renderer will substitute a default");
            _stdout.WriteLine($"preview: icon '{file.Path}' not found; renderer will substitute a default");
        }

        foreach (var assoc in _config.FileAssociations)
        {
            if (assoc.IconPath is { Length: > 0 } ap && !File.Exists(ap))
            {
                _log.Warn($"preview: file-association icon '{ap}' not found; renderer will substitute a default");
            }
        }
    }

    /// <summary>Dumps each simulated-step progress tick to stdout as a one-liner.</summary>
    private sealed class ConsoleProgress : IProgress<OverallProgress>
    {
        private readonly TextWriter _stdout;

        public ConsoleProgress(TextWriter stdout) => _stdout = stdout;

        public void Report(OverallProgress value)
        {
            var pct = (int)(value.Fraction * 100);
            _stdout.WriteLine($"preview: [{pct,3}%] {value.Stage} {value.StepName}{(value.Status is null ? "" : $" ({value.Status})")}");
        }
    }
}
