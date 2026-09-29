using System.Text;
using Instella.Core.Installation;
using Instella.Core.Manifest;
using Instella.Installer.Runtime.Builders;
using NUnit.Framework;

namespace Instella.Installer.Testing.Tests;

/// <summary>
/// <see cref="InstellaTestHarness.WhenProgramRuns"/> and <see cref="InstellaTestHarness.ProgramRuns"/>,
/// used the way an app's tests would: check the app's <c>instella-upgrade.json</c> is honoured, and
/// what the installer does with each way the program can end. Nothing is ever really started.
/// </summary>
[TestFixture]
public class UpgradeProgramHarnessTests
{
    private static readonly string Upgrade = OperatingSystem.IsWindows() ? "App.Upgrade.exe" : "App.Upgrade";
    private readonly string _installPath = Path.Combine(Path.GetTempPath(), "instella-harness-upgrade", Guid.NewGuid().ToString("N"));
    private InstellaTestHarness _harness = null!;

    [SetUp]
    public void SetUp()
    {
        var installer = InstellaInstaller.Create()
            .WithApp("App", "com.example.app", new Version(1, 4))
            .WithExecutableName("App.exe")
            .WithElevation(ElevationMode.PerUser)
            .Build();
        _harness = InstellaTestHarness.Create()
            .WithInstaller(installer)
            .WithPayload(new Dictionary<string, string>
            {
                ["App.exe"] = "app",
                [Upgrade] = "upgrade program",
                ["instella-upgrade.json"] = """{"contractVersion":1,"program":"App.Upgrade","arguments":["--verbose"]}""",
            })
            .Build();
    }

    [TearDown]
    public async Task TearDown() => await _harness.DisposeAsync();

    private Task<int> Install() => _harness.RunFullWithArgsAsync(["--install", "--silent", "--path", _installPath]);

    private bool Installed => _harness.FileSystem.Exists(Path.Combine(_installPath, "App.exe"));

    private string Log => string.Join("\n", _harness.LogSink.Entries.Select(e => e.Message));

    [Test]
    public async Task ByDefault_TheProgramSucceeds_AndItsRunIsRecorded()
    {
        Assert.That(await Install(), Is.EqualTo(0));

        var run = _harness.ProgramRuns.Single();
        Assert.That(run.Path, Is.EqualTo(Path.Combine(_installPath, Upgrade)));
        Assert.That(run.WorkingDirectory, Is.EqualTo(_installPath));
        Assert.That(run.Arguments, Is.EqualTo(new[]
        {
            "--instella-upgrade", "--contract", "1", "--mode", "first-install", "--from", "none", "--to", "1.4.0",
            "--scope", "user", "--install-path", _installPath, "--app-id", "com.example.app", "--verbose",
        }));
        Assert.That(Installed, Is.True);
    }

    [Test]
    public async Task AFailingProgram_RollsTheInstallBack_WithExit15_AndItsOutputIsLogged()
    {
        _harness.WhenProgramRuns(_ => ProgramOutcome.Exit(1, "opening the database").WithErrorOutput("the database is locked"));

        Assert.That(await Install(), Is.EqualTo((int)InstellaExitCode.InstallAppUpgradeFailed));
        Assert.That(Installed, Is.False);
        Assert.That(Log, Does.Contain("app-upgrade: opening the database"));
        Assert.That(Log, Does.Contain("app-upgrade (stderr): the database is locked"));
    }

    [Test]
    public async Task AProgramThatTimesOut_RollsTheInstallBack()
    {
        _harness.WhenProgramRuns(_ => ProgramOutcome.TimeOut());
        Assert.That(await Install(), Is.EqualTo(15));
        Assert.That(Installed, Is.False);
    }

    [Test]
    public async Task AProgramThatCannotStart_RollsTheInstallBack()
    {
        _harness.WhenProgramRuns(_ => ProgramOutcome.CannotStart("access is denied"));
        Assert.That(await Install(), Is.EqualTo(15));
        Assert.That(Log, Does.Contain("access is denied"));
    }

    [Test]
    public async Task TheBehaviour_CanSimulateTheProgramsData()
    {
        var data = Path.Combine(Path.GetTempPath(), "instella-harness-data", Guid.NewGuid().ToString("N"), "app.db");
        _harness.WhenProgramRuns(run =>
        {
            ((IFakeFileSystem)_harness.FileSystem).AddFile(data, Encoding.UTF8.GetBytes("schema " + run.Arguments[8]));
            return ProgramOutcome.Exit(0, "##instella progress 100 Database ready");
        });

        Assert.That(await Install(), Is.EqualTo(0));
        Assert.That(Encoding.UTF8.GetString(_harness.FileSystem.Snapshot()[Path.GetFullPath(data)]), Is.EqualTo("schema 1.4.0"));
    }

    [Test]
    public void Outcomes_CarryWhatTheyWereGiven()
    {
        var exit = ProgramOutcome.Exit(2, "a", "b").WithErrorOutput("c");
        Assert.Multiple(() =>
        {
            Assert.That(exit.ExitCode, Is.EqualTo(2));
            Assert.That(exit.Output, Is.EqualTo(new[] { "a", "b" }));
            Assert.That(exit.ErrorOutput, Is.EqualTo(new[] { "c" }));
            Assert.That(exit.TimesOut, Is.False);
            Assert.That(exit.StartError, Is.Null);
            Assert.That(ProgramOutcome.TimeOut().TimesOut, Is.True);
            Assert.That(ProgramOutcome.CannotStart("gone").StartError, Is.EqualTo("gone"));
        });
        Assert.Throws<ArgumentException>(() => ProgramOutcome.CannotStart(""));
        Assert.Throws<ArgumentNullException>(() => _harness.WhenProgramRuns(null!));
    }
}
