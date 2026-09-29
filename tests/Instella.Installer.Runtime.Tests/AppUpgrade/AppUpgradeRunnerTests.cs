using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Installation;
using Instella.Core.Platform;
using Instella.Installer.Runtime.AppUpgrade;
using Instella.Installer.Runtime.Migrations;
using Instella.Installer.Runtime.Tests.Migrations;
using Instella.Installer.Testing;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.AppUpgrade;

/// <summary>
/// <see cref="AppUpgradeRunner"/> on a fake program runner: one test per outcome, the progress
/// protocol, the output caps, and exactly what the program is started with.
/// </summary>
[TestFixture]
internal class AppUpgradeRunnerTests
{
    private static readonly string Root = OperatingSystem.IsWindows() ? @"C:\Apps\Example" : "/opt/example";
    private const string Declaration = """{"contractVersion":1,"program":"App.Upgrade","arguments":["--db","data.db"]}""";

    private InMemoryFileSystem _fs = null!;
    private FakePrograms _programs = null!;
    private RecordingLog _log = null!;
    private List<AppUpgradeProgress> _progress = null!;

    [SetUp]
    public void SetUp()
    {
        _fs = new InMemoryFileSystem();
        _programs = new FakePrograms();
        _log = new RecordingLog();
        _progress = [];
    }

    private static string Exe => OperatingSystem.IsWindows() ? "App.Upgrade.exe" : "App.Upgrade";

    private void Seed(string? declaration = Declaration, bool program = true)
    {
        if (declaration is not null) _fs.AddFile(Path.Combine(Root, "instella-upgrade.json"), Encoding.UTF8.GetBytes(declaration));
        if (program) _fs.AddFile(Path.Combine(Root, Exe), Encoding.UTF8.GetBytes("program"));
    }

    private AppUpgradeRequest Request(AppUpgradeLaunchMode mode = AppUpgradeLaunchMode.Update, IReadOnlyCollection<string>? files = null) => new()
    {
        Mode = mode,
        From = new Version(1, 0),
        To = mode == AppUpgradeLaunchMode.Uninstall ? null : new Version(2, 0),
        Scope = InstallationScope.PerUser,
        InstallPath = Root,
        AppId = "com.example.app",
        Platform = OperatingSystem.IsWindows() ? TargetPlatform.Windows : TargetPlatform.Linux,
        InstalledFiles = files ?? ["App.exe", Exe, "instella-upgrade.json"],
    };

    private Task<AppUpgradeResult> RunAsync(AppUpgradeRequest? request = null, AppUpgradeRunner? runner = null) =>
        (runner ?? new AppUpgradeRunner(_fs, _programs, _log))
            .RunAsync(request ?? Request(), new SyncProgress(_progress), CancellationToken.None);

    [Test]
    public async Task WithoutADeclaration_NothingRuns()
    {
        var result = await RunAsync();
        Assert.That(result.Outcome, Is.EqualTo(AppUpgradeOutcome.NotDeclared));
        Assert.That(result.Success, Is.True);
        Assert.That(_programs.CapturedRuns, Is.Empty);
    }

    [Test]
    public async Task AnInvalidDeclaration_FailsWithoutRunningAnything()
    {
        Seed("""{"contractVersion":2,"program":"App.Upgrade"}""");
        var result = await RunAsync();
        Assert.That(result.Outcome, Is.EqualTo(AppUpgradeOutcome.InvalidDeclaration));
        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("contract version 2"));
        Assert.That(_programs.CapturedRuns, Is.Empty);
    }

    [Test]
    public async Task AProgramThatIsNotOneOfTheAppsFiles_IsNeverRun()
    {
        Seed();
        var result = await RunAsync(Request(files: ["App.exe", "instella-upgrade.json"]));
        Assert.That(result.Outcome, Is.EqualTo(AppUpgradeOutcome.InvalidDeclaration));
        Assert.That(result.Message, Does.Contain("not one of the app's files"));
        Assert.That(_programs.CapturedRuns, Is.Empty);
    }

    [Test]
    public async Task AProgramMissingFromTheFolder_FailsAsNotStarted()
    {
        Seed(program: false);
        var result = await RunAsync();
        Assert.That(result.Outcome, Is.EqualTo(AppUpgradeOutcome.NotStarted));
        Assert.That(result.Message, Does.Contain("missing"));
    }

    [Test]
    public async Task TheProgram_IsStartedWithTheContract_InTheInstallFolder()
    {
        Seed();
        var result = await RunAsync();

        Assert.That(result.Outcome, Is.EqualTo(AppUpgradeOutcome.Succeeded));
        var start = _programs.CapturedRuns.Single();
        Assert.Multiple(() =>
        {
            Assert.That(start.ExePath, Is.EqualTo(Path.Combine(Root, Exe)));
            Assert.That(start.WorkingDirectory, Is.EqualTo(Root));
            Assert.That(start.Environment["INSTELLA_UPGRADE_CONTRACT"], Is.EqualTo("1"));
            Assert.That(start.Arguments, Is.EqualTo(new[]
            {
                "--instella-upgrade", "--contract", "1", "--mode", "update", "--from", "1.0.0", "--to", "2.0.0",
                "--scope", "user", "--install-path", Root, "--app-id", "com.example.app", "--db", "data.db",
            }));
        });
    }

    [TestCase(1, AppUpgradeOutcome.Failed)]
    [TestCase(7, AppUpgradeOutcome.Failed)]
    [TestCase(-1, AppUpgradeOutcome.Failed)]
    [TestCase(2, AppUpgradeOutcome.Refused)]
    [TestCase(3, AppUpgradeOutcome.NotUnderstood)]
    public async Task ANonZeroExit_IsAFailure(int code, AppUpgradeOutcome outcome)
    {
        Seed();
        _programs.Behaviour = (_, line) =>
        {
            line(ProgramStream.Error, "the schema is locked");
            return code;
        };
        var result = await RunAsync();
        Assert.That(result.Outcome, Is.EqualTo(outcome));
        Assert.That(result.ExitCode, Is.EqualTo(code));
        Assert.That(result.Success, Is.False);
        if (outcome is AppUpgradeOutcome.Failed or AppUpgradeOutcome.Refused)
            Assert.That(result.Message, Does.Contain("the schema is locked"), "the last stderr line explains it");
    }

    [Test]
    public async Task ATimeout_IsAFailure()
    {
        Seed();
        _programs.Behaviour = (_, _) => throw new TimeoutException("ended");
        var result = await RunAsync();
        Assert.That(result.Outcome, Is.EqualTo(AppUpgradeOutcome.TimedOut));
        Assert.That(result.Message, Does.Contain("30 minute(s)"));
    }

    [Test]
    public async Task AProgramThatCannotStart_IsAFailure()
    {
        Seed();
        _programs.Behaviour = (start, _) => throw new ProgramStartException(start.ExePath, "access is denied");
        var result = await RunAsync();
        Assert.That(result.Outcome, Is.EqualTo(AppUpgradeOutcome.NotStarted));
        Assert.That(result.Message, Does.Contain("access is denied"));
    }

    [Test]
    public async Task ProgressLines_DriveProgress_AndEverythingIsLogged()
    {
        Seed();
        _programs.Behaviour = (_, line) =>
        {
            line(ProgramStream.Output, "starting");
            line(ProgramStream.Output, "##instella progress 25 Migrating orders");
            line(ProgramStream.Output, "##instella progress 80");
            line(ProgramStream.Output, "##instella dance 3");
            line(ProgramStream.Error, "a warning");
            return 0;
        };
        await RunAsync();

        Assert.That(_progress, Is.EqualTo(new[]
        {
            new AppUpgradeProgress(0, "Upgrading your data"),
            new AppUpgradeProgress(0.25, "Migrating orders"),
            new AppUpgradeProgress(0.8, null),
        }));
        Assert.That(_log.Lines, Does.Contain("INFO app-upgrade: starting"));
        Assert.That(_log.Lines, Does.Contain("INFO app-upgrade: ##instella progress 25 Migrating orders"));
        Assert.That(_log.Lines, Does.Contain("WARN app-upgrade (stderr): a warning"));
        Assert.That(_log.Warnings.Any(l => l.Contains("unknown instruction 'dance'")), Is.True);
    }

    [Test]
    public async Task LongLines_AreCut_AndOutputPastTheCap_IsCountedNotLogged()
    {
        Seed();
        var line = new string('x', 10_000);
        _programs.Behaviour = (_, write) =>
        {
            write(ProgramStream.Output, line);
            for (var i = 0; i < 300; i++) write(ProgramStream.Output, new string('y', 4000));   // about 1.2 MB
            write(ProgramStream.Output, "##instella progress 90 still read after the cap");
            return 0;
        };
        await RunAsync();

        var logged = _log.Lines.Where(l => l.StartsWith("INFO app-upgrade: x", StringComparison.Ordinal)).Single();
        Assert.That(logged, Does.EndWith("(5904 more characters)"));
        var yLines = _log.Lines.Count(l => l.StartsWith("INFO app-upgrade: yyyy", StringComparison.Ordinal));
        Assert.That(yLines, Is.LessThan(300));
        Assert.That(_log.Warnings.Any(l => l.Contains("more output line(s) were not logged")), Is.True);
        Assert.That(_progress.Last(), Is.EqualTo(new AppUpgradeProgress(0.9, "still read after the cap")));
    }

    [Test]
    public async Task Uninstall_WithoutHandlesUninstall_IsSkipped()
    {
        Seed();
        var result = await RunAsync(Request(AppUpgradeLaunchMode.Uninstall));
        Assert.That(result.Outcome, Is.EqualTo(AppUpgradeOutcome.Skipped));
        Assert.That(_programs.CapturedRuns, Is.Empty);
    }

    private const string UninstallDeclaration = """{"contractVersion":1,"program":"App.Upgrade","handlesUninstall":true}""";

    [Test]
    public async Task Uninstall_RunsTheProgram_WhenItsBytesMatchTheRecord()
    {
        Seed(UninstallDeclaration);
        var request = Request(AppUpgradeLaunchMode.Uninstall) with
        {
            ExpectedHashes = new Dictionary<string, string> { [Exe] = Sha("program"), ["instella-upgrade.json"] = Sha(UninstallDeclaration) },
        };
        var result = await RunAsync(request);
        Assert.That(result.Outcome, Is.EqualTo(AppUpgradeOutcome.Succeeded));
        Assert.That(_programs.CapturedRuns.Single().Arguments.Take(9), Is.EqualTo(new[]
        {
            "--instella-uninstall", "--contract", "1", "--mode", "uninstall", "--from", "1.0.0", "--to", "none",
        }));
    }

    [Test]
    public async Task Uninstall_NeverRunsAProgramThatChangedSinceItWasInstalled()
    {
        Seed(UninstallDeclaration);
        var request = Request(AppUpgradeLaunchMode.Uninstall) with
        {
            ExpectedHashes = new Dictionary<string, string> { [Exe] = Sha("the installed bytes"), ["instella-upgrade.json"] = Sha(UninstallDeclaration) },
        };
        var result = await RunAsync(request);
        Assert.That(result.Outcome, Is.EqualTo(AppUpgradeOutcome.Skipped));
        Assert.That(result.Message, Does.Contain("changed since it was installed"));
        Assert.That(_programs.CapturedRuns, Is.Empty);
    }

    [Test]
    public async Task TheDeclaredTimeout_IsPassedToTheRunner()
    {
        Seed("""{"contractVersion":1,"program":"App.Upgrade","timeoutMinutes":45}""");
        var timeouts = new List<TimeSpan>();
        var runner = new AppUpgradeRunner(_fs, new TimeoutRecorder(timeouts), _log);
        await RunAsync(runner: runner);
        Assert.That(timeouts, Is.EqualTo(new[] { TimeSpan.FromMinutes(45) }));
    }

    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private sealed class SyncProgress(List<AppUpgradeProgress> sink) : IProgress<AppUpgradeProgress>
    {
        public void Report(AppUpgradeProgress value) => sink.Add(value);
    }

    private sealed class TimeoutRecorder(List<TimeSpan> timeouts) : IProgramRunner
    {
        public Task<int> RunAsync(string exePath, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<ProgramExit> RunCapturedAsync(ProgramStart start, Action<ProgramStream, string> onLine, TimeSpan timeout, CancellationToken ct)
        {
            timeouts.Add(timeout);
            return Task.FromResult(new ProgramExit(0, []));
        }
    }
}
