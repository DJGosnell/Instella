using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Installation;
using Instella.Core.Platform;
using Instella.Installer.Runtime.Core.Processes;
using Instella.Installer.Runtime.Installation;
using Instella.Installer.Runtime.Migrations;
using Instella.Installer.Runtime.Tests.Transactions;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Migrations;

/// <summary>Every migration action: what it does, what it refuses, preview, and BeforeCommit undo.</summary>
[TestFixture]
public class MigrationActionTests
{
    private MigrationTestBed _bed = null!;
    private InstallContext _context = null!;

    [SetUp]
    public void SetUp()
    {
        _bed = new MigrationTestBed();
        _context = null!;
    }

    private string OldExe => _bed.PathOf(KnownFolder.LocalAppData, "ExampleApp/ExampleApp.exe");
    private string OldPdb => _bed.PathOf(KnownFolder.LocalAppData, "ExampleApp/ExampleApp.pdb");
    private string NewExe => Path.Combine(_bed.InstallPath, "ExampleApp.exe");

    private void SeedOldCopy()
    {
        _bed.AddFile(KnownFolder.LocalAppData, "ExampleApp/ExampleApp.exe");
        _bed.AddFile(KnownFolder.LocalAppData, "ExampleApp/ExampleApp.pdb");
    }

    private async Task<MigrationExecutionResult> Run(TestMigration m)
    {
        _context = _bed.Context();
        return await MigrationExecution.RunAsync(m, _context, CancellationToken.None);
    }

    private static TestMigration Body(Func<TestMigration, Task> body, MigrationTiming timing = MigrationTiming.AfterCommit) =>
        new("test", timing) { Body = (m, _) => body(m) };

    // ---- StopProcessesInAsync --------------------------------------------------------------

    [Test]
    public async Task StopProcesses_ClosesProgramsRunningFromTheFolder()
    {
        SeedOldCopy();
        _bed.Processes.Start(OldExe);
        var elsewhere = _bed.PathOf(KnownFolder.LocalAppData, "Other/Other.exe");
        _bed.AddFile(KnownFolder.LocalAppData, "Other/Other.exe");
        _bed.Processes.Start(elsewhere);

        var result = await Run(Body(m => m.StopProcessesInAsync(m.Folder(KnownFolder.LocalAppData, "ExampleApp"), default)));

        Assert.That(result.Outcome, Is.EqualTo(MigrationRunOutcome.Completed), result.Reason);
        Assert.That(_bed.Processes.Closed, Is.EqualTo(new[] { OldExe }));
        Assert.That(_bed.Processes.IsRunning(elsewhere), Is.True, "only processes from the folder");
        Assert.That(result.Run.Actions.Single().Kind, Is.EqualTo("stop-process"));
    }

    [Test]
    public async Task StopProcesses_NothingRunning_IsFine()
    {
        SeedOldCopy();
        var result = await Run(Body(m => m.StopProcessesInAsync(m.Folder(KnownFolder.LocalAppData, "ExampleApp"), default)));
        Assert.That(result.Outcome, Is.EqualTo(MigrationRunOutcome.Completed));
        Assert.That(result.Run.Actions, Is.Empty);
    }

    [Test]
    public async Task StopProcesses_FailsWhenAProgramSurvives()
    {
        SeedOldCopy();
        _bed.Processes.Start(OldExe, closes: false);
        var result = await Run(Body(m => m.StopProcessesInAsync(m.Folder(KnownFolder.LocalAppData, "ExampleApp"), default)));
        Assert.That(result.Outcome, Is.EqualTo(MigrationRunOutcome.Failed));
        Assert.That(result.Reason, Does.Contain("still running"));
    }

    [Test]
    public async Task StopProcesses_SilentWithoutForceClose_ClosesNothing_AndFails()
    {
        SeedOldCopy();
        _bed.Processes.Start(OldExe);
        _bed.ForceClose = false;
        var result = await Run(Body(m => m.StopProcessesInAsync(m.Folder(KnownFolder.LocalAppData, "ExampleApp"), default)));
        Assert.That(result.Outcome, Is.EqualTo(MigrationRunOutcome.Failed));
        Assert.That(result.Reason, Does.Contain("--force-close"));
        Assert.That(_bed.Processes.IsRunning(OldExe), Is.True);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task StopProcesses_Interactive_AsksFirst(bool closed)
    {
        SeedOldCopy();
        _bed.Processes.Start(OldExe);
        _bed.ForceClose = false;
        var asked = 0;
        _bed.Prompt = (_, programs) => { asked++; Assert.That(programs.Single(), Does.Contain("ExampleApp.exe")); return closed ? AppRunningChoice.CloseAutomatically : AppRunningChoice.Cancel; };
        var result = await Run(Body(m => m.StopProcessesInAsync(m.Folder(KnownFolder.LocalAppData, "ExampleApp"), default)));
        Assert.That(asked, Is.EqualTo(1));
        Assert.That(_bed.Processes.IsRunning(OldExe), Is.EqualTo(!closed));
        Assert.That(result.Outcome, Is.EqualTo(closed ? MigrationRunOutcome.Completed : MigrationRunOutcome.Failed));
    }

    [Test]
    public async Task StopProcesses_NeverClosesExplorerOrServices()
    {
        SeedOldCopy();
        _bed.Processes.Start(OldExe, canClose: false);
        var result = await Run(Body(m => m.StopProcessesInAsync(m.Folder(KnownFolder.LocalAppData, "ExampleApp"), default)));
        Assert.That(result.Outcome, Is.EqualTo(MigrationRunOutcome.Completed));
        Assert.That(_bed.Processes.IsRunning(OldExe), Is.True);
    }

    // ---- Run values ------------------------------------------------------------------------

    [Test]
    public async Task RepointRunValue_PointsAtTheNewExecutable_KeepingTheArguments()
    {
        _bed.SetRunValue("ExampleApp", $"\"{OldExe}\" --tray");
        bool changed = false;
        var result = await Run(Body(async m => changed = await m.RepointRunValueAsync("ExampleApp", m.Folder(KnownFolder.LocalAppData, "ExampleApp"), default)));
        Assert.That(result.Outcome, Is.EqualTo(MigrationRunOutcome.Completed), result.Reason);
        Assert.That(changed, Is.True);
        Assert.That(_bed.RunValue("ExampleApp"), Is.EqualTo($"\"{NewExe}\" --tray"));
    }

    [Test]
    public async Task RepointRunValue_LeavesAValuePointingElsewhere()
    {
        const string other = "\"C:\\Elsewhere\\ExampleApp.exe\"";
        _bed.SetRunValue("ExampleApp", other);
        bool changed = true;
        await Run(Body(async m => changed = await m.RepointRunValueAsync("ExampleApp", m.Folder(KnownFolder.LocalAppData, "ExampleApp"), default)));
        Assert.That(changed, Is.False);
        Assert.That(_bed.RunValue("ExampleApp"), Is.EqualTo(other));
    }

    [Test]
    public async Task RepointRunValue_MissingValue_ReturnsFalse()
    {
        bool changed = true;
        var result = await Run(Body(async m => changed = await m.RepointRunValueAsync("ExampleApp", m.Folder(KnownFolder.LocalAppData, "ExampleApp"), default)));
        Assert.That(result.Outcome, Is.EqualTo(MigrationRunOutcome.Completed));
        Assert.That(changed, Is.False);
        Assert.That(_bed.RunValue("ExampleApp"), Is.Null, "nothing was created");
    }

    [Test]
    public async Task RepointRunValue_InAMachineInstall_UsesHklm()
    {
        _bed.Scope = InstallationScope.SystemWide;
        _bed.InstallPath = Path.Combine(_bed.Folders[KnownFolder.ProgramFiles], "ExampleApp");
        var oldMachineExe = _bed.PathOf(KnownFolder.ProgramFiles, "Vendor/ExampleApp.exe");
        _bed.SetRunValue("ExampleApp", $"\"{oldMachineExe}\"");
        await Run(Body(m => m.RepointRunValueAsync("ExampleApp", m.Folder(KnownFolder.ProgramFiles, "Vendor"), default)));
        Assert.That(_bed.Platform.Registry.Get(RegistryHive.LocalMachine, RunCommand.RunKey, "ExampleApp"),
            Is.EqualTo($"\"{Path.Combine(_bed.InstallPath, "ExampleApp.exe")}\""));
    }

    [Test]
    public async Task DeleteRunValue_OnlyWhenItPointsIntoTheFolder()
    {
        _bed.SetRunValue("ExampleApp", $"\"{OldExe}\"");
        _bed.SetRunValue("Other", "\"C:\\Elsewhere\\Other.exe\"");
        bool a = false, b = true, c = true;
        await Run(Body(async m =>
        {
            var folder = m.Folder(KnownFolder.LocalAppData, "ExampleApp");
            a = await m.DeleteRunValueAsync("ExampleApp", folder, default);
            b = await m.DeleteRunValueAsync("Other", folder, default);
            c = await m.DeleteRunValueAsync("Missing", folder, default);
        }));
        Assert.That((a, b, c), Is.EqualTo((true, false, false)));
        Assert.That(_bed.RunValue("ExampleApp"), Is.Null);
        Assert.That(_bed.RunValue("Other"), Is.Not.Null);
    }

    [Test]
    public async Task AdoptRunValue_RecordsTheValueForUninstall()
    {
        _bed.SetRunValue("ExampleApp", $"\"{NewExe}\"");
        bool adopted = false, missing = true;
        await Run(Body(async m =>
        {
            adopted = await m.AdoptRunValueAsync("ExampleApp", default);
            missing = await m.AdoptRunValueAsync("Missing", default);
            await m.AdoptRunValueAsync("ExampleApp", default);
        }));
        Assert.That((adopted, missing), Is.EqualTo((true, false)));
        Assert.That(_context.Migrations.Adopted, Is.EqualTo(new[] { new ManifestAdoptedItem("run-value", "ExampleApp", true, "test") }),
            "adopted once");
        Assert.That(_bed.RunValue("ExampleApp"), Is.EqualTo($"\"{NewExe}\""), "adoption writes nothing");
    }

    // ---- DeleteFilesAsync ------------------------------------------------------------------

    [Test]
    public async Task DeleteFiles_DeletesOnlyTheNamedFiles_AndSkipsMissingOnes()
    {
        SeedOldCopy();
        _bed.AddFile(KnownFolder.LocalAppData, "ExampleApp/settings.json");
        var count = 0;
        var result = await Run(Body(async m => count = await m.DeleteFilesAsync(
            m.Folder(KnownFolder.LocalAppData, "ExampleApp"), ["ExampleApp.exe", "ExampleApp.pdb", "Missing.dll"], default)));
        Assert.That(result.Outcome, Is.EqualTo(MigrationRunOutcome.Completed), result.Reason);
        Assert.That(count, Is.EqualTo(2));
        Assert.That(_bed.FileSystem.Exists(OldExe), Is.False);
        Assert.That(_bed.FileSystem.Exists(OldPdb), Is.False);
        Assert.That(_bed.FileSystem.Exists(_bed.PathOf(KnownFolder.LocalAppData, "ExampleApp/settings.json")), Is.True);
    }

    [Test]
    public async Task DeleteFiles_AcceptsRelativePathsInsideTheFolder()
    {
        _bed.AddFile(KnownFolder.LocalAppData, "ExampleApp/lib/old.dll");
        await Run(Body(m => m.DeleteFilesAsync(m.Folder(KnownFolder.LocalAppData, "ExampleApp"), ["lib/old.dll"], default)));
        Assert.That(_bed.FileSystem.Exists(_bed.PathOf(KnownFolder.LocalAppData, "ExampleApp/lib/old.dll")), Is.False);
    }

    [TestCase("*.exe")]
    [TestCase("Example?pp.exe")]
    [TestCase("../Other/Other.exe")]
    [TestCase(@"C:\Windows\notepad.exe")]
    [TestCase("")]
    public async Task DeleteFiles_RefusesUnsafeNames_BeforeDeletingAnything(string bad)
    {
        SeedOldCopy();
        var result = await Run(Body(m => m.DeleteFilesAsync(m.Folder(KnownFolder.LocalAppData, "ExampleApp"), ["ExampleApp.exe", bad], default)));
        Assert.That(result.Outcome, Is.EqualTo(MigrationRunOutcome.Failed));
        Assert.That(result.Reason, Does.StartWith("DeleteFiles:"));
        Assert.That(_bed.FileSystem.Exists(OldExe), Is.True, "nothing deleted");
    }

    [Test]
    public async Task DeleteFiles_AFileThatCannotBeDeleted_FailsTheMigration()
    {
        SeedOldCopy();
        var faulty = new FaultInjectingFileSystem(_bed.FileSystem);
        faulty.Arm(1);
        _bed.FileSystemOverride = faulty;
        var result = await Run(Body(m => m.DeleteFilesAsync(m.Folder(KnownFolder.LocalAppData, "ExampleApp"), ["ExampleApp.exe", "ExampleApp.pdb"], default)));
        Assert.That(result.Outcome, Is.EqualTo(MigrationRunOutcome.Failed));
        Assert.That(result.Reason, Does.Contain("injected fault"));
        Assert.That(_bed.FileSystem.Exists(OldExe), Is.False);
        Assert.That(_bed.FileSystem.Exists(OldPdb), Is.True);
    }

    // ---- DeleteFolderIfEmptyAsync ----------------------------------------------------------

    [Test]
    public async Task DeleteFolderIfEmpty_OnlyWhenEmpty()
    {
        SeedOldCopy();
        bool first = true, second = false, missing = true;
        await Run(Body(async m =>
        {
            var folder = m.Folder(KnownFolder.LocalAppData, "ExampleApp");
            first = await m.DeleteFolderIfEmptyAsync(folder, default);
            await m.DeleteFilesAsync(folder, ["ExampleApp.exe", "ExampleApp.pdb"], default);
            second = await m.DeleteFolderIfEmptyAsync(folder, default);
            missing = await m.DeleteFolderIfEmptyAsync(m.Folder(KnownFolder.LocalAppData, "Nothing"), default);
        }));
        Assert.That((first, second, missing), Is.EqualTo((false, true, false)));
        Assert.That(_bed.FileSystem.DirectoryExists(_bed.PathOf(KnownFolder.LocalAppData, "ExampleApp")), Is.False);
    }

    [Test]
    public async Task DeleteFolderIfEmpty_KeepsAFolderWithFilesInSubfolders()
    {
        _bed.AddFile(KnownFolder.LocalAppData, "ExampleApp/logs/today.log");
        bool deleted = true;
        await Run(Body(async m => deleted = await m.DeleteFolderIfEmptyAsync(m.Folder(KnownFolder.LocalAppData, "ExampleApp"), default)));
        Assert.That(deleted, Is.False);
        Assert.That(_bed.FileSystem.Exists(_bed.PathOf(KnownFolder.LocalAppData, "ExampleApp/logs/today.log")), Is.True);
    }

    // ---- RunProgramAsync -------------------------------------------------------------------

    [Test]
    public async Task RunProgram_RunsWithTheArguments_AndReturnsTheExitCode()
    {
        _bed.AddFile(KnownFolder.ProgramFiles, "OldVendor/uninstall.exe");
        var exe = _bed.PathOf(KnownFolder.ProgramFiles, "OldVendor/uninstall.exe");
        _bed.Programs.ExitCode = 3010;
        var code = 0;
        var result = await Run(Body(async m => code = await m.RunProgramAsync(exe, ["/S", "/keepdata"], [0, 3010], default)));
        Assert.That(result.Outcome, Is.EqualTo(MigrationRunOutcome.Completed), result.Reason);
        Assert.That(code, Is.EqualTo(3010));
        Assert.That(_bed.Programs.Runs.Single().Exe, Is.EqualTo(exe));
        Assert.That(_bed.Programs.Runs.Single().Args, Is.EqualTo(new[] { "/S", "/keepdata" }));
    }

    [Test]
    public async Task RunProgram_AnExitCodeOutsideTheList_Fails()
    {
        _bed.AddFile(KnownFolder.ProgramFiles, "OldVendor/uninstall.exe");
        _bed.Programs.ExitCode = 1;
        var result = await Run(Body(m => m.RunProgramAsync(_bed.PathOf(KnownFolder.ProgramFiles, "OldVendor/uninstall.exe"), [], [], default)));
        Assert.That(result.Outcome, Is.EqualTo(MigrationRunOutcome.Failed));
        Assert.That(result.Reason, Does.Contain("exited with 1; success is 0"));
    }

    [TestCase("uninstall.exe")]
    [TestCase(@"OldVendor\uninstall.exe")]
    public async Task RunProgram_RefusesRelativePaths(string exe)
    {
        var result = await Run(Body(m => m.RunProgramAsync(exe, [], [0], default)));
        Assert.That(result.Reason, Does.Contain("is not a full path"));
        Assert.That(_bed.Programs.Runs, Is.Empty);
    }

    [Test]
    public async Task RunProgram_RefusesAMissingProgram()
    {
        var result = await Run(Body(m => m.RunProgramAsync(_bed.PathOf(KnownFolder.ProgramFiles, "Gone/uninstall.exe"), [], [0], default)));
        Assert.That(result.Reason, Does.Contain("does not exist"));
        Assert.That(_bed.Programs.Runs, Is.Empty);
    }

    // ---- Refusals --------------------------------------------------------------------------

    private async Task<string?> RefusalFor(KnownFolder root, string relative)
    {
        var result = await Run(Body(m => m.DeleteFilesAsync(m.Folder(root, relative), ["x.exe"], default)));
        Assert.That(result.Outcome, Is.EqualTo(MigrationRunOutcome.Failed));
        return result.Reason;
    }

    [Test]
    public async Task Actions_RefuseTheInstallFolder()
    {
        Assert.That(await RefusalFor(KnownFolder.InstallFolder, "sub"), Does.Contain("for conditions only"));
    }

    [Test]
    public async Task Actions_RefuseAFolderInsideTheInstallFolder()
    {
        Assert.That(await RefusalFor(KnownFolder.LocalAppData, "Programs/ExampleApp/sub"), Does.Contain("folder being installed, or inside it"));
    }

    [Test]
    public async Task Actions_RefuseTheInstallFolderThroughAnotherRoot()
    {
        Assert.That(await RefusalFor(KnownFolder.LocalAppData, "Programs/ExampleApp"), Does.Contain("folder being installed"));
    }

    [Test]
    public async Task Actions_RefuseAnAncestorOfTheInstallFolder()
    {
        _bed.InstallPath = _bed.PathOf(KnownFolder.LocalAppData, "Vendor/Apps/ExampleApp");
        Assert.That(await RefusalFor(KnownFolder.LocalAppData, "Vendor"), Does.Contain("contains the folder being installed"));
    }

    [TestCase(KnownFolder.LocalAppData, "Programs")]
    [TestCase(KnownFolder.LocalAppData, "Microsoft")]
    [TestCase(KnownFolder.LocalAppData, "Packages")]
    [TestCase(KnownFolder.LocalAppData, "Temp")]
    [TestCase(KnownFolder.RoamingAppData, "Microsoft")]
    public async Task Actions_RefuseSharedSubfolders(KnownFolder root, string relative)
    {
        Assert.That(await RefusalFor(root, relative), Does.Contain("protected folder"));
    }

    [Test]
    public async Task Actions_RefuseAnAncestorOfAnotherKnownFolder()
    {
        // The fake Start menu lives under Roaming/Microsoft/Windows/Start Menu/Programs.
        Assert.That(await RefusalFor(KnownFolder.RoamingAppData, "Microsoft/Windows"), Does.Contain("protected folder"));
    }

    [Test]
    public async Task Actions_RefuseAFolderHoldingAnInstellaInstallation()
    {
        _bed.AddFile(KnownFolder.LocalAppData, "ExampleApp/" + InstellaOwnedPaths.InstalledManifest, "{}");
        _bed.AddFile(KnownFolder.LocalAppData, "ExampleApp/x.exe");
        Assert.That(await RefusalFor(KnownFolder.LocalAppData, "ExampleApp"), Does.Contain("holds an Instella installation"));
        Assert.That(_bed.FileSystem.Exists(_bed.PathOf(KnownFolder.LocalAppData, "ExampleApp/x.exe")), Is.True);
    }

    [Test]
    public async Task Actions_RefusePerUserFoldersInAMachineInstall()
    {
        _bed.AddFile(KnownFolder.LocalAppData, "ExampleApp/x.exe");
        _bed.Scope = InstallationScope.SystemWide;
        Assert.That(await RefusalFor(KnownFolder.LocalAppData, "ExampleApp"), Does.Contain("per-user folder"));
    }

    [Test]
    public async Task EveryFolderAction_RefusesTheSameWay()
    {
        var bad = (Func<TestMigration, MigrationFolder>)(m => m.Folder(KnownFolder.InstallFolder, "sub"));
        foreach (var action in new Func<TestMigration, Task>[]
                 {
                     m => m.StopProcessesInAsync(bad(m), default),
                     m => m.RepointRunValueAsync("ExampleApp", bad(m), default),
                     m => m.DeleteRunValueAsync("ExampleApp", bad(m), default),
                     m => m.DeleteFilesAsync(bad(m), ["x"], default),
                     m => m.DeleteFolderIfEmptyAsync(bad(m), default),
                 })
        {
            var result = await Run(Body(action));
            Assert.That(result.Reason, Does.Contain("refused").And.Contain("for conditions only"));
        }
    }

    // ---- Preview ---------------------------------------------------------------------------

    [Test]
    public async Task Preview_ReportsEveryAction_AndChangesNothing()
    {
        SeedOldCopy();
        _bed.Processes.Start(OldExe);
        _bed.SetRunValue("ExampleApp", $"\"{OldExe}\"");
        _bed.AddFile(KnownFolder.ProgramFiles, "OldVendor/uninstall.exe");
        _bed.Preview = true;
        var before = _bed.FileSystem.Snapshot().Keys.ToList();

        var result = await Run(Body(async m =>
        {
            var folder = m.Folder(KnownFolder.LocalAppData, "ExampleApp");
            await m.StopProcessesInAsync(folder, default);
            Assert.That(await m.RepointRunValueAsync("ExampleApp", folder, default), Is.True);
            Assert.That(await m.AdoptRunValueAsync("ExampleApp", default), Is.True);
            Assert.That(await m.DeleteFilesAsync(folder, ["ExampleApp.exe", "ExampleApp.pdb"], default), Is.EqualTo(2));
            Assert.That(await m.DeleteFolderIfEmptyAsync(folder, default), Is.True, "would be empty after the deletes");
            Assert.That(await m.RunProgramAsync(_bed.PathOf(KnownFolder.ProgramFiles, "OldVendor/uninstall.exe"), [], [0], default), Is.Zero);
            Assert.That(await m.DeleteRunValueAsync("ExampleApp", folder, default), Is.True);
        }));

        Assert.That(result.Outcome, Is.EqualTo(MigrationRunOutcome.Completed), result.Reason);
        Assert.That(result.Run.Actions.Select(a => a.Kind), Is.EqualTo(new[]
        {
            "stop-process", "run-value-repoint", "run-value-adopt", "delete-file", "delete-file", "delete-folder", "run-program", "run-value-delete",
        }));
        Assert.That(result.Run.Actions.All(a => a.Preview), Is.True);
        Assert.That(_bed.FileSystem.Snapshot().Keys, Is.EquivalentTo(before));
        Assert.That(_bed.Processes.IsRunning(OldExe), Is.True);
        Assert.That(_bed.RunValue("ExampleApp"), Is.EqualTo($"\"{OldExe}\""));
        Assert.That(_bed.Programs.Runs, Is.Empty);
        Assert.That(_context.Migrations.Adopted, Is.Empty);
        Assert.That(_context.Migrations.Completed, Is.Empty, "a preview never records completion");
        Assert.That(_bed.Log.Lines, Has.Some.Contains("preview: would delete-file"));
    }

    // ---- BeforeCommit undo -----------------------------------------------------------------

    [Test]
    public async Task BeforeCommit_Failure_UndoesEveryAction_AndCallsRollback()
    {
        SeedOldCopy();
        _bed.SetRunValue("ExampleApp", $"\"{OldExe}\" --tray");
        _bed.SetRunValue("Helper", $"\"{_bed.PathOf(KnownFolder.LocalAppData, "ExampleApp/Helper.exe")}\"");
        var m = Body(async t =>
        {
            var folder = t.Folder(KnownFolder.LocalAppData, "ExampleApp");
            await t.RepointRunValueAsync("ExampleApp", folder, default);
            await t.DeleteRunValueAsync("Helper", folder, default);
            await t.AdoptRunValueAsync("ExampleApp", default);
            await t.DeleteFilesAsync(folder, ["ExampleApp.exe", "ExampleApp.pdb"], default);
            await t.DeleteFolderIfEmptyAsync(folder, default);
            throw new InvalidOperationException("custom code failed");
        }, MigrationTiming.BeforeCommit);

        var result = await Run(m);

        Assert.That(result.Outcome, Is.EqualTo(MigrationRunOutcome.RolledBack));
        Assert.That(result.Reason, Is.EqualTo("custom code failed"));
        Assert.That(m.Rollbacks, Is.EqualTo(1));
        Assert.That(_bed.FileSystem.Exists(OldExe), Is.True);
        Assert.That(_bed.FileSystem.Exists(OldPdb), Is.True);
        Assert.That(_bed.RunValue("ExampleApp"), Is.EqualTo($"\"{OldExe}\" --tray"));
        Assert.That(_bed.RunValue("Helper"), Is.Not.Null);
        Assert.That(_context.Migrations.Adopted, Is.Empty);
        Assert.That(_context.Migrations.Completed, Is.Empty);
        Assert.That(_bed.FileSystem.DirectoryExists(Path.Combine(_bed.Root, "undo", "test")), Is.False, "the undo copies are gone");
    }

    [Test]
    public async Task BeforeCommit_Success_KeepsUndoCopies_UntilTheInstallCompletes()
    {
        SeedOldCopy();
        var m = Body(t => t.DeleteFilesAsync(t.Folder(KnownFolder.LocalAppData, "ExampleApp"), ["ExampleApp.exe"], default), MigrationTiming.BeforeCommit);
        var result = await Run(m);
        Assert.That(result.Outcome, Is.EqualTo(MigrationRunOutcome.Completed));
        Assert.That(_bed.FileSystem.Exists(OldExe), Is.False);
        Assert.That(_bed.FileSystem.DirectoryExists(Path.Combine(_bed.Root, "undo")), Is.True);
        Assert.That(_context.CompletionActions, Has.Count.EqualTo(1));

        await _context.CompletionActions.Single()();
        Assert.That(_bed.FileSystem.DirectoryExists(Path.Combine(_bed.Root, "undo")), Is.False);
    }

    [Test]
    public async Task BeforeCommit_ALaterFailure_RollsTheMigrationBack()
    {
        SeedOldCopy();
        var m = Body(t => t.DeleteFilesAsync(t.Folder(KnownFolder.LocalAppData, "ExampleApp"), ["ExampleApp.exe"], default), MigrationTiming.BeforeCommit);
        var step = new MigrationStep(m);
        _context = _bed.Context();
        var executed = await new StepExecutor([step, new FailingStep()]).ExecuteAsync(_context, null, CancellationToken.None);
        Assert.That(executed.Success, Is.False);
        Assert.That(_bed.FileSystem.Exists(OldExe), Is.True, "restored");
        Assert.That(m.Rollbacks, Is.EqualTo(1));
        Assert.That(_context.Migrations.Records.Single().Outcome, Is.EqualTo(MigrationRunOutcome.RolledBack));
        Assert.That(_context.Migrations.Completed, Is.Empty);
    }

    [Test]
    public async Task AfterCommit_Failure_UndoesNothing()
    {
        SeedOldCopy();
        var m = Body(async t =>
        {
            await t.DeleteFilesAsync(t.Folder(KnownFolder.LocalAppData, "ExampleApp"), ["ExampleApp.exe"], default);
            throw new InvalidOperationException("boom");
        });
        var result = await Run(m);
        Assert.That(result.Outcome, Is.EqualTo(MigrationRunOutcome.Failed));
        Assert.That(m.Rollbacks, Is.Zero, "RollbackAsync is for BeforeCommit only");
        Assert.That(_bed.FileSystem.Exists(OldExe), Is.False, "best effort: what was done stays done");
    }

    // ---- Execution -------------------------------------------------------------------------

    [Test]
    public async Task Execution_RecordsCompletion_OnlyForRunOnceSuccesses()
    {
        var once = await Run(new TestMigration("once"));
        Assert.That(_context.Migrations.Completed, Is.EqualTo(new[] { "once" }));
        Assert.That(once.Outcome, Is.EqualTo(MigrationRunOutcome.Completed));

        await Run(new TestMigration("every-time") { RunOnceValue = false });
        Assert.That(_context.Migrations.Completed, Is.Empty);

        await Run(new TestMigration("skipped") { WhenFactory = t => t.IsUpgrade() });
        Assert.That(_context.Migrations.Completed, Is.Empty);
        Assert.That(_context.Migrations.Records.Single().Outcome, Is.EqualTo(MigrationRunOutcome.Skipped));

        await Run(new TestMigration("failed") { Body = (_, _) => throw new IOException("nope") });
        Assert.That(_context.Migrations.Completed, Is.Empty);
    }

    [Test]
    public async Task Execution_SkipsAMigrationTheInstallationAlreadyCompleted()
    {
        _bed.Mode = InstallerMode.Repair;
        _bed.PreviousVersion = new Version(2, 0, 0);
        _bed.PreviouslyCompleted = ["done"];
        var m = new TestMigration("done");
        var result = await Run(m);
        Assert.That(result.Outcome, Is.EqualTo(MigrationRunOutcome.AlreadyCompleted));
        Assert.That(m.Executions, Is.Zero);

        var notOnce = new TestMigration("done") { RunOnceValue = false };
        await Run(notOnce);
        Assert.That(notOnce.Executions, Is.EqualTo(1), "RunOnce = false ignores the record");
    }

    [Test]
    public async Task Execution_LogsWithTheMigrationPrefix_AndASummary()
    {
        SeedOldCopy();
        await Run(new TestMigration("replace-old-copy")
        {
            DisplayNameValue = "Removing the old copy",
            Body = (t, _) => t.DeleteFilesAsync(t.Folder(KnownFolder.LocalAppData, "ExampleApp"), ["ExampleApp.exe"], default),
        });
        var lines = _bed.Log.Lines.Where(l => l.Contains("migration[")).ToList();
        Assert.That(lines, Has.All.Contains("migration[replace-old-copy]: "));
        Assert.That(lines, Has.Some.Contains("start (Removing the old copy)"));
        Assert.That(lines, Has.Some.Contains("completed: delete-file"));
    }

    [Test]
    public async Task Execution_LogsTheSkipReason()
    {
        await Run(new TestMigration { WhenFactory = t => t.FileExists(KnownFolder.LocalAppData, "ExampleApp/ExampleApp.exe") });
        Assert.That(_bed.Log.Lines, Has.Some.EqualTo("INFO migration[test-migration]: skipped: FileExists(LocalAppData/ExampleApp/ExampleApp.exe) is false"));
    }

    [Test]
    public async Task Execution_ContextAndLogAreAvailableInTheBody_NotAfter()
    {
        MigrationContext? seen = null;
        var m = new TestMigration { Body = (t, _) => { seen = t.Ctx; return Task.CompletedTask; } };
        await Run(m);
        Assert.That(seen!.Mode, Is.EqualTo(InstallerMode.FirstInstall));
        Assert.That(seen.GetFolderPath(KnownFolder.LocalAppData), Is.EqualTo(_bed.Folders[KnownFolder.LocalAppData]));
        Assert.Throws<InvalidOperationException>(() => _ = m.Ctx);
    }

    private sealed class FailingStep : IInstallStepExecution
    {
        public string Name => "fails";
        public InstallStage Stage => InstallStage.Finalize;
        public int Weight => 1;
        public Task<StepResult> ExecuteAsync(InstallContext context, IStepProgress progress, CancellationToken cancellationToken) =>
            Task.FromResult(StepResult.Fail("later step failed"));
    }
}
