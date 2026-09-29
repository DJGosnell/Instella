using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Installation;
using Instella.Core.Logging;
using Instella.Core.Manifest;
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Runtime.Installation;
using Instella.Installer.Runtime.Installation.BuiltIn;
using Instella.Installer.Runtime.Installation.Builders;
using Instella.Installer.Testing;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.AppUpgrade;

/// <summary>
/// The app's upgrade program in the install pipeline, through the real installer on the harness
/// fakes: the arguments for each mode, and a failure (exit code, timeout, bad declaration) rolling
/// the install back with exit 15.
/// </summary>
[TestFixture]
public class AppUpgradeInstallTests
{
    private static readonly string Upgrade = OperatingSystem.IsWindows() ? "ExampleApp.Upgrade.exe" : "ExampleApp.Upgrade";
    private const string Declaration = """{"contractVersion":1,"program":"ExampleApp.Upgrade"}""";

    private readonly string _installPath = Path.Combine(Path.GetTempPath(), "instella-app-upgrade", Guid.NewGuid().ToString("N"), "ExampleApp");
    private InMemoryFileSystem _fs = null!;
    private FakePlatformServices _platform = null!;
    private readonly List<InstellaTestHarness> _harnesses = [];

    [SetUp]
    public void SetUp()
    {
        _fs = new InMemoryFileSystem();
        _platform = new FakePlatformServices(OperatingSystem.IsWindows() ? Instella.Core.Platform.TargetPlatform.Windows
            : OperatingSystem.IsMacOS() ? Instella.Core.Platform.TargetPlatform.MacOS : Instella.Core.Platform.TargetPlatform.Linux);
    }

    [TearDown]
    public async Task TearDown()
    {
        foreach (var h in _harnesses) await h.DisposeAsync();
    }

    /// <summary>A harness on the shared file system and platform, installing <paramref name="version"/>.</summary>
    private InstellaTestHarness Harness(string version, string? declaration = Declaration,
        Func<InstallerBuilder, InstallerBuilder>? configure = null)
    {
        var builder = InstellaInstaller.Create()
            .WithApp("ExampleApp", "com.example.app", Version.Parse(version))
            .WithExecutableName("ExampleApp.exe")
            .WithElevation(ElevationMode.PerUser);
        var payload = new Dictionary<string, string>
        {
            ["ExampleApp.exe"] = $"app {version}",
            [Upgrade] = $"upgrade program {version}",
        };
        if (declaration is not null) payload["instella-upgrade.json"] = declaration;
        var harness = InstellaTestHarness.Create()
            .WithInstaller((configure?.Invoke(builder) ?? builder).Build())
            .WithFileSystem(_fs)
            .WithPlatformServices(_platform)
            .WithPayload(payload)
            .Build();
        _harnesses.Add(harness);
        return harness;
    }

    private Task<int> Install(InstellaTestHarness harness, params string[] extra) =>
        harness.RunFullWithArgsAsync(["--install", "--silent", "--path", _installPath, .. extra]);

    private string? Live(string rel)
    {
        var path = Path.Combine(_installPath, rel);
        return _fs.Exists(path) ? Encoding.UTF8.GetString(_fs.Snapshot()[Path.GetFullPath(path)]) : null;
    }

    private Task<InstalledManifest?> Manifest() => new InstallManifestWriter(_fs).ReadAsync(_installPath, CancellationToken.None);

    private static string[] ContractArgs(ProgramRun run) => run.Arguments.Take(15).ToArray();

    private string[] Expected(string mode, string from, string to) =>
    [
        "--instella-upgrade", "--contract", "1", "--mode", mode, "--from", from, "--to", to,
        "--scope", "user", "--install-path", _installPath, "--app-id", "com.example.app",
    ];

    private static string Log(InstellaTestHarness h) => string.Join("\n", h.LogSink.Entries.Select(e => $"{e.Level} {e.Message}"));

    [Test]
    public async Task AFirstInstall_RunsTheProgram_FromTheNewFiles_WithNoFromVersion()
    {
        var h = Harness("1.0");
        string? journalDuringRun = null;
        h.WhenProgramRuns(run =>
        {
            journalDuringRun = JournalState();
            Assert.That(_fs.Exists(run.Path), Is.True, "the new files are in place when the program runs");
            return ProgramOutcome.Exit(0, "##instella progress 50 Creating the database");
        });

        Assert.That(await Install(h), Is.EqualTo(0), Log(h));

        var run = h.ProgramRuns.Single();
        Assert.That(run.Path, Is.EqualTo(Path.Combine(_installPath, Upgrade)));
        Assert.That(run.WorkingDirectory, Is.EqualTo(_installPath));
        Assert.That(ContractArgs(run), Is.EqualTo(Expected("first-install", "none", "1.0.0")));
        Assert.That(journalDuringRun, Is.EqualTo("Committing"), "the commit is held while the program runs");
        Assert.That(JournalState(), Is.Null, "the transaction folder is gone after the install");
        Assert.That(Live("ExampleApp.exe"), Is.EqualTo("app 1.0"));
    }

    [Test]
    public async Task AFailingProgram_OnAFirstInstall_RemovesEverything_Exit15()
    {
        var h = Harness("1.0");
        h.WhenProgramRuns(_ => ProgramOutcome.Exit(1, "creating tables").WithErrorOutput("disk full"));

        Assert.That(await Install(h), Is.EqualTo((int)InstellaExitCode.InstallAppUpgradeFailed));

        Assert.That(Live("ExampleApp.exe"), Is.Null);
        Assert.That(await Manifest(), Is.Null);
        Assert.That(Log(h), Does.Contain("failed with exit code 1: disk full"));
        Assert.That(Log(h), Does.Contain("app-upgrade (stderr): disk full"));
    }

    [Test]
    public async Task AnUpgrade_PassesBothVersions_AndAFailure_RestoresThePreviousVersion()
    {
        Assert.That(await Install(Harness("1.0")), Is.EqualTo(0));

        var v2 = Harness("2.0");
        v2.WhenProgramRuns(_ => ProgramOutcome.Exit(1));
        Assert.That(await Install(v2), Is.EqualTo(15));

        Assert.That(ContractArgs(v2.ProgramRuns.Single()), Is.EqualTo(Expected("upgrade", "1.0.0", "2.0.0")));
        Assert.That(Live("ExampleApp.exe"), Is.EqualTo("app 1.0"), "the previous files are back");
        Assert.That(Live(Upgrade), Is.EqualTo("upgrade program 1.0"));
        Assert.That((await Manifest())!.Version, Is.EqualTo(new Version(1, 0, 0)));
        Assert.That(JournalState(), Is.Null);

        var retry = Harness("2.0");
        Assert.That(await Install(retry), Is.EqualTo(0), "a retry is a new operation");
        Assert.That(ContractArgs(retry.ProgramRuns.Single()), Is.EqualTo(Expected("upgrade", "1.0.0", "2.0.0")));
        Assert.That(Live("ExampleApp.exe"), Is.EqualTo("app 2.0"));
    }

    [Test]
    public async Task ARepair_PassesTheSameVersionTwice()
    {
        Assert.That(await Install(Harness("1.5")), Is.EqualTo(0));
        var repair = Harness("1.5");
        Assert.That(await Install(repair), Is.EqualTo(0));
        Assert.That(ContractArgs(repair.ProgramRuns.Single()), Is.EqualTo(Expected("repair", "1.5.0", "1.5.0")));
    }

    [Test]
    public async Task ADowngrade_PassesTheNewerVersionAsFrom_AndTheAppMayRefuseIt()
    {
        Assert.That(await Install(Harness("2.0")), Is.EqualTo(0));

        var older = Harness("1.0");
        older.WhenProgramRuns(_ => ProgramOutcome.Exit(2).WithErrorOutput("the database is from a newer version"));
        Assert.That(await Install(older, "--allow-downgrade"), Is.EqualTo(15));

        Assert.That(ContractArgs(older.ProgramRuns.Single()), Is.EqualTo(Expected("downgrade", "2.0.0", "1.0.0")));
        Assert.That(Log(older), Does.Contain("refused this change: the database is from a newer version"));
        Assert.That(Live("ExampleApp.exe"), Is.EqualTo("app 2.0"));
    }

    [Test]
    public async Task ATimeout_RollsBack_Exit15()
    {
        Assert.That(await Install(Harness("1.0")), Is.EqualTo(0));
        var v2 = Harness("2.0");
        v2.WhenProgramRuns(_ => ProgramOutcome.TimeOut());

        Assert.That(await Install(v2), Is.EqualTo(15));
        Assert.That(Live("ExampleApp.exe"), Is.EqualTo("app 1.0"));
        Assert.That(Log(v2), Does.Contain("did not finish within 30 minute(s)"));
    }

    [Test]
    public async Task AProgramThatCannotStart_RollsBack_Exit15()
    {
        var h = Harness("1.0");
        h.WhenProgramRuns(_ => ProgramOutcome.CannotStart("access is denied"));
        Assert.That(await Install(h), Is.EqualTo(15));
        Assert.That(Live("ExampleApp.exe"), Is.Null);
    }

    [TestCase("""{"contractVersion":2,"program":"ExampleApp.Upgrade"}""", "contract version 2")]
    [TestCase("""{"contractVersion":1,"program":"Missing.Upgrade"}""", "not one of the app's files")]
    [TestCase("""{"contractVersion":1,"program":"../ExampleApp.Upgrade"}""", "illegal segment")]
    [TestCase("not json", "is not valid")]
    public async Task ADeclarationThatCannotBeHonoured_FailsTheInstall_WithoutRunningAnything(string declaration, string reason)
    {
        Assert.That(await Install(Harness("1.0")), Is.EqualTo(0));
        var v2 = Harness("2.0", declaration);

        Assert.That(await Install(v2), Is.EqualTo(15));
        Assert.That(v2.ProgramRuns, Is.Empty);
        Assert.That(Log(v2), Does.Contain(reason));
        Assert.That(Live("ExampleApp.exe"), Is.EqualTo("app 1.0"));
    }

    [Test]
    public async Task WithoutADeclaration_NothingRuns_AndTheCommitIsNotHeld()
    {
        var h = Harness("1.0", declaration: null);
        Assert.That(await Install(h), Is.EqualTo(0));
        Assert.That(h.ProgramRuns, Is.Empty);
        Assert.That(Log(h), Does.Contain("app-upgrade: skipped"));
    }

    [Test]
    public async Task ALaterFinalizeStepFailing_RollsTheFilesBack_AndWarnsThatTheDataWasUpgraded()
    {
        Assert.That(await Install(Harness("1.0")), Is.EqualTo(0));
        var ranBefore = false;
        var v2 = Harness("2.0", configure: b => b
            .AddStep("start-service", s => s.InStage(InstallStage.Finalize)
                .Execute((_, _, _) => Task.FromResult(StepResult.Fail("the service would not start")))
                .NoRollbackNeeded("test")));
        v2.WhenProgramRuns(_ => { ranBefore = true; return ProgramOutcome.Exit(0); });

        var exit = await Install(v2);

        Assert.That(ranBefore, Is.True);
        Assert.That(exit, Is.AnyOf((int)InstellaExitCode.InstallGeneralFailure, (int)InstellaExitCode.InstallRollbackCompletedWithWarnings));
        Assert.That(Live("ExampleApp.exe"), Is.EqualTo("app 1.0"));
        Assert.That(Log(v2), Does.Contain("the app's upgrade program already ran"));
    }

    [Test]
    public async Task ACancelWhileTheProgramRuns_DoesNotRollTheFilesBackOverUpgradedData()
    {
        Assert.That(await Install(Harness("1.0")), Is.EqualTo(0));
        using var cts = new CancellationTokenSource();
        var laterStepRan = false;
        var v2 = Harness("2.0", configure: b => b
            .AddStep("start-service", s => s.InStage(InstallStage.Finalize)
                .Execute((_, _, _) => { laterStepRan = true; return Task.FromResult(StepResult.Ok); })
                .NoRollbackNeeded("test")));
        v2.WhenProgramRuns(_ => { cts.Cancel(); return ProgramOutcome.Exit(0); });

        var exit = await v2.RunFullWithArgsAsync(["--install", "--silent", "--path", _installPath], cts.Token);

        Assert.That(exit, Is.EqualTo(0), Log(v2));
        Assert.That(laterStepRan, Is.True, "the install runs to its end");
        Assert.That(Live("ExampleApp.exe"), Is.EqualTo("app 2.0"));
        Assert.That((await Manifest())!.Version, Is.EqualTo(new Version(2, 0, 0)));
        Assert.That(Log(v2), Does.Contain("can no longer be cancelled"));
    }

    // ---- Ordering and configuration ---------------------------------------------------------

    private static FrozenConfig Config(InstallerBuilder builder) => ((InstellaInstallerImpl)builder.Build()).ConfigForTests;

    private static InstallerBuilder App() =>
        InstellaInstaller.Create().WithApp("ExampleApp", "com.example.app", new Version(2, 0)).WithExecutableName("ExampleApp.exe");

    private static StepExecuteAsync Ok => (_, _, _) => Task.FromResult(StepResult.Ok);

    [Test]
    public void AppUpgrade_RunsRightAfterTheCommit_BeforeCustomFinalizeSteps_AndPointsOfNoReturn()
    {
        var config = Config(App()
            .AddStep("user-finalize", s => s.InStage(InstallStage.Finalize).Execute(Ok).NoRollbackNeeded("t"))
            .AddStep("ponr", s => s.InStage(InstallStage.Finalize).Execute(Ok).NoRollbackNeeded("t").PointOfNoReturn("t"))
            .AddStep("early", s => s.InStage(InstallStage.Finalize).Before(AppUpgradeStep.StepName).After(CommitTransactionStep.StepName)
                .Execute(Ok).NoRollbackNeeded("t"))
            .AddMigration(new Migrations.TestMigration("after")));
        var names = StepOrdering.BuildOrderedSteps(OfflineInstallRunner.BuildDefaultSteps(), config.UserSteps, config.MigrationsOrEmpty)
            .Select(s => s.Name).ToList();
        int At(string name) => names.IndexOf(name);

        Assert.That(At("early"), Is.EqualTo(At(CommitTransactionStep.StepName) + 1), "Before(\"app-upgrade\") opts in to running earlier");
        Assert.That(At(AppUpgradeStep.StepName), Is.EqualTo(At("early") + 1));
        Assert.That(At("user-finalize"), Is.GreaterThan(At(AppUpgradeStep.StepName)));
        Assert.That(At("ponr"), Is.GreaterThan(At(AppUpgradeStep.StepName)));
        Assert.That(names.Last(), Is.EqualTo("migration:after"));
    }

    [Test]
    public void WithoutCustomSteps_AppUpgrade_FollowsTheCommit()
    {
        var names = OfflineInstallRunner.BuildDefaultSteps().Select(s => s.Name).ToList();
        Assert.That(names.TakeLast(2), Is.EqualTo(new[] { CommitTransactionStep.StepName, AppUpgradeStep.StepName }));
    }

    [Test]
    public void APointOfNoReturn_BeforeAppUpgrade_IsRefusedAtBuild()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => App()
            .AddStep("ponr", s => s.InStage(InstallStage.Finalize).Before(AppUpgradeStep.StepName)
                .Execute(Ok).NoRollbackNeeded("t").PointOfNoReturn("t"))
            .Build());
        Assert.That(ex!.Message, Does.Contain("must run after 'app-upgrade'"));
    }

    [Test]
    public void TheStepName_IsReserved()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => App()
            .AddStep(AppUpgradeStep.StepName, s => s.Execute(Ok).NoRollbackNeeded("t")).Build());
        Assert.That(ex!.Message, Does.Contain("reserved for the app's upgrade program"));
    }

    [Test]
    public void TheProgressPage_ShowsUpgradingYourData() =>
        Assert.That(StepDisplayNames.For(new AppUpgradeStep()), Is.EqualTo("Upgrading your data"));

    // ---- Uninstall ---------------------------------------------------------------------------

    private const string HandlesUninstall = """{"contractVersion":1,"program":"ExampleApp.Upgrade","handlesUninstall":true}""";

    private Task<int> Uninstall(InstellaTestHarness harness) =>
        harness.RunFullWithArgsAsync(["--uninstall", "--silent", "--path", _installPath]);

    [Test]
    public async Task Uninstall_RunsTheHandlerFirst_BeforeMigrationsAndHooks()
    {
        var order = new List<string>();
        var migration = new Migrations.TestMigration("goodbye", Instella.Installer.Runtime.Migrations.MigrationTiming.Uninstall)
        {
            RunOnceValue = false,
            WhenFactory = t => t.IsUninstall(),
            Body = (_, _) => { order.Add("migration"); return Task.CompletedTask; },
        };
        var h = Harness("1.0", HandlesUninstall, b => b
            .AddMigration(migration)
            .AddStep("hooked", s => s.Execute(Ok).NoRollbackNeeded("t")
                .OnUninstall((_, _) => { order.Add("hook"); return Task.CompletedTask; })));
        h.WhenProgramRuns(run =>
        {
            if (run.Arguments[0] == "--instella-uninstall")
            {
                order.Add("program");
                Assert.That(_fs.Exists(Path.Combine(_installPath, "ExampleApp.exe")), Is.True, "nothing is removed yet");
            }
            return ProgramOutcome.Exit(0);
        });
        Assert.That(await Install(h), Is.EqualTo(0));

        Assert.That(await Uninstall(h), Is.EqualTo(0), Log(h));

        Assert.That(order, Is.EqualTo(new[] { "program", "migration", "hook" }));
        var run = h.ProgramRuns.Last();
        Assert.That(ContractArgs(run), Is.EqualTo(new[]
        {
            "--instella-uninstall", "--contract", "1", "--mode", "uninstall", "--from", "1.0.0", "--to", "none",
            "--scope", "user", "--install-path", _installPath, "--app-id", "com.example.app",
        }));
        Assert.That(Live("ExampleApp.exe"), Is.Null, "the uninstall went ahead");
    }

    [TestCase(1)]
    [TestCase(2)]
    public async Task Uninstall_ContinuesWhenTheHandlerFails(int exitCode)
    {
        var h = Harness("1.0", HandlesUninstall);
        Assert.That(await Install(h), Is.EqualTo(0));
        h.WhenProgramRuns(_ => ProgramOutcome.Exit(exitCode));

        Assert.That(await Uninstall(h), Is.EqualTo(0));
        Assert.That(Live("ExampleApp.exe"), Is.Null);
        Assert.That(Log(h), Does.Contain("uninstall handler did not succeed"));
    }

    [Test]
    public async Task Uninstall_ContinuesWhenTheHandlerTimesOut()
    {
        var h = Harness("1.0", HandlesUninstall);
        Assert.That(await Install(h), Is.EqualTo(0));
        h.WhenProgramRuns(_ => ProgramOutcome.TimeOut());

        Assert.That(await Uninstall(h), Is.EqualTo(0));
        Assert.That(Live("ExampleApp.exe"), Is.Null);
    }

    [Test]
    public async Task Uninstall_ContinuesWhenTheProgramIsMissing()
    {
        var h = Harness("1.0", HandlesUninstall);
        Assert.That(await Install(h), Is.EqualTo(0));
        await _fs.DeleteFileAsync(Path.Combine(_installPath, Upgrade), CancellationToken.None);

        Assert.That(await Uninstall(h), Is.EqualTo(0));
        Assert.That(h.ProgramRuns, Has.Count.EqualTo(1), "only the install ran it");
        Assert.That(Log(h), Does.Contain("is missing"));
    }

    [Test]
    public async Task Uninstall_WithoutHandlesUninstall_DoesNotRunTheProgram()
    {
        var h = Harness("1.0");
        Assert.That(await Install(h), Is.EqualTo(0));

        Assert.That(await Uninstall(h), Is.EqualTo(0));
        Assert.That(h.ProgramRuns, Has.Count.EqualTo(1), "only the install ran it");
    }

    [Test]
    public async Task Uninstall_NeverRunsAProgramThatWasChangedAfterInstall()
    {
        var h = Harness("1.0", HandlesUninstall);
        Assert.That(await Install(h), Is.EqualTo(0));
        _fs.AddFile(Path.Combine(_installPath, Upgrade), Encoding.UTF8.GetBytes("replaced by someone else"));

        Assert.That(await Uninstall(h), Is.EqualTo(0));
        Assert.That(h.ProgramRuns, Has.Count.EqualTo(1), "only the install ran it");
        Assert.That(Log(h), Does.Contain("changed since it was installed"));
    }

    private string? JournalState()
    {
        var txnRoot = Path.GetFullPath(Path.Combine(_installPath, ".instella", "txn"));
        var journal = _fs.Snapshot().Where(kv => kv.Key.StartsWith(txnRoot, StringComparison.OrdinalIgnoreCase)
                                                && kv.Key.EndsWith("journal.json", StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Value).SingleOrDefault();
        return journal is null ? null
            : System.Text.Json.JsonSerializer.Deserialize(journal, TransactionJournalJsonContext.Default.TransactionJournal)!.State.ToString();
    }
}
