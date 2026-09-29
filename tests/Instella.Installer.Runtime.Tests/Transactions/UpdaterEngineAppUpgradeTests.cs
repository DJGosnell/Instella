using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Diff;
using Instella.Core.Installation;
using Instella.Core.Update;
using Instella.Installer.Runtime.Core.Transactions;
using Instella.Installer.Runtime.Core.Update;
using Instella.Installer.Runtime.Migrations;
using Instella.Installer.Runtime.Tests.Migrations;
using Instella.Installer.Testing;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Transactions;

/// <summary>
/// The app's upgrade program during an in-app update: it runs on the new files between the held
/// commit and completion. A failure (exit code, timeout, bad declaration) puts the previous files
/// back and exits 25; a crash while it runs is rolled back by recovery.
/// </summary>
public partial class UpdaterEngineTests
{
    private const string UpgradeDeclaration = """{"contractVersion":1,"program":"App.Upgrade"}""";

    private Dictionary<string, byte[]> V1Files() => new() { ["App.exe"] = B("app-v1"), ["App.Upgrade.exe"] = B("upgrade-v1") };

    private Dictionary<string, byte[]> V2Files(string? declaration = UpgradeDeclaration)
    {
        var files = new Dictionary<string, byte[]> { ["App.exe"] = B("app-v2"), ["App.Upgrade.exe"] = B("upgrade-v2") };
        if (declaration is not null) files["instella-upgrade.json"] = B(declaration);
        return files;
    }

    private async Task<(UpdateResult Result, List<UpdaterState> States, List<UpdaterProgress> Progress)> RunWithProgram(
        FakeDownloader server, FakePrograms programs, Version? from = null, Version? to = null, bool repair = false)
    {
        var engine = new UpdaterEngine(Args(from ?? V1, to ?? V2, usePatch: false, repair), server, new FakePlatformServices(),
            _fs, BsDiffEngine.Instance, NoDelays) { Programs = programs };
        var states = new List<UpdaterState>();
        var progress = new List<UpdaterProgress>();
        engine.StateChanged += (_, s) => states.Add(s);
        engine.ProgressChanged += (_, p) => progress.Add(p);
        var result = await engine.RunAsync(CancellationToken.None);
        return (result, states, progress);
    }

    private bool PostUpdateMarkerWritten() =>
        _fs.EnumerateFiles(Path.Combine(_root, ".instella"), "*", recursive: true)
            .Any(f => f.Contains($"{Path.DirectorySeparatorChar}post-update{Path.DirectorySeparatorChar}"));

    [Test]
    public async Task Update_RunsTheUpgradeProgram_OnTheNewFiles_ThenCompletes()
    {
        Install(V1Files());
        var programs = new FakePrograms();
        string? seen = null;
        programs.Behaviour = (start, line) =>
        {
            seen = Text("App.exe");
            line(ProgramStream.Output, "##instella progress 60 Converting settings");
            return 0;
        };

        var (result, states, progress) = await RunWithProgram(new FakeDownloader(Release(V2, V2Files())), programs);

        Assert.That(result.Success, Is.True, result.Error);
        Assert.That(seen, Is.EqualTo("app-v2"), "the program runs after the new files are in place");
        var start = programs.CapturedRuns.Single();
        Assert.That(start.ExePath, Is.EqualTo(Path.Combine(_root, "App.Upgrade.exe")));
        Assert.That(start.Arguments.Take(15), Is.EqualTo(new[]
        {
            "--instella-upgrade", "--contract", "1", "--mode", "update", "--from", "1.0.0", "--to", "2.0.0",
            "--scope", "user", "--install-path", _root, "--app-id", AppId,
        }));
        Assert.That(states.SkipWhile(s => s != UpdaterState.Finalizing),
            Is.EqualTo(new[] { UpdaterState.Finalizing, UpdaterState.UpgradingData, UpdaterState.Completed }));
        Assert.That(progress.Any(p => p.State == UpdaterState.UpgradingData && p.Percentage == 60
                                      && p.StatusText == "Converting settings…"), Is.True);
        Assert.That((await ReadManifest()).Version, Is.EqualTo(V2));
        Assert.That(PostUpdateMarkerWritten(), Is.True);
    }

    [Test]
    public async Task Repair_RunsTheProgram_InRepairMode_WithTheSameVersionTwice()
    {
        Install(V1Files());
        var files = V1Files();
        files["instella-upgrade.json"] = B(UpgradeDeclaration);
        var programs = new FakePrograms();

        var (result, _, _) = await RunWithProgram(new FakeDownloader(Release(V1, files)), programs, V1, V1, repair: true);

        Assert.That(result.Success, Is.True, result.Error);
        Assert.That(programs.CapturedRuns.Single().Arguments.Skip(3).Take(6),
            Is.EqualTo(new[] { "--mode", "repair", "--from", "1.0.0", "--to", "1.0.0" }));
    }

    [Test]
    public async Task AMachineWideInstallation_PassesMachineScope()
    {
        Install(V1Files(), perUser: false);
        var programs = new FakePrograms();
        await RunWithProgram(new FakeDownloader(Release(V2, V2Files())), programs);
        Assert.That(programs.CapturedRuns.Single().Arguments[10], Is.EqualTo("machine"));
    }

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(42)]
    public async Task AFailingProgram_RollsTheUpdateBack_Exit25(int exitCode)
    {
        Install(V1Files());
        var programs = new FakePrograms { ExitCode = exitCode };

        var (result, states, _) = await RunWithProgram(new FakeDownloader(Release(V2, V2Files())), programs);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Failure, Is.EqualTo(UpdateFailure.AppUpgradeFailed));
        Assert.That(result.ExitCode, Is.EqualTo(InstellaExitCode.UpdateAppUpgradeFailed));
        Assert.That(result.Error, Does.EndWith("(rolled back)"));
        Assert.That(states, Does.Contain(UpdaterState.RollingBack));
        Assert.That(Text("App.exe"), Is.EqualTo("app-v1"));
        Assert.That(Text("App.Upgrade.exe"), Is.EqualTo("upgrade-v1"));
        Assert.That(Tree().ContainsKey("instella-upgrade.json"), Is.False, "a file the update added is gone");
        Assert.That((await ReadManifest()).Version, Is.EqualTo(V1));
        Assert.That(PostUpdateMarkerWritten(), Is.False, "the app is not told about an update that did not happen");
        Assert.That(_fs.EnumerateFiles(Path.Combine(_root, ".instella"), "*", recursive: true), Is.Empty);
    }

    [Test]
    public async Task ACommitThatCannotBeConfirmed_AfterTheProgram_RollsBack_Exit22_NotBeforeCommit()
    {
        Install(V1Files());
        var faulty = new FaultInjectingFileSystem(_fs);
        // The program succeeds; the next write (the journal's "committed" record) fails once.
        var programs = new FakePrograms { Behaviour = (_, _) => { faulty.Arm(0); return 0; } };
        var engine = new UpdaterEngine(Args(V1, V2, usePatch: false, repair: false),
            new FakeDownloader(Release(V2, V2Files())), new FakePlatformServices(), faulty, BsDiffEngine.Instance, NoDelays)
        { Programs = programs };

        var result = await engine.RunAsync(CancellationToken.None);

        Assert.That(result.Failure, Is.EqualTo(UpdateFailure.RolledBack), result.Error);
        Assert.That(result.ExitCode, Is.EqualTo(InstellaExitCode.UpdateRolledBack));
        Assert.That(result.Error, Does.Contain("the app's upgrade program had already run"));
        Assert.That(Text("App.exe"), Is.EqualTo("app-v1"));
        Assert.That((await ReadManifest()).Version, Is.EqualTo(V1));
        Assert.That(PostUpdateMarkerWritten(), Is.False);
    }

    [Test]
    public async Task ATimeout_RollsTheUpdateBack_Exit25()
    {
        Install(V1Files());
        var programs = new FakePrograms { Behaviour = (_, _) => throw new TimeoutException("ended") };

        var (result, _, _) = await RunWithProgram(new FakeDownloader(Release(V2, V2Files())), programs);

        Assert.That(result.ExitCode, Is.EqualTo(InstellaExitCode.UpdateAppUpgradeFailed));
        Assert.That(result.Error, Does.Contain("did not finish within 30 minute(s)"));
        Assert.That(Text("App.exe"), Is.EqualTo("app-v1"));
    }

    [Test]
    public async Task ADeclarationThisUpdaterCannotHonour_RollsBack_WithoutRunningAnything()
    {
        Install(V1Files());
        var programs = new FakePrograms();

        var (result, _, _) = await RunWithProgram(
            new FakeDownloader(Release(V2, V2Files("""{"contractVersion":2,"program":"App.Upgrade"}"""))), programs);

        Assert.That(result.ExitCode, Is.EqualTo(InstellaExitCode.UpdateAppUpgradeFailed));
        Assert.That(result.Error, Does.Contain("contract version 2"));
        Assert.That(programs.CapturedRuns, Is.Empty);
        Assert.That(Text("App.exe"), Is.EqualTo("app-v1"));
    }

    [Test]
    public async Task AReleaseWithoutADeclaration_UpdatesAsBefore()
    {
        Install(V1Files());
        var programs = new FakePrograms();

        var (result, states, _) = await RunWithProgram(new FakeDownloader(Release(V2, V2Files(declaration: null))), programs);

        Assert.That(result.Success, Is.True, result.Error);
        Assert.That(programs.CapturedRuns, Is.Empty);
        Assert.That(states, Does.Not.Contain(UpdaterState.UpgradingData));
    }

    [Test]
    public async Task ACrashWhileTheProgramRuns_IsRolledBackByTheNextRun()
    {
        Install(V1Files());
        var programs = new FakePrograms();
        InMemoryFileSystem? atCrash = null;
        programs.Behaviour = (_, _) =>
        {
            // The machine loses power here: keep the disk as it is at this moment.
            atCrash = new InMemoryFileSystem();
            foreach (var (path, bytes) in _fs.Snapshot()) atCrash.AddFile(path, bytes);
            return 0;
        };
        await RunWithProgram(new FakeDownloader(Release(V2, V2Files())), programs);

        var rolledBack = await InstallTransaction.RecoverAsync(atCrash!, _root, CancellationToken.None, retryDelays: NoDelays);

        Assert.That(rolledBack, Is.True, "the held commit is an interrupted transaction");
        var manifest = await new InstallManifestWriter(atCrash!).ReadAsync(_root, CancellationToken.None);
        Assert.That(manifest!.Version, Is.EqualTo(V1));
        Assert.That(Encoding.UTF8.GetString(atCrash!.Snapshot()[Path.GetFullPath(Path.Combine(_root, "App.exe"))]), Is.EqualTo("app-v1"));
    }
}
