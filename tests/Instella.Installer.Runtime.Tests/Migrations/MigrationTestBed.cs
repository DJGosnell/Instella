using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Installation;
using Instella.Core.Manifest;
using Instella.Core.Platform;
using Instella.Installer.Runtime.Core;
using Instella.Installer.Runtime.Core.Processes;
using Instella.Installer.Runtime.Installation;
using Instella.Installer.Runtime.Migrations;
using Instella.Installer.Testing;

namespace Instella.Installer.Runtime.Tests.Migrations;

/// <summary>
/// An install context on the fakes, with every known folder rooted in a fake profile, fake
/// processes and a fake program runner: every condition and action can be driven from here.
/// </summary>
internal sealed class MigrationTestBed
{
    public MigrationTestBed()
    {
        Root = Path.Combine(Path.GetTempPath(), "instella-migration-tests", Guid.NewGuid().ToString("N"));
        Folders = new()
        {
            [KnownFolder.LocalAppData] = Path.Combine(Root, "Users", "u", "AppData", "Local"),
            [KnownFolder.RoamingAppData] = Path.Combine(Root, "Users", "u", "AppData", "Roaming"),
            [KnownFolder.ProgramFiles] = Path.Combine(Root, "Program Files"),
            [KnownFolder.ProgramFilesX86] = Path.Combine(Root, "Program Files (x86)"),
            [KnownFolder.ProgramData] = Path.Combine(Root, "ProgramData"),
        };
        UserStartMenu = Path.Combine(Folders[KnownFolder.RoamingAppData], "Microsoft", "Windows", "Start Menu", "Programs");
        CommonStartMenu = Path.Combine(Folders[KnownFolder.ProgramData], "Microsoft", "Windows", "Start Menu", "Programs");
        InstallPath = Path.Combine(Folders[KnownFolder.LocalAppData], "Programs", "ExampleApp");
    }

    public string Root { get; }
    public Dictionary<KnownFolder, string> Folders { get; }
    public string UserStartMenu { get; }
    public string CommonStartMenu { get; }
    public string InstallPath { get; set; }
    public InMemoryFileSystem FileSystem { get; } = new();
    /// <summary>Replaces <see cref="FileSystem"/> in the context (fault injection); seed through <see cref="FileSystem"/>.</summary>
    public Instella.Core.FileSystem.IFileSystem? FileSystemOverride { get; set; }
    public FakePlatformServices Platform { get; set; } = new();
    public RecordingLog Log { get; } = new();
    public FakeProcesses Processes { get; } = new();
    public FakePrograms Programs { get; } = new();
    public InstallerMode Mode { get; set; } = InstallerMode.FirstInstall;
    public InstallationScope Scope { get; set; } = InstallationScope.PerUser;
    public Version? PreviousVersion { get; set; }
    public IReadOnlyList<string>? PreviouslyCompleted { get; set; }
    public bool Preview { get; set; }
    public bool ForceClose { get; set; } = true;
    public AppRunningPrompt? Prompt { get; set; }

    public bool PerUser => Scope == InstallationScope.PerUser;

    public string PathOf(KnownFolder root, string relative) =>
        Path.Combine(root == KnownFolder.InstallFolder ? InstallPath : Resolver().Resolve(root, Scope, InstallPath)!,
            relative.Replace('/', Path.DirectorySeparatorChar));

    public void AddDirectory(KnownFolder root, string relative) => FileSystem.AddDirectory(PathOf(root, relative));

    public void AddFile(KnownFolder root, string relative, string content = "x") =>
        FileSystem.AddFile(PathOf(root, relative), Encoding.UTF8.GetBytes(content));

    public void SetRunValue(string name, string command) =>
        Platform.Registry.Set(PerUser ? RegistryHive.CurrentUser : RegistryHive.LocalMachine, RunCommand.RunKey, name,
            InstellaRegistryValueKind.String, command);

    public string? RunValue(string name) =>
        Platform.Registry.Get(PerUser ? RegistryHive.CurrentUser : RegistryHive.LocalMachine, RunCommand.RunKey, name) as string;

    public KnownFolderResolver Resolver() => new((folder, machine) => folder switch
    {
        KnownFolder.StartMenuPrograms => machine ? CommonStartMenu : UserStartMenu,
        _ => Folders.TryGetValue(folder, out var p) ? p : null,
    });

    public MigrationRuntime Runtime() => new()
    {
        Folders = Resolver(),
        ProcessFinder = Processes,
        ProcessCloser = Processes,
        Programs = Programs,
        Prompt = Prompt,
        ForceClose = ForceClose,
        IsPreview = Preview,
        UndoRoot = Root,
        UndoDirectory = Path.Combine(Root, "undo"),
    };

    public InstallContext Context()
    {
        var manifest = new InstellaManifest
        {
            AppName = "ExampleApp", AppId = "com.example.app", Version = new Version(2, 0, 0), ExecutableName = "ExampleApp.exe", ServerUrl = "",
        };
        var context = new InstallContext
        {
            AppName = "ExampleApp",
            AppId = "com.example.app",
            AppVersion = new Version(2, 0, 0),
            InstallPath = InstallPath,
            Mode = Mode,
            Scope = Scope,
            Manifest = manifest,
            Options = new InstallOptions { InstallPath = InstallPath },
            Platform = Platform,
            FileSystem = FileSystemOverride ?? FileSystem,
            Log = Log,
            ExistingInstallation = PreviousVersion is null ? null : new InstalledManifest
            {
                AppName = "ExampleApp", AppId = "com.example.app", Version = PreviousVersion, InstallDirectory = InstallPath,
                ExecutableName = "ExampleApp.exe", InstalledAt = DateTime.UtcNow, Files = [],
                InstalledPerUser = PerUser, CompletedMigrations = PreviouslyCompleted,
            },
        };
        context.Migrations = Runtime();
        return context;
    }

    public MigrationContext MigrationContext(InstallContext? install = null)
    {
        install ??= Context();
        return new MigrationContext(install, install.Migrations, new MigrationLogger(install.Log, "test"));
    }

    /// <summary>Evaluates <paramref name="condition"/> in a fresh context.</summary>
    public async Task<(bool Value, string? Reason)> EvaluateAsync(Condition condition)
    {
        var outcome = await condition.EvaluateAsync(MigrationContext(), CancellationToken.None);
        return (outcome.Value, outcome.Reason);
    }
}

/// <summary>Every log line, as "LEVEL message".</summary>
internal sealed class RecordingLog : Instella.Core.Logging.IInstellaLogger
{
    private readonly List<string> _lines = [];
    public IReadOnlyList<string> Lines { get { lock (_lines) return [.. _lines]; } }
    public IEnumerable<string> Warnings => Lines.Where(l => l.StartsWith("WARN ", StringComparison.Ordinal));
    private void Add(string level, string message) { lock (_lines) _lines.Add(level + " " + message); }
    public void Trace(string message) => Add("TRACE", message);
    public void Debug(string message) => Add("DEBUG", message);
    public void Info(string message) => Add("INFO", message);
    public void Warn(string message) => Add("WARN", message);
    public void Error(string message, Exception? exception = null) => Add("ERROR", message);
    public IDisposable Scope(string segment) => new MemoryStream();
    public bool IsEnabled(Instella.Core.Logging.InstellaLogLevel level) => true;
}

/// <summary>Processes "running" from a file path; closing removes them unless they refuse.</summary>
internal sealed class FakeProcesses : ILockingProcessFinder, IProcessCloser
{
    private readonly List<(LockingProcess Process, string Exe, bool Closes)> _running = [];
    private int _nextId = 50_000;

    public LockingProcess Start(string exePath, bool closes = true, bool canClose = true)
    {
        var p = new LockingProcess(_nextId++, Path.GetFileName(exePath), canClose);
        lock (_running) _running.Add((p, exePath, closes));
        return p;
    }

    public IReadOnlyList<string> Closed => _closed;
    private readonly List<string> _closed = [];

    public bool IsRunning(string exePath)
    {
        lock (_running) return _running.Any(r => string.Equals(r.Exe, exePath, StringComparison.OrdinalIgnoreCase));
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
            _closed.Add(_running[i].Exe);
            _running.RemoveAt(i);
            return Task.FromResult(true);
        }
    }
}

/// <summary>Records programs run and answers with <see cref="ExitCode"/>.</summary>
internal sealed class FakePrograms : IProgramRunner
{
    public int ExitCode { get; set; }
    public List<(string Exe, IReadOnlyList<string> Args)> Runs { get; } = [];

    public Task<int> RunAsync(string exePath, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        Runs.Add((exePath, arguments));
        return Task.FromResult(ExitCode);
    }
}

/// <summary>
/// A migration configured by delegates, with the protected conditions and actions made public so
/// tests can compose them.
/// </summary>
internal sealed class TestMigration : InstallMigration
{
    private readonly string _id;

    public TestMigration(string id = "test-migration", MigrationTiming timing = MigrationTiming.AfterCommit, int order = 0)
    {
        _id = id;
        TimingValue = timing;
        OrderValue = order;
    }

    public MigrationTiming TimingValue { get; set; }
    public int OrderValue { get; set; }
    public bool? RunOnceValue { get; set; }
    public string? DisplayNameValue { get; set; }
    public Func<TestMigration, Condition> WhenFactory { get; set; } = _ => Condition.Always;
    public Func<TestMigration, CancellationToken, Task> Body { get; set; } = (_, _) => Task.CompletedTask;
    public Func<TestMigration, CancellationToken, Task> Rollback { get; set; } = (_, _) => Task.CompletedTask;
    public int Executions { get; private set; }
    public int Rollbacks { get; private set; }

    public override string Id => _id;
    public override string DisplayName => DisplayNameValue ?? base.DisplayName;
    public override MigrationTiming Timing => TimingValue;
    public override int Order => OrderValue;
    public override bool RunOnce => RunOnceValue ?? base.RunOnce;

    protected override Condition When() => WhenFactory(this);

    protected override Task ExecuteAsync(CancellationToken ct)
    {
        Executions++;
        return Body(this, ct);
    }

    protected override Task RollbackAsync(CancellationToken ct)
    {
        Rollbacks++;
        return Rollback(this, ct);
    }

    public MigrationContext Ctx => Context;
    public new Condition IsFirstInstall() => base.IsFirstInstall();
    public new Condition IsUpgrade() => base.IsUpgrade();
    public new Condition IsFirstInstallOrUpgrade() => base.IsFirstInstallOrUpgrade();
    public new Condition IsRepair() => base.IsRepair();
    public new Condition IsUninstall() => base.IsUninstall();
    public new Condition UpgradingFrom(string range) => base.UpgradingFrom(range);
    public new Condition FileExists(KnownFolder root, string relative) => base.FileExists(root, relative);
    public new Condition FolderExists(KnownFolder root, string relative) => base.FolderExists(root, relative);
    public new Condition InstellaInstallationAt(KnownFolder root, string relative) => base.InstellaInstallationAt(root, relative);
    public new Condition RunValueExists(string name) => base.RunValueExists(name);
    public new Condition RunValuePointsInto(string name, MigrationFolder folder) => base.RunValuePointsInto(name, folder);
    public new Condition RegistryValueExists(RegistryHive hive, string keyPath, string name) => base.RegistryValueExists(hive, keyPath, name);
    public new Condition ProcessRunningIn(MigrationFolder folder) => base.ProcessRunningIn(folder);
    public new Condition IsWindows() => base.IsWindows();
    public new Condition IsPerUserInstall() => base.IsPerUserInstall();
    public new Condition IsMachineInstall() => base.IsMachineInstall();
    public new MigrationFolder Folder(KnownFolder root, string relative) => base.Folder(root, relative);
    public new Task StopProcessesInAsync(MigrationFolder folder, CancellationToken ct) => base.StopProcessesInAsync(folder, ct);
    public new Task<bool> RepointRunValueAsync(string name, MigrationFolder from, CancellationToken ct) => base.RepointRunValueAsync(name, from, ct);
    public new Task<bool> DeleteRunValueAsync(string name, MigrationFolder pointingInto, CancellationToken ct) => base.DeleteRunValueAsync(name, pointingInto, ct);
    public new Task<bool> AdoptRunValueAsync(string name, CancellationToken ct) => base.AdoptRunValueAsync(name, ct);
    public new Task<int> DeleteFilesAsync(MigrationFolder folder, IReadOnlyList<string> fileNames, CancellationToken ct) => base.DeleteFilesAsync(folder, fileNames, ct);
    public new Task<bool> DeleteFolderIfEmptyAsync(MigrationFolder folder, CancellationToken ct) => base.DeleteFolderIfEmptyAsync(folder, ct);
    public new Task<int> RunProgramAsync(string exePath, IReadOnlyList<string> arguments, IReadOnlyList<int> successExitCodes, CancellationToken ct) =>
        base.RunProgramAsync(exePath, arguments, successExitCodes, ct);
}
