using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Installation;
using Instella.Core.Manifest;
using Instella.Core.Platform;
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Runtime.Installation;
using Instella.Installer.Runtime.Migrations;
using Instella.Installer.Testing;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Migrations;

/// <summary>Symbolic links and junctions are never followed; BeforeCommit undo survives a crash.</summary>
[TestFixture]
public class MigrationLinkAndRecoveryTests
{
    private MigrationTestBed _bed = null!;

    [SetUp]
    public void SetUp()
    {
        _bed = new MigrationTestBed();
        _bed.AddFile(KnownFolder.LocalAppData, "ExampleApp/ExampleApp.exe", "old exe");
        _bed.AddFile(KnownFolder.LocalAppData, "ExampleApp/ExampleApp.pdb", "old pdb");
    }

    private string P(string relative) => _bed.PathOf(KnownFolder.LocalAppData, relative);

    private Task<MigrationExecutionResult> Run(Func<TestMigration, Task> body, MigrationTiming timing = MigrationTiming.AfterCommit, InstallContext? context = null) =>
        MigrationExecution.RunAsync(new TestMigration("m", timing) { Body = (t, _) => body(t) }, context ?? _bed.Context(), CancellationToken.None);

    // ---- Links -----------------------------------------------------------------------------

    [Test]
    public async Task AFolderThatIsALink_IsRefused()
    {
        _bed.FileSystem.AddLink(P("Linked"));
        var result = await Run(t => t.DeleteFilesAsync(t.Folder(KnownFolder.LocalAppData, "Linked"), ["x.exe"], default));
        Assert.That(result.Reason, Does.Contain("symbolic link or junction"));
    }

    [Test]
    public async Task AFolderReachedThroughALink_IsRefused()
    {
        _bed.FileSystem.AddLink(P("Vendor"));
        _bed.AddFile(KnownFolder.LocalAppData, "Vendor/App/x.exe");
        var result = await Run(t => t.DeleteFilesAsync(t.Folder(KnownFolder.LocalAppData, "Vendor/App"), ["x.exe"], default));
        Assert.That(result.Reason, Does.Contain("symbolic link or junction"));
        Assert.That(_bed.FileSystem.Exists(P("Vendor/App/x.exe")), Is.True);
    }

    [Test]
    public async Task AFileNameThroughALink_IsRefused_BeforeAnythingIsDeleted()
    {
        _bed.AddFile(KnownFolder.LocalAppData, "ExampleApp/plugins/old.dll");
        _bed.FileSystem.AddLink(P("ExampleApp/plugins"));
        var result = await Run(t => t.DeleteFilesAsync(t.Folder(KnownFolder.LocalAppData, "ExampleApp"), ["ExampleApp.exe", "plugins/old.dll"], default));
        Assert.That(result.Reason, Does.Contain("goes through the link"));
        Assert.That(_bed.FileSystem.Exists(P("ExampleApp/ExampleApp.exe")), Is.True, "checked before the first delete");
        Assert.That(_bed.FileSystem.Exists(P("ExampleApp/plugins/old.dll")), Is.True);
    }

    [Test]
    public async Task ALinkInsideTheFolder_IsNotFollowed_ByTheInstallationCheckOrTheProcessSearch()
    {
        // A junction inside the old copy pointing at another app's folder: its installation and
        // programs are not this migration's business.
        _bed.AddFile(KnownFolder.LocalAppData, "ExampleApp/shared/" + InstellaOwnedPaths.InstalledManifest, "{}");
        _bed.AddFile(KnownFolder.LocalAppData, "ExampleApp/shared/Other.exe");
        _bed.FileSystem.AddLink(P("ExampleApp/shared"));
        _bed.Processes.Start(P("ExampleApp/shared/Other.exe"));
        _bed.Processes.Start(P("ExampleApp/ExampleApp.exe"));

        var result = await Run(async t =>
        {
            var old = t.Folder(KnownFolder.LocalAppData, "ExampleApp");
            await t.StopProcessesInAsync(old, default);
            await t.DeleteFilesAsync(old, ["ExampleApp.exe"], default);
        });

        Assert.That(result.Outcome, Is.EqualTo(MigrationRunOutcome.Completed), result.Reason);
        Assert.That(_bed.Processes.IsRunning(P("ExampleApp/shared/Other.exe")), Is.True, "not closed through the link");
        Assert.That(_bed.Processes.IsRunning(P("ExampleApp/ExampleApp.exe")), Is.False);
        Assert.That(_bed.FileSystem.Exists(P("ExampleApp/ExampleApp.exe")), Is.False);
    }

    // ---- Crash recovery --------------------------------------------------------------------

    /// <summary>A BeforeCommit migration that deleted, repointed and removed, then the installer "crashed".</summary>
    private async Task<string> CrashAfterMigrationAsync()
    {
        _bed.SetRunValue("ExampleApp", $"\"{P("ExampleApp/ExampleApp.exe")}\" --tray");
        var result = await Run(async t =>
        {
            var old = t.Folder(KnownFolder.LocalAppData, "ExampleApp");
            await t.RepointRunValueAsync("ExampleApp", old, default);
            await t.DeleteFilesAsync(old, ["ExampleApp.exe", "ExampleApp.pdb"], default);
            await t.DeleteFolderIfEmptyAsync(old, default);
        }, MigrationTiming.BeforeCommit);
        Assert.That(result.Outcome, Is.EqualTo(MigrationRunOutcome.Completed), result.Reason);
        Assert.That(_bed.FileSystem.Exists(P("ExampleApp/ExampleApp.exe")), Is.False);
        // Nothing else runs: no commit, no rollback, no completion clean-up.
        return Path.Combine(_bed.Root, "undo", "m");
    }

    /// <summary>The next installer run on the same folder: a fresh context, same undo root.</summary>
    private async Task RecoverAsync()
    {
        var context = _bed.Context();
        context.Migrations = new MigrationRuntime
        {
            Folders = _bed.Resolver(), UndoRoot = _bed.Root, UndoDirectory = Path.Combine(_bed.Root, "next-run"),
        };
        await MigrationUndo.RecoverAsync(context, CancellationToken.None);
    }

    private async Task WriteInstalledManifestAsync(Version version, DateTime installedAt) =>
        await new InstallManifestWriter(_bed.FileSystem).WriteAsync(_bed.InstallPath, new InstalledManifest
        {
            AppName = "ExampleApp", AppId = "com.example.app", Version = version, InstallDirectory = _bed.InstallPath,
            ExecutableName = "ExampleApp.exe", InstalledAt = installedAt, Files = [],
        }, CancellationToken.None);

    [Test]
    public async Task EveryChange_IsJournaledOnDisk_BeforeItIsMade()
    {
        var dir = await CrashAfterMigrationAsync();
        var journal = JsonSerializer.Deserialize(
            (await _bed.FileSystem.ReadAllBytesAsync(Path.Combine(dir, "undo.json"), CancellationToken.None)).Value!,
            UndoJournalJsonContext.Default.UndoJournalFile)!;
        Assert.That(journal.MigrationId, Is.EqualTo("m"));
        Assert.That(journal.InstallPath, Is.EqualTo(_bed.InstallPath));
        Assert.That(journal.TargetVersion, Is.EqualTo("2.0.0"));
        Assert.That(journal.Entries.Select(e => e.Kind), Is.EqualTo(new[] { "run-value", "file", "file", "folder" }));
        Assert.That(journal.Entries[0].Value, Is.EqualTo($"\"{P("ExampleApp/ExampleApp.exe")}\" --tray"), "the original, to write back");
    }

    [Test]
    public async Task AfterACrashBeforeTheCommit_TheNextRunUndoesTheMigration()
    {
        var dir = await CrashAfterMigrationAsync();
        await RecoverAsync();

        Assert.That(Encoding.UTF8.GetString(_bed.FileSystem.Snapshot()[P("ExampleApp/ExampleApp.exe")]), Is.EqualTo("old exe"));
        Assert.That(_bed.FileSystem.Exists(P("ExampleApp/ExampleApp.pdb")), Is.True);
        Assert.That(_bed.RunValue("ExampleApp"), Is.EqualTo($"\"{P("ExampleApp/ExampleApp.exe")}\" --tray"));
        Assert.That(_bed.FileSystem.DirectoryExists(dir), Is.False, "copies and journal gone");
        Assert.That(_bed.FileSystem.DirectoryExists(Path.Combine(_bed.Root, "undo")), Is.False, "the run's folder too");
        Assert.That(_bed.Log.Warnings, Has.Some.Contains("an earlier install stopped before it completed"));
    }

    [Test]
    public async Task AfterACrashOfAnUpgrade_ThePreviousVersionsManifest_MeansNotCommitted()
    {
        await WriteInstalledManifestAsync(new Version(1, 0, 0), DateTime.UtcNow.AddDays(-30));
        await CrashAfterMigrationAsync();
        await RecoverAsync();
        Assert.That(_bed.FileSystem.Exists(P("ExampleApp/ExampleApp.exe")), Is.True);
    }

    [Test]
    public async Task AfterACrashPastTheCommit_TheChangesStand_AndOnlyTheCopiesGo()
    {
        var dir = await CrashAfterMigrationAsync();
        await WriteInstalledManifestAsync(new Version(2, 0, 0), DateTime.UtcNow.AddSeconds(1));   // the commit happened
        await RecoverAsync();

        Assert.That(_bed.FileSystem.Exists(P("ExampleApp/ExampleApp.exe")), Is.False);
        Assert.That(_bed.RunValue("ExampleApp"), Does.Contain(_bed.InstallPath));
        Assert.That(_bed.FileSystem.DirectoryExists(dir), Is.False);
    }

    [Test]
    public async Task ARepairOfTheSameVersionFromBefore_IsNotMistakenForTheCommit()
    {
        await WriteInstalledManifestAsync(new Version(2, 0, 0), DateTime.UtcNow.AddDays(-1));   // an older install of 2.0.0
        await CrashAfterMigrationAsync();
        await RecoverAsync();
        Assert.That(_bed.FileSystem.Exists(P("ExampleApp/ExampleApp.exe")), Is.True);
    }

    [Test]
    public async Task AJournalOfAnotherInstallFolder_IsLeftForItsOwnNextRun()
    {
        var dir = await CrashAfterMigrationAsync();
        _bed.InstallPath = P("Programs/Other");
        await RecoverAsync();
        Assert.That(_bed.FileSystem.DirectoryExists(dir), Is.True);
        Assert.That(_bed.FileSystem.Exists(P("ExampleApp/ExampleApp.exe")), Is.False);
    }

    [Test]
    public async Task ARestoreThatFails_IsKept_AndRetriedByTheRunAfter()
    {
        var dir = await CrashAfterMigrationAsync();
        _bed.AddFile(KnownFolder.LocalAppData, "ExampleApp/ExampleApp.exe", "a new file in the way");
        await RecoverAsync();

        Assert.That(_bed.Log.Warnings, Has.Some.Contains("exists again"));
        Assert.That(_bed.FileSystem.Exists(Path.Combine(dir, "undo.json")), Is.True, "what is left to undo stays journaled");
        Assert.That(_bed.FileSystem.Exists(P("ExampleApp/ExampleApp.pdb")), Is.True, "the rest was restored");
        Assert.That(Encoding.UTF8.GetString(_bed.FileSystem.Snapshot()[P("ExampleApp/ExampleApp.exe")]), Is.EqualTo("a new file in the way"), "never overwritten");

        await _bed.FileSystem.DeleteFileAsync(P("ExampleApp/ExampleApp.exe"), CancellationToken.None);
        await RecoverAsync();
        Assert.That(Encoding.UTF8.GetString(_bed.FileSystem.Snapshot()[P("ExampleApp/ExampleApp.exe")]), Is.EqualTo("old exe"));
        Assert.That(_bed.FileSystem.DirectoryExists(dir), Is.False);
    }

    [Test]
    public async Task ARunValueThatCouldNotBeRestored_IsRetriedByTheNextRun()
    {
        var run = await Run(async t =>
        {
            await t.RepointRunValueAsync("ExampleApp", t.Folder(KnownFolder.LocalAppData, "ExampleApp"), default);
            _bed.Platform.DenyRegistryWrites(RegistryHive.CurrentUser, RunCommand.RunKey);
            throw new InvalidOperationException("custom code failed");
        }, MigrationTiming.BeforeCommit, WithRunValue());
        Assert.That(run.Outcome, Is.EqualTo(MigrationRunOutcome.RolledBack));
        Assert.That(_bed.RunValue("ExampleApp"), Does.Contain(_bed.InstallPath), "dangling for now");

        _bed.Platform = new FakePlatformServices(_bed.Platform.Registry);   // access restored
        await RecoverAsync();
        Assert.That(_bed.RunValue("ExampleApp"), Is.EqualTo($"\"{P("ExampleApp/ExampleApp.exe")}\""));
    }

    private InstallContext WithRunValue()
    {
        _bed.SetRunValue("ExampleApp", $"\"{P("ExampleApp/ExampleApp.exe")}\"");
        return _bed.Context();
    }

    [Test]
    public async Task AJournalThatCannotBeRead_IsLeftInPlace()
    {
        var journal = Path.Combine(_bed.Root, "crashed", "m", "undo.json");
        _bed.FileSystem.AddFile(journal, Encoding.UTF8.GetBytes("{ not json"));
        await RecoverAsync();
        Assert.That(_bed.FileSystem.Exists(journal), Is.True);
        Assert.That(_bed.Log.Warnings, Has.Some.Contains("not an undo journal this installer can read"));
    }

    [Test]
    public async Task ASilentInstall_RecoversBeforeItsOwnSteps()
    {
        var harness = InstellaTestHarness.Create()
            .WithInstaller(InstellaInstaller.Create().WithApp("ExampleApp", "com.example.app", new Version(2, 0))
                .WithExecutableName("ExampleApp.exe").WithElevation(ElevationMode.PerUser).Build())
            .WithPayload(new Dictionary<string, string> { ["ExampleApp.exe"] = "new" })
            .Build();
        var fs = (InMemoryFileSystem)harness.FileSystem;
        var local = harness.KnownFolderPath(KnownFolder.LocalAppData);
        var installPath = Path.Combine(local, "Programs", "ExampleApp");
        var original = Path.Combine(local, "ExampleApp", "ExampleApp.exe");
        var backup = Path.Combine(Path.GetTempPath(), "Instella", "migration-undo", "crashed-run", "m", "0-ExampleApp.exe");
        fs.AddFile(backup, Encoding.UTF8.GetBytes("old exe"));
        var journal = new UndoJournalFile(1, "m", installPath, "2.0.0", DateTime.UtcNow.AddMinutes(-5),
            [new UndoEntry(UndoEntry.File, Path: original, Backup: backup)]);
        fs.AddFile(Path.Combine(Path.GetDirectoryName(backup)!, "undo.json"),
            JsonSerializer.SerializeToUtf8Bytes(journal, UndoJournalJsonContext.Default.UndoJournalFile));

        Assert.That(await harness.RunFullWithArgsAsync(["--install", "--silent", "--path", installPath]), Is.EqualTo(0));
        Assert.That(fs.Exists(original), Is.True, "the interrupted install's migration was undone");
        Assert.That(fs.Exists(backup), Is.False);
    }
}
