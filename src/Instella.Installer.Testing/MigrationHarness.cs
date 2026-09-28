using Instella.Core.Installation;
using Instella.Core.Logging;
using Instella.Core.Manifest;
using Instella.Core.Platform;
using Instella.Installer.Runtime.Core;
using Instella.Installer.Runtime.Core.Processes;
using Instella.Installer.Runtime.Installation;
using Instella.Installer.Runtime.Migrations;

namespace Instella.Installer.Testing;

/// <summary>
/// Runs one <see cref="InstallMigration"/> in isolation against in-memory fakes: a file system
/// with every <see cref="KnownFolder"/> rooted in a fake profile, a registry, running processes and
/// programs. Nothing on the machine is touched. Set up the scenario, call <see cref="RunAsync"/>,
/// and assert on the <see cref="MigrationResult"/>.
/// </summary>
/// <example>
/// <code>
/// var result = await MigrationHarness.For&lt;ReplaceOldCopy&gt;()
///     .WithFile(KnownFolder.LocalAppData, "ExampleApp/ExampleApp.exe")
///     .WithRunningProcess(KnownFolder.LocalAppData, "ExampleApp/ExampleApp.exe")
///     .RunAsync();
/// Assert.That(result.Outcome, Is.EqualTo(MigrationOutcome.Completed));
/// </code>
/// </example>
public sealed class MigrationHarness
{
    private readonly InstallMigration _migration;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "instella-migration-harness", Guid.NewGuid().ToString("N"));
    private readonly List<Action<InMemoryFileSystem, FakePlatformServices, HarnessProcesses>> _seeds = [];
    private InstallerMode? _mode;
    private Version? _previousVersion;
    private InstallationScope? _scope;
    private bool _elevated;
    private bool _preview;
    private string? _installPath;
    private string _appName = "ExampleApp";
    private string _appId = "com.example.app";
    private string _executableName = "ExampleApp.exe";
    private Version _appVersion = new(2, 0, 0);
    private bool _alreadyCompleted;
    private bool _forceClose = true;
    private int _programExitCode;
    private bool _failLaterStep;

    private MigrationHarness(InstallMigration migration)
    {
        _migration = migration;
    }

    /// <summary>A harness for a new <typeparamref name="T"/>.</summary>
    public static MigrationHarness For<T>() where T : InstallMigration, new() => new(new T());

    /// <summary>A harness for <paramref name="migration"/>.</summary>
    public static MigrationHarness For(InstallMigration migration)
    {
        ArgumentNullException.ThrowIfNull(migration);
        return new(migration);
    }

    /// <summary>
    /// The installer mode. Defaults to <see cref="InstallerMode.FirstInstall"/>, or
    /// <see cref="InstallerMode.Upgrade"/> once <see cref="PreviousVersion"/> is set, or
    /// <see cref="InstallerMode.Uninstall"/> for an uninstall migration.
    /// </summary>
    public MigrationHarness Mode(InstallerMode mode)
    {
        _mode = mode;
        return this;
    }

    /// <summary>The version installed before this run (the one upgraded, repaired or uninstalled).</summary>
    public MigrationHarness PreviousVersion(string version)
    {
        ArgumentException.ThrowIfNullOrEmpty(version);
        _previousVersion = Version.Parse(version.Contains('.') ? version : version + ".0");
        return this;
    }

    /// <summary>Per-user (the default) or machine-wide.</summary>
    public MigrationHarness Scope(InstallationScope scope)
    {
        _scope = scope;
        return this;
    }

    /// <summary>
    /// The installer runs as administrator. Unless <see cref="Scope"/> is called, this means a
    /// machine-wide install, where per-user known folders and HKCU are off limits.
    /// </summary>
    public MigrationHarness Elevated(bool elevated = true)
    {
        _elevated = elevated;
        return this;
    }

    /// <summary>Preview: the actions report what they would do (<see cref="MigrationResult.PlannedActions"/>) and change nothing.</summary>
    public MigrationHarness Preview()
    {
        _preview = true;
        return this;
    }

    /// <summary>The folder being installed. Defaults to <c>LocalAppData/Programs/&lt;app&gt;</c> (per-user) or <c>ProgramFiles/&lt;app&gt;</c>.</summary>
    public MigrationHarness InstallPath(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        _installPath = path;
        return this;
    }

    /// <summary>The app being installed. Defaults to <c>ExampleApp</c>, <c>com.example.app</c>, <c>ExampleApp.exe</c>, version 2.0.0.</summary>
    public MigrationHarness WithApp(string name, string appId, string executableName, string version = "2.0.0")
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(appId);
        ArgumentException.ThrowIfNullOrEmpty(executableName);
        _appName = name;
        _appId = appId;
        _executableName = executableName;
        _appVersion = Version.Parse(version);
        return this;
    }

    /// <summary>The installation has already completed this migration (its id is in the installed manifest).</summary>
    public MigrationHarness AlreadyCompleted()
    {
        _alreadyCompleted = true;
        return this;
    }

    /// <summary>A file under a known folder (<paramref name="relative"/> uses <c>/</c> or <c>\</c>).</summary>
    public MigrationHarness WithFile(KnownFolder root, string relative, string content = "")
    {
        _seeds.Add((fs, _, _) => fs.AddFile(PathOf(root, relative), System.Text.Encoding.UTF8.GetBytes(content)));
        return this;
    }

    /// <summary>An empty folder under a known folder.</summary>
    public MigrationHarness WithFolder(KnownFolder root, string relative)
    {
        _seeds.Add((fs, _, _) => fs.AddDirectory(PathOf(root, relative)));
        return this;
    }

    /// <summary>An Instella installation (a folder with <c>.instella-manifest.json</c>) under a known folder.</summary>
    public MigrationHarness WithInstellaInstallation(KnownFolder root, string relative) =>
        WithFile(root, relative.TrimEnd('/', '\\') + "/" + InstellaOwnedPaths.InstalledManifest, "{}");

    /// <summary>A Run value in the scope's Run key (HKCU per-user, HKLM machine-wide). Use <see cref="PathOf"/> to build the command.</summary>
    public MigrationHarness WithRunValue(string name, string command)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(command);
        _seeds.Add((_, platform, _) => platform.Registry.Set(RunHive, RunCommand.RunKey, name, InstellaRegistryValueKind.String, command));
        return this;
    }

    /// <summary>A string registry value.</summary>
    public MigrationHarness WithRegistryValue(RegistryHive hive, string keyPath, string name, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(keyPath);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);
        _seeds.Add((_, platform, _) => platform.Registry.Set(hive, keyPath, name, InstellaRegistryValueKind.String, value));
        return this;
    }

    /// <summary>
    /// A program running from <paramref name="relativeExe"/> under <paramref name="root"/> (the
    /// file is created too). With <paramref name="closes"/> false it refuses to close.
    /// </summary>
    public MigrationHarness WithRunningProcess(KnownFolder root, string relativeExe, bool closes = true)
    {
        _seeds.Add((fs, _, processes) =>
        {
            var exe = PathOf(root, relativeExe);
            if (!fs.Exists(exe)) fs.AddFile(exe, [0x4D, 0x5A]);
            processes.Start(exe, closes);
        });
        return this;
    }

    /// <summary>
    /// Whether programs may be closed without asking (<c>--force-close</c>). Defaults to true; false
    /// behaves as a silent install without <c>--force-close</c>, which closes nothing.
    /// </summary>
    public MigrationHarness ForceClose(bool forceClose = true)
    {
        _forceClose = forceClose;
        return this;
    }

    /// <summary>
    /// The installer may read <paramref name="relative"/> under <paramref name="root"/> (a file or a
    /// folder, and everything in it) but not change it: deletes, moves and writes fail with access
    /// denied, as for a per-user installer in Program Files. Use an empty <paramref name="relative"/>
    /// for the known folder itself.
    /// </summary>
    public MigrationHarness DenyWrites(KnownFolder root, string relative)
    {
        _seeds.Add((fs, _, _) => fs.DenyWrites(PathOf(root, relative)));
        return this;
    }

    /// <summary>
    /// The installer may not read <paramref name="relative"/> under <paramref name="root"/>: it
    /// looks absent (as <c>File.Exists</c> reports it), listing it fails, and writes fail.
    /// </summary>
    public MigrationHarness DenyReads(KnownFolder root, string relative)
    {
        _seeds.Add((fs, _, _) => fs.DenyReads(PathOf(root, relative)));
        return this;
    }

    /// <summary>The scope's Run key can be read but not changed: repointing, deleting or restoring a Run value fails.</summary>
    public MigrationHarness DenyRunKeyWrites()
    {
        _seeds.Add((_, platform, _) => platform.DenyRegistryWrites(RunHive, RunCommand.RunKey));
        return this;
    }

    /// <summary>The scope's Run key cannot be read: every Run value looks absent.</summary>
    public MigrationHarness DenyRunKeyReads()
    {
        _seeds.Add((_, platform, _) => platform.DenyRegistryReads(RunHive, RunCommand.RunKey));
        return this;
    }

    /// <summary>The exit code every program run with <c>RunProgramAsync</c> returns. Defaults to 0.</summary>
    public MigrationHarness ProgramExitCode(int exitCode)
    {
        _programExitCode = exitCode;
        return this;
    }

    /// <summary>A step after the migration fails the install, so a <see cref="MigrationTiming.BeforeCommit"/> migration is rolled back.</summary>
    public MigrationHarness FailLaterStep()
    {
        _failLaterStep = true;
        return this;
    }

    /// <summary>
    /// The full path <paramref name="relative"/> under <paramref name="root"/> has in this harness
    /// (for Run value commands and assertions). <see cref="KnownFolder.StartMenuPrograms"/> follows the scope.
    /// </summary>
    public string PathOf(KnownFolder root, string relative)
    {
        ArgumentNullException.ThrowIfNull(relative);
        var basePath = root == KnownFolder.InstallFolder ? EffectiveInstallPath : Lookup(root, EffectiveScope == InstallationScope.SystemWide)
            ?? throw new ArgumentException($"{root} has no path", nameof(root));
        return relative.Length == 0 ? basePath : Path.Combine(basePath, relative.Replace('\\', '/').Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>
    /// Validates the migration as <c>InstallerBuilder.Build()</c> would, then runs it: an install
    /// migration inside an install step list (with rollback when <see cref="FailLaterStep"/> is
    /// set), an uninstall migration as uninstall does.
    /// </summary>
    /// <exception cref="InvalidOperationException">The migration would be refused by <c>Build()</c>.</exception>
    public async Task<MigrationResult> RunAsync(CancellationToken ct = default)
    {
        MigrationValidation.ValidateAndSort([_migration]);

        var fs = new InMemoryFileSystem();
        var platform = new FakePlatformServices();
        var processes = new HarnessProcesses();
        var programs = new HarnessPrograms(_programExitCode);
        var log = new HarnessLog();
        foreach (var seed in _seeds) seed(fs, platform, processes);
        var registryBefore = platform.Registry.Snapshot();

        var mode = EffectiveMode;
        var scope = EffectiveScope;
        var installPath = EffectiveInstallPath;
        var existing = mode == InstallerMode.FirstInstall ? null : new InstalledManifest
        {
            AppName = _appName, AppId = _appId, Version = _previousVersion ?? _appVersion, InstallDirectory = installPath,
            ExecutableName = _executableName, InstalledAt = DateTime.UtcNow, Files = [],
            InstalledPerUser = scope == InstallationScope.PerUser,
            CompletedMigrations = _alreadyCompleted ? [_migration.Id] : null,
        };
        var context = new InstallContext
        {
            AppName = _appName,
            AppId = _appId,
            AppVersion = _appVersion,
            InstallPath = installPath,
            Mode = mode,
            Scope = scope,
            Manifest = new InstellaManifest
            {
                AppName = _appName, AppId = _appId, Version = _appVersion, ExecutableName = _executableName, ServerUrl = "",
            },
            Options = new InstallOptions { InstallPath = installPath },
            Platform = platform,
            FileSystem = fs,
            Log = log,
            ExistingInstallation = existing,
        };
        var runtime = new MigrationRuntime
        {
            Folders = new KnownFolderResolver(Lookup),
            ProcessFinder = processes,
            ProcessCloser = processes,
            Programs = programs,
            ForceClose = _forceClose,
            IsPreview = _preview,
            UndoDirectory = Path.Combine(_root, "undo"),
        };
        context.Migrations = runtime;

        if (_migration.Timing == MigrationTiming.Uninstall)
        {
            await MigrationExecution.RunAsync(_migration, context, ct);
        }
        else
        {
            var steps = new List<IInstallStepExecution> { new MigrationStep(_migration) };
            if (_failLaterStep) steps.Add(new FailingStep());
            await new StepExecutor(steps).ExecuteAsync(context, null, ct);
        }

        var record = runtime.Records.LastOrDefault()
            ?? new MigrationRunRecord(_migration.Id, MigrationRunOutcome.Skipped, "the migration did not run", []);
        return new MigrationResult(
            record,
            recorded: runtime.Completed.Contains(_migration.Id, StringComparer.Ordinal),
            adopted: [.. runtime.Adopted],
            registryChanges: MigrationResult.Diff(registryBefore, platform.Registry.Snapshot()),
            programsRun: programs.Runs,
            logLines: log.Lines,
            fs,
            platform,
            PathOf,
            RunHive);
    }

    private InstallerMode EffectiveMode =>
        _mode ?? (_migration.Timing == MigrationTiming.Uninstall ? InstallerMode.Uninstall
            : _previousVersion is not null ? InstallerMode.Upgrade : InstallerMode.FirstInstall);

    private InstallationScope EffectiveScope =>
        _scope ?? (_elevated ? InstallationScope.SystemWide : InstallationScope.PerUser);

    private RegistryHive RunHive => EffectiveScope == InstallationScope.PerUser ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;

    private string EffectiveInstallPath => _installPath ?? (EffectiveScope == InstallationScope.PerUser
        ? Path.Combine(Lookup(KnownFolder.LocalAppData, false)!, "Programs", _appName)
        : Path.Combine(Lookup(KnownFolder.ProgramFiles, true)!, _appName));

    private string? Lookup(KnownFolder folder, bool machine)
    {
        var profile = Path.Combine(_root, "Users", "user");
        var programData = Path.Combine(_root, "ProgramData");
        return folder switch
        {
            KnownFolder.LocalAppData => Path.Combine(profile, "AppData", "Local"),
            KnownFolder.RoamingAppData => Path.Combine(profile, "AppData", "Roaming"),
            KnownFolder.StartMenuPrograms => machine
                ? Path.Combine(programData, "Microsoft", "Windows", "Start Menu", "Programs")
                : Path.Combine(profile, "AppData", "Roaming", "Microsoft", "Windows", "Start Menu", "Programs"),
            KnownFolder.ProgramFiles => Path.Combine(_root, "Program Files"),
            KnownFolder.ProgramFilesX86 => Path.Combine(_root, "Program Files (x86)"),
            KnownFolder.ProgramData => programData,
            KnownFolder.InstallFolder => EffectiveInstallPath,
            _ => null,
        };
    }

    private sealed class FailingStep : IInstallStepExecution
    {
        public string Name => "later-step";
        public InstallStage Stage => InstallStage.Finalize;
        public int Weight => 1;
        public Task<StepResult> ExecuteAsync(InstallContext context, IStepProgress progress, CancellationToken cancellationToken) =>
            Task.FromResult(StepResult.Fail("a later step failed (MigrationHarness.FailLaterStep)"));
    }
}

/// <summary>Fake running programs: found by the files they hold; closing removes them unless they refuse.</summary>
internal sealed class HarnessProcesses : ILockingProcessFinder, IProcessCloser
{
    private readonly List<(LockingProcess Process, string Exe, bool Closes)> _running = [];
    private int _nextId = 40_000;

    public void Start(string exe, bool closes)
    {
        lock (_running) _running.Add((new LockingProcess(_nextId++, Path.GetFileName(exe), CanClose: true), exe, closes));
    }

    public bool IsRunning(string exe)
    {
        lock (_running) return _running.Any(r => string.Equals(r.Exe, exe, StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<LockingProcess> Find(IReadOnlyList<string> files)
    {
        lock (_running)
            return _running.Where(r => files.Contains(r.Exe, StringComparer.OrdinalIgnoreCase)).Select(r => r.Process).ToList();
    }

    public Task<bool> CloseAsync(LockingProcess process, TimeSpan timeout, CancellationToken ct)
    {
        lock (_running)
        {
            var i = _running.FindIndex(r => r.Process.Id == process.Id);
            if (i < 0) return Task.FromResult(true);
            if (!_running[i].Closes) return Task.FromResult(false);
            _running.RemoveAt(i);
            return Task.FromResult(true);
        }
    }
}

/// <summary>Records programs instead of starting them.</summary>
internal sealed class HarnessPrograms(int exitCode) : IProgramRunner
{
    public List<string> Runs { get; } = [];

    public Task<int> RunAsync(string exePath, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        Runs.Add(arguments.Count == 0 ? exePath : $"{exePath} {string.Join(' ', arguments)}");
        return Task.FromResult(exitCode);
    }
}

/// <summary>Every log line as <c>LEVEL message</c>.</summary>
internal sealed class HarnessLog : IInstellaLogger
{
    private readonly List<string> _lines = [];
    public IReadOnlyList<string> Lines { get { lock (_lines) return [.. _lines]; } }
    private void Add(string level, string message) { lock (_lines) _lines.Add(level + " " + message); }
    public void Trace(string message) => Add("TRACE", message);
    public void Debug(string message) => Add("DEBUG", message);
    public void Info(string message) => Add("INFO", message);
    public void Warn(string message) => Add("WARN", message);
    public void Error(string message, Exception? exception = null) => Add("ERROR", message);
    public IDisposable Scope(string segment) => new MemoryStream();
    public bool IsEnabled(InstellaLogLevel level) => true;
}
