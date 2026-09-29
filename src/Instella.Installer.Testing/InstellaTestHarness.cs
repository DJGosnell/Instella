using Instella.Core.FileSystem;
using Instella.Core.Installation;
using Instella.Core.Logging;
using Instella.Core.Manifest;
using Instella.Core.Platform;
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Runtime.Core;
using Instella.Installer.Runtime.Installation;
using Instella.Installer.Runtime.Migrations;
using Instella.Installer.Runtime.UI.Widgets;

namespace Instella.Installer.Testing;

/// <summary>
/// Entry point for CI-testable installer scenarios. A harness bundles a
/// fully-wired <see cref="InstallContext"/> with in-memory file system and
/// registry fakes, a recording logger, and seeded page state. The caller
/// obtains one via <see cref="Create"/>, configures the scenario on the
/// returned <see cref="Builder"/>, calls <see cref="Builder.Build"/>, then
/// drives custom steps through <see cref="RunStepAsync"/> or a full
/// step-list through <see cref="RunInstallAsync"/>.
/// </summary>
/// <remarks>
/// The harness never initializes the Logsmith pipeline, never opens file
/// sinks, and never hits the real registry — it is safe to spin up many
/// harnesses in parallel tests without cross-contamination.
/// </remarks>
public sealed class InstellaTestHarness : IAsyncDisposable
{
    private readonly Dictionary<string, PageState> _pageStates;
    private readonly IInstellaInstaller? _installer;
    private readonly string[] _cliArgs;

    internal InstellaTestHarness(
        InstallContext context,
        IFakeFileSystem fileSystem,
        FakePlatformServices platform,
        RecordingLogger logger,
        RecordingSink logSink,
        Dictionary<string, PageState> pageStates,
        IInstellaInstaller? installer,
        string[] cliArgs,
        FakeElevationService? elevation = null)
    {
        Elevation = elevation ?? new FakeElevationService();
        Context = context;
        FileSystem = fileSystem;
        PlatformServices = platform;
        Logger = logger;
        LogSink = logSink;
        _pageStates = pageStates;
        _installer = installer;
        _cliArgs = cliArgs;
    }

    // Set by Builder.Build(): the app files RunFullAsync installs, the fake running programs, and
    // the fake profile the known folders of install migrations live in.
    private byte[]? _payload;
    private readonly HarnessProcesses _processes = new();
    private readonly string _knownFolderRoot = Path.Combine(Path.GetTempPath(), "instella-harness-folders", Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Where <paramref name="folder"/> is for install migrations during <see cref="RunFullAsync"/>:
    /// a fake profile inside <see cref="FileSystem"/>, never the host's. Seed old files there.
    /// </summary>
    public string KnownFolderPath(KnownFolder folder, InstallationScope scope = InstallationScope.PerUser) =>
        FakeKnownFolders(folder, scope == InstallationScope.SystemWide)
        ?? throw new ArgumentException($"{folder} is the install folder; use the installer's --path", nameof(folder));

    /// <summary>
    /// Pretends a program is running from <paramref name="exePath"/> (a file in <see cref="FileSystem"/>)
    /// during <see cref="RunFullAsync"/>: install migrations find it and can close it, unless
    /// <paramref name="closes"/> is false.
    /// </summary>
    public void StartProcess(string exePath, bool closes = true)
    {
        ArgumentException.ThrowIfNullOrEmpty(exePath);
        _processes.Start(exePath, closes);
    }

    /// <summary>Whether a program started with <see cref="StartProcess"/> is still running.</summary>
    public bool IsProcessRunning(string exePath) => _processes.IsRunning(exePath);

    /// <summary>
    /// Decides what the programs started during <see cref="RunFullAsync"/> do: the app's upgrade program
    /// (<c>instella-upgrade.json</c>) and programs install migrations run. The default is
    /// <c>ProgramOutcome.Exit(0)</c>. <paramref name="behaviour"/> may change <see cref="FileSystem"/>, to
    /// simulate what the program does to the app's data. Nothing is ever really started.
    /// </summary>
    public void WhenProgramRuns(Func<ProgramRun, ProgramOutcome> behaviour)
    {
        ArgumentNullException.ThrowIfNull(behaviour);
        _programs.SetBehaviour(behaviour);
    }

    /// <summary>The programs <see cref="RunFullAsync"/> started, in order, with their arguments.</summary>
    public IReadOnlyList<ProgramRun> ProgramRuns => _programs.Runs;

    private readonly HarnessProgramRunner _programs = new();

    private string? FakeKnownFolders(KnownFolder folder, bool machine)
    {
        var profile = Path.Combine(_knownFolderRoot, "Users", "user");
        var programData = Path.Combine(_knownFolderRoot, "ProgramData");
        return folder switch
        {
            KnownFolder.LocalAppData => Path.Combine(profile, "AppData", "Local"),
            KnownFolder.RoamingAppData => Path.Combine(profile, "AppData", "Roaming"),
            KnownFolder.StartMenuPrograms => machine
                ? Path.Combine(programData, "Microsoft", "Windows", "Start Menu", "Programs")
                : Path.Combine(profile, "AppData", "Roaming", "Microsoft", "Windows", "Start Menu", "Programs"),
            KnownFolder.ProgramFiles => Path.Combine(_knownFolderRoot, "Program Files"),
            KnownFolder.ProgramFilesX86 => Path.Combine(_knownFolderRoot, "Program Files (x86)"),
            KnownFolder.ProgramData => programData,
            _ => null,
        };
    }

    /// <summary>
    /// Elevation for <see cref="RunFullAsync"/>: not elevated by default; records every elevated
    /// relaunch instead of showing a UAC prompt.
    /// </summary>
    public FakeElevationService Elevation { get; }

    /// <summary>The errors and notices <see cref="RunFullAsync"/> would have shown in a message box.</summary>
    public IReadOnlyList<string> ShownMessages => _messages.Shown;

    /// <summary>The programs <see cref="RunFullAsync"/> would have started (the app after an install or update).</summary>
    public IReadOnlyList<string> LaunchedPrograms => _launcher.Launched;

    private readonly HarnessMessages _messages = new();
    private readonly HarnessLauncher _launcher = new();

    /// <summary>Fully-wired install context — platform, filesystem, logger, manifest, options.</summary>
    public InstallContext Context { get; }

    /// <summary>The in-memory file system fake. Seed preconditions via
    /// <see cref="IFakeFileSystem.AddFile"/>; inspect post-run state via
    /// <see cref="IFakeFileSystem.Snapshot"/>.</summary>
    public IFakeFileSystem FileSystem { get; }

    /// <summary>The fake platform services — registry, shortcut calls,
    /// PATH entries, and so on are all observable here.</summary>
    public FakePlatformServices PlatformServices { get; }

    /// <summary>Shorthand for <c>PlatformServices.Registry</c>.</summary>
    public IFakeRegistry Registry => PlatformServices.Registry;

    /// <summary>The logger handed to steps as <c>ctx.Log</c>.</summary>
    public IInstellaLogger Logger { get; }

    /// <summary>Every <see cref="InstellaLogEntry"/> captured during the run, including a <see cref="RunFullAsync"/> run's log.</summary>
    public RecordingSink LogSink { get; }

    /// <summary>Page-state bag keyed by page id. Seeded via <see cref="Builder.WithPageState"/>.</summary>
    public IReadOnlyDictionary<string, PageState> PageStates => _pageStates;

    /// <summary>Create a harness builder with sensible defaults.</summary>
    public static Builder Create() => new();

    /// <summary>
    /// Run a single step through the installer's step executor. Returns the
    /// <see cref="ExecutionResult"/> so tests can assert on step
    /// outcome, warnings, and the context-mutations the step recorded.
    /// </summary>
    public Task<ExecutionResult> RunStepAsync(
        IInstallStepExecution step,
        IProgress<OverallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(step);
        return RunInstallAsync(new IInstallStepExecution[] { step }, progress, cancellationToken);
    }

    /// <summary>
    /// Run the supplied list of steps, in order and with rollback on failure, against the
    /// harness context.
    /// </summary>
    public Task<ExecutionResult> RunInstallAsync(
        IReadOnlyList<IInstallStepExecution> steps,
        IProgress<OverallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var executor = new StepExecutor(steps);
        return executor.ExecuteAsync(Context, progress, cancellationToken);
    }

    /// <summary>
    /// Invoke the attached <see cref="IInstellaInstaller"/> end-to-end using
    /// the CLI args configured on the harness. Requires <see cref="Builder.WithInstaller"/>
    /// to have been called; throws <see cref="InvalidOperationException"/>
    /// otherwise.
    /// </summary>
    /// <remarks>
    /// An installer built with <c>InstellaInstaller.Create()</c> runs against the harness
    /// <see cref="FileSystem"/> and <see cref="PlatformServices"/>, never the host's. Use
    /// <see cref="RunStepAsync"/>/<see cref="RunInstallAsync"/> for step-level assertions.
    /// </remarks>
    public Task<int> RunFullAsync(CancellationToken cancellationToken = default) => RunFullWithArgsAsync(_cliArgs, cancellationToken);

    /// <summary>
    /// <see cref="RunFullAsync"/> with <paramref name="args"/> instead of the
    /// arguments given to <see cref="Builder.WithCliArgs"/>, so one harness (one file system, one
    /// registry) can install, repair and uninstall in turn.
    /// </summary>
    public Task<int> RunFullWithArgsAsync(IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (_installer is null)
            throw new InvalidOperationException(
                "RunFullAsync requires an installer — call Builder.WithInstaller(...) before Build().");
        var argv = args.ToArray();
        // Never the host: the built-in installer gets a fake for everything that could
        // reach it: UAC, windows, message boxes, Restart Manager, process starts, the stub folder,
        // the payload, and the known folders install migrations use.
        var payload = _payload;
        if (_installer is Instella.Installer.Runtime.Builders.InstellaInstallerImpl impl)
            return impl.RunAsync(argv, new Instella.Installer.Runtime.Builders.InstallerServices(
                PlatformServices, FileSystem,
                Elevation: Elevation,
                Payload: payload is null ? null : () => new MemoryStream(payload, writable: false),
                Messages: _messages,
                HostFactory: (_, _, _) => throw new InvalidOperationException(NoWindowMessage),
                ProcessFinder: _processes,
                AppLauncher: _launcher,
                ScopePrompt: (_, _) => throw new InvalidOperationException(NoWindowMessage),
                StubDirectory: () => Context.InstallPath,
                ManagerUi: (_, _, _, _, _, _) => throw new InvalidOperationException(NoWindowMessage),
                KnownFolders: new Instella.Installer.Runtime.Migrations.KnownFolderResolver(FakeKnownFolders),
                ProcessCloser: _processes,
                Programs: _programs,
                Log: Logger), cancellationToken);
        return _installer.RunAsync(argv, cancellationToken);
    }

    internal const string NoWindowMessage =
        "interactive UI requested in a harness run; use --silent or provide a host";

    private sealed class HarnessMessages : Instella.Installer.Runtime.Runners.IUserMessages
    {
        private readonly List<string> _shown = [];
        public IReadOnlyList<string> Shown { get { lock (_shown) return [.. _shown]; } }
        public void Error(string title, string text) { lock (_shown) _shown.Add(text); }
        public void Info(string title, string text) { lock (_shown) _shown.Add(text); }
        public bool Confirm(string title, string text) { lock (_shown) _shown.Add(text); return false; }
    }

    private sealed class HarnessLauncher : Instella.Installer.Runtime.Core.Processes.IAppLauncher
    {
        private readonly List<string> _launched = [];
        public IReadOnlyList<string> Launched { get { lock (_launched) return [.. _launched]; } }
        public void Launch(string exePath, string workingDirectory) { lock (_launched) _launched.Add(exePath); }
    }

    /// <summary>Releases the payload archive, if the run opened one.</summary>
    public ValueTask DisposeAsync()
    {
        if (Context.PayloadArchive is { } archive)
        {
            Context.PayloadArchive = null;
            archive.Dispose();
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Fluent builder for <see cref="InstellaTestHarness"/>. Every method
    /// returns <c>this</c> so calls chain.
    /// </summary>
    public sealed class Builder
    {
        private string _appId = "com.instella.tests.fake";
        private string _appName = "FakeApp";
        private Version _version = new(1, 0, 0);
        private InstallerMode _mode = InstellaMode();
        private string _installPath = OperatingSystem.IsWindows()
            ? "C:\\Fake\\Install\\FakeApp"
            : "/tmp/fake-install/FakeApp";
        private string[] _cliArgs = Array.Empty<string>();
        private IInstellaInstaller? _installer;
        private FakeElevationService? _elevationService;
        private IFakeFileSystem? _fileSystem;
        private FakePlatformServices? _platformServices;
        private RecordingSink? _logSink;
        private TargetPlatform _platform = DefaultPlatform();
        private InstallationScope _scope = InstallationScope.PerUser;
        private ElevationMode _elevation = ElevationMode.UserChoice;
        private readonly Dictionary<string, PageState> _pageStates = new();
        private string _serverUrl = "https://updates.example.test";
        private string _channel = "stable";
        private Dictionary<string, string>? _payloadFiles;

        /// <summary>
        /// The app files <see cref="RunFullAsync"/> installs (relative path → text),
        /// as if they were appended to the installer. Without it a full install has no payload.
        /// </summary>
        public Builder WithPayload(IReadOnlyDictionary<string, string> files)
        {
            ArgumentNullException.ThrowIfNull(files);
            _payloadFiles = new Dictionary<string, string>(files, StringComparer.Ordinal);
            return this;
        }


        /// <summary>The application id. Default <c>com.instella.tests.fake</c>.</summary>
        public Builder WithAppId(string appId)
        {
            ArgumentException.ThrowIfNullOrEmpty(appId);
            _appId = appId;
            return this;
        }

        /// <summary>The application name. Default <c>FakeApp</c>.</summary>
        public Builder WithAppName(string appName)
        {
            ArgumentException.ThrowIfNullOrEmpty(appName);
            _appName = appName;
            return this;
        }

        /// <summary>The version being installed. Default 1.0.0.</summary>
        public Builder WithVersion(Version version)
        {
            ArgumentNullException.ThrowIfNull(version);
            _version = version;
            return this;
        }

        /// <summary>The installer mode the steps see. Default first install.</summary>
        public Builder WithMode(InstallerMode mode)
        {
            _mode = mode;
            return this;
        }

        /// <summary>The install directory (inside the fake file system).</summary>
        public Builder WithInstallPath(string path)
        {
            ArgumentException.ThrowIfNullOrEmpty(path);
            _installPath = path;
            return this;
        }

        /// <summary>Arguments passed to the installer by <see cref="RunFullAsync"/>.</summary>
        public Builder WithCliArgs(params string[] args)
        {
            ArgumentNullException.ThrowIfNull(args);
            _cliArgs = args;
            return this;
        }

        /// <summary>Seeds the state of page <paramref name="pageId"/>, as if the user had filled it in.</summary>
        public Builder WithPageState(string pageId, Action<PageState> configure)
        {
            ArgumentException.ThrowIfNullOrEmpty(pageId);
            ArgumentNullException.ThrowIfNull(configure);
            if (!_pageStates.TryGetValue(pageId, out var state))
            {
                state = new PageState();
                _pageStates[pageId] = state;
            }
            configure(state);
            return this;
        }

        /// <summary>
        /// Elevation for <see cref="RunFullAsync"/>; default: not elevated, relaunches exit 0.
        /// </summary>
        public Builder WithElevationService(FakeElevationService elevation)
        {
            ArgumentNullException.ThrowIfNull(elevation);
            _elevationService = elevation;
            return this;
        }

        /// <summary>The installer <see cref="RunFullAsync"/> runs.</summary>
        public Builder WithInstaller(IInstellaInstaller installer)
        {
            ArgumentNullException.ThrowIfNull(installer);
            _installer = installer;
            return this;
        }

        /// <summary>Uses <paramref name="fileSystem"/> instead of a new <see cref="InMemoryFileSystem"/>.</summary>
        public Builder WithFileSystem(IFakeFileSystem fileSystem)
        {
            ArgumentNullException.ThrowIfNull(fileSystem);
            _fileSystem = fileSystem;
            return this;
        }

        /// <summary>Uses <paramref name="services"/> instead of a new <see cref="FakePlatformServices"/>.</summary>
        public Builder WithPlatformServices(FakePlatformServices services)
        {
            ArgumentNullException.ThrowIfNull(services);
            _platformServices = services;
            _platform = services.Platform;
            return this;
        }

        /// <summary>The platform the fakes emulate. Default: the host platform.</summary>
        public Builder WithPlatform(TargetPlatform platform)
        {
            _platform = platform;
            return this;
        }

        /// <summary>Per-user or machine-wide. Default per-user.</summary>
        public Builder WithScope(InstallationScope scope)
        {
            _scope = scope;
            return this;
        }

        /// <summary>The elevation mode in the manifest. Default user choice.</summary>
        public Builder WithElevation(ElevationMode elevation)
        {
            _elevation = elevation;
            return this;
        }

        /// <summary>The server URL in the manifest.</summary>
        public Builder WithServerUrl(string serverUrl)
        {
            ArgumentException.ThrowIfNullOrEmpty(serverUrl);
            _serverUrl = serverUrl;
            return this;
        }

        /// <summary>Captures log entries into <paramref name="sink"/> instead of a new one.</summary>
        public Builder WithLogSink(RecordingSink sink)
        {
            ArgumentNullException.ThrowIfNull(sink);
            _logSink = sink;
            return this;
        }

        /// <summary>Creates the harness.</summary>
        public InstellaTestHarness Build()
        {
            var sink = _logSink ?? new RecordingSink();
            var logger = new RecordingLogger(sink);
            var fs = _fileSystem ?? new InMemoryFileSystem();
            var platform = _platformServices ?? new FakePlatformServices(_platform);

            var manifest = new InstellaManifest
            {
                AppName = _appName,
                AppId = _appId,
                Version = _version,
                ServerUrl = _serverUrl,
                Elevation = _elevation,
                Channel = _channel,
            };

            var options = new InstallOptions
            {
                InstallPath = _installPath,
                Elevation = _elevation,
            };

            var context = new InstallContext
            {
                AppName = _appName,
                AppId = _appId,
                AppVersion = _version,
                InstallPath = _installPath,
                Mode = _mode,
                Scope = _scope,
                Manifest = manifest,
                Options = options,
                Platform = platform,
                FileSystem = fs,
                Log = logger,
            };

            var harness = new InstellaTestHarness(
                context,
                fs,
                platform,
                logger,
                sink,
                _pageStates,
                _installer,
                _cliArgs,
                _elevationService);
            if (_payloadFiles is not null) harness._payload = Zip(_payloadFiles);
            return harness;
        }

        private static byte[] Zip(Dictionary<string, string> files)
        {
            using var ms = new MemoryStream();
            using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (path, text) in files)
                {
                    using var entry = zip.CreateEntry(path.Replace('\\', '/')).Open();
                    entry.Write(System.Text.Encoding.UTF8.GetBytes(text));
                }
            }
            return ms.ToArray();
        }

        private static InstallerMode InstellaMode() => InstallerMode.FirstInstall;

        private static TargetPlatform DefaultPlatform()
        {
            if (OperatingSystem.IsWindows()) return TargetPlatform.Windows;
            if (OperatingSystem.IsMacOS()) return TargetPlatform.MacOS;
            return TargetPlatform.Linux;
        }
    }
}
