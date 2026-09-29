using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Instella.Installer.Runtime.Migrations;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.AppUpgrade;

/// <summary>
/// <see cref="ProcessProgramRunner.RunCapturedAsync"/> with real processes (<c>cmd.exe</c>): output
/// capture per stream, exit codes, closed stdin, the timeout and a program that cannot start.
/// </summary>
[TestFixture]
[Platform("Win")]
internal class ProcessProgramRunnerTests
{
    private static readonly string Cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

    private static ProgramStart Start(string exe, params string[] args) =>
        new(exe, args, Path.GetTempPath(), new Dictionary<string, string> { ["INSTELLA_TEST_VALUE"] = "from-the-runner" });

    [Test]
    public async Task StdoutAndStderr_AreCapturedLineByLine_WithTheExitCode()
    {
        var lines = new List<(ProgramStream, string)>();
        var exit = await ProcessProgramRunner.Instance.RunCapturedAsync(
            Start(Cmd, "/c", "echo first& echo ##instella progress 50 half& echo oops 1>&2& echo %INSTELLA_TEST_VALUE%& exit /b 3"),
            (s, l) => lines.Add((s, l)), TimeSpan.FromMinutes(1), CancellationToken.None);

        Assert.That(exit.ExitCode, Is.EqualTo(3));
        Assert.That(exit.Notes, Is.Empty, "the program was put in a job object");
        Assert.That(lines, Does.Contain((ProgramStream.Output, "first")));
        Assert.That(lines, Does.Contain((ProgramStream.Output, "##instella progress 50 half")));
        Assert.That(lines, Does.Contain((ProgramStream.Output, "from-the-runner")), "the environment is passed");
        Assert.That(lines, Does.Contain((ProgramStream.Error, "oops ")));
    }

    [Test]
    public async Task TheWorkingDirectory_IsTheOneGiven()
    {
        var dir = Directory.CreateTempSubdirectory("instella-cwd-").FullName;
        try
        {
            var lines = new List<string>();
            await ProcessProgramRunner.Instance.RunCapturedAsync(
                new ProgramStart(Cmd, ["/c", "cd"], dir, new Dictionary<string, string>()),
                (_, l) => lines.Add(l), TimeSpan.FromMinutes(1), CancellationToken.None);
            Assert.That(lines, Does.Contain(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Stdin_IsClosed_SoAProgramThatReadsItDoesNotWait()
    {
        var findstr = Path.Combine(Environment.SystemDirectory, "findstr.exe");
        var clock = Stopwatch.StartNew();
        var exit = await ProcessProgramRunner.Instance.RunCapturedAsync(
            Start(findstr, "x"), (_, _) => { }, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.That(exit.ExitCode, Is.EqualTo(1), "findstr found nothing on an empty stdin");
        Assert.That(clock.Elapsed, Is.LessThan(TimeSpan.FromSeconds(20)));
    }

    [Test]
    public void ATimeout_EndsTheProgram_AndThrows()
    {
        var clock = Stopwatch.StartNew();
        var ex = Assert.ThrowsAsync<TimeoutException>(() => ProcessProgramRunner.Instance.RunCapturedAsync(
            Start(Cmd, "/c", "ping -n 60 127.0.0.1"), (_, _) => { }, TimeSpan.FromSeconds(1), CancellationToken.None));
        Assert.That(ex!.Message, Does.Contain("did not finish within 1 second(s)"));
        Assert.That(clock.Elapsed, Is.LessThan(TimeSpan.FromSeconds(30)), "the tree was ended, not waited for");
    }

    [Test]
    public void AMissingProgram_CannotStart()
    {
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".exe");
        var ex = Assert.ThrowsAsync<ProgramStartException>(() => ProcessProgramRunner.Instance.RunCapturedAsync(
            Start(missing), (_, _) => { }, TimeSpan.FromSeconds(10), CancellationToken.None));
        Assert.That(ex!.Reason, Does.Contain("does not exist"));
    }

    [Test]
    public void AFileThatIsNotAProgram_CannotStart()
    {
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllText(file, "not a program");
        try
        {
            Assert.ThrowsAsync<ProgramStartException>(() => ProcessProgramRunner.Instance.RunCapturedAsync(
                Start(file), (_, _) => { }, TimeSpan.FromSeconds(10), CancellationToken.None));
        }
        finally
        {
            File.Delete(file);
        }
    }
}
