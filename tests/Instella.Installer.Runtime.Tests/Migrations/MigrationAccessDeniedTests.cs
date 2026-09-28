using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.FileSystem;
using Instella.Core.Installation;
using Instella.Core.Platform;
using Instella.Installer.Runtime.Installation;
using Instella.Installer.Runtime.Migrations;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Migrations;

/// <summary>
/// Reads and writes the installer is not allowed to do: every one fails the migration with the
/// reason (or, for a condition, skips it), never crashes, and BeforeCommit still undoes what it can.
/// </summary>
[TestFixture]
public class MigrationAccessDeniedTests
{
    private MigrationTestBed _bed = null!;
    private InstallContext _context = null!;

    [SetUp]
    public void SetUp()
    {
        _bed = new MigrationTestBed();
        _bed.AddFile(KnownFolder.LocalAppData, "ExampleApp/ExampleApp.exe");
        _bed.AddFile(KnownFolder.LocalAppData, "ExampleApp/ExampleApp.pdb");
    }

    private string OldFolder => _bed.PathOf(KnownFolder.LocalAppData, "ExampleApp");
    private string OldExe => _bed.PathOf(KnownFolder.LocalAppData, "ExampleApp/ExampleApp.exe");
    private string OldPdb => _bed.PathOf(KnownFolder.LocalAppData, "ExampleApp/ExampleApp.pdb");

    private async Task<MigrationExecutionResult> Run(Func<TestMigration, Task> body, MigrationTiming timing = MigrationTiming.AfterCommit,
        Func<TestMigration, Condition>? when = null)
    {
        _context = _bed.Context();
        var m = new TestMigration("denied", timing) { Body = (t, _) => body(t), WhenFactory = when ?? (_ => Condition.Always) };
        return await MigrationExecution.RunAsync(m, _context, CancellationToken.None);
    }

    private static MigrationFolder Old(TestMigration t) => t.Folder(KnownFolder.LocalAppData, "ExampleApp");

    // ---- Files -----------------------------------------------------------------------------

    [Test]
    public async Task DeletingFromAWriteProtectedFolder_FailsWithTheReason_AndDeletesNothing()
    {
        _bed.FileSystem.DenyWrites(OldFolder);
        var result = await Run(t => t.DeleteFilesAsync(Old(t), ["ExampleApp.exe", "ExampleApp.pdb"], default));
        Assert.That(result.Outcome, Is.EqualTo(MigrationRunOutcome.Failed));
        Assert.That(result.Reason, Does.Contain("could not delete").And.Contain("denied"));
        Assert.That(_bed.FileSystem.Exists(OldExe) && _bed.FileSystem.Exists(OldPdb), Is.True);
        Assert.That(_context.Migrations.Completed, Is.Empty, "not recorded: it is tried again next time");
    }

    [Test]
    public async Task OneWriteProtectedFile_FailsAfterTheFilesBeforeIt()
    {
        _bed.FileSystem.DenyWrites(OldPdb);
        var result = await Run(t => t.DeleteFilesAsync(Old(t), ["ExampleApp.exe", "ExampleApp.pdb"], default));
        Assert.That(result.Outcome, Is.EqualTo(MigrationRunOutcome.Failed));
        Assert.That(result.Reason, Does.Contain("ExampleApp.pdb"));
        Assert.That(_bed.FileSystem.Exists(OldExe), Is.False, "AfterCommit is best effort: what was done stays done");
    }

    [Test]
    public async Task BeforeCommit_AWriteProtectedFile_RollsBackTheFilesAlreadyDeleted()
    {
        _bed.FileSystem.DenyWrites(OldPdb);
        var result = await Run(t => t.DeleteFilesAsync(Old(t), ["ExampleApp.exe", "ExampleApp.pdb"], default), MigrationTiming.BeforeCommit);
        Assert.That(result.Outcome, Is.EqualTo(MigrationRunOutcome.RolledBack));
        Assert.That(result.Reason, Does.Contain("denied"));
        Assert.That(_bed.FileSystem.Exists(OldExe), Is.True, "restored from the undo copy");
        Assert.That(_bed.FileSystem.Exists(OldPdb), Is.True);
    }

    [Test]
    public async Task BeforeCommit_AnUndoFolderThatCannotBeWritten_FailsBeforeDeletingAnything()
    {
        _bed.FileSystem.DenyWrites(Path.Combine(_bed.Root, "undo"));
        var result = await Run(t => t.DeleteFilesAsync(Old(t), ["ExampleApp.exe"], default), MigrationTiming.BeforeCommit);
        Assert.That(result.Outcome, Is.EqualTo(MigrationRunOutcome.RolledBack));
        Assert.That(_bed.FileSystem.Exists(OldExe), Is.True, "no undo copy, no delete");
    }

    [Test]
    public async Task BeforeCommit_AnUndoThatIsDenied_IsAWarning_NotACrash()
    {
        var result = await Run(async t =>
        {
            await t.DeleteFilesAsync(Old(t), ["ExampleApp.exe"], default);
            _bed.FileSystem.DenyWrites(OldFolder);   // the folder became read-only in the meantime
            throw new InvalidOperationException("custom code failed");
        }, MigrationTiming.BeforeCommit);
        Assert.That(result.Outcome, Is.EqualTo(MigrationRunOutcome.RolledBack));
        Assert.That(_bed.Log.Warnings, Has.Some.Contains("rollback: could not restore").And.Contains("denied"));

        // The undo copy is the only copy left: it must survive the rollback and the clean-up.
        var kept = _bed.FileSystem.EnumerateFiles(Path.Combine(_bed.Root, "undo", "denied")).ToList();
        Assert.That(kept.Select(Path.GetFileName), Is.EqualTo(new[] { "0-ExampleApp.exe" }));
        Assert.That(_bed.Log.Warnings, Has.Some.Contains("are kept in").And.Contains(Path.Combine(_bed.Root, "undo", "denied")));
        Assert.That(_context.Migrations.UndoCopiesKept, Is.True);
        foreach (var completion in _context.CompletionActions) await completion();
        Assert.That(_bed.FileSystem.Exists(kept[0]), Is.True, "the completion clean-up leaves kept copies alone");
    }

    [Test]
    public async Task BeforeCommit_AFullUndo_StillDeletesTheCopies()
    {
        var result = await Run(async t =>
        {
            await t.DeleteFilesAsync(Old(t), ["ExampleApp.exe"], default);
            throw new InvalidOperationException("custom code failed");
        }, MigrationTiming.BeforeCommit);
        Assert.That(result.Outcome, Is.EqualTo(MigrationRunOutcome.RolledBack));
        Assert.That(_bed.FileSystem.Exists(OldExe), Is.True);
        Assert.That(_bed.FileSystem.DirectoryExists(Path.Combine(_bed.Root, "undo", "denied")), Is.False);
        Assert.That(_context.Migrations.UndoCopiesKept, Is.False);
    }

    [Test]
    public async Task AFolderWhoseContentsCannotBeListed_IsNotDeleted_AndFails()
    {
        _bed.AddFile(KnownFolder.LocalAppData, "ExampleApp/private/secret.dat");
        _bed.FileSystem.DenyReads(_bed.PathOf(KnownFolder.LocalAppData, "ExampleApp/private"));
        var result = await Run(t => t.DeleteFolderIfEmptyAsync(Old(t), default));
        Assert.That(result.Outcome, Is.EqualTo(MigrationRunOutcome.Failed));
        Assert.That(result.Reason, Does.Contain("cannot be checked for Instella installations").And.Contain("denied"));
        Assert.That(_bed.FileSystem.DirectoryExists(OldFolder), Is.True);
    }

    [Test]
    public async Task AFolderThatCannotBeRemoved_IsLeftInPlace()
    {
        _bed.AddDirectory(KnownFolder.LocalAppData, "Empty");
        _bed.FileSystem.DenyWrites(_bed.PathOf(KnownFolder.LocalAppData, "Empty"));
        bool deleted = true;
        var result = await Run(async t => deleted = await t.DeleteFolderIfEmptyAsync(t.Folder(KnownFolder.LocalAppData, "Empty"), default));
        Assert.That(result.Outcome, Is.EqualTo(MigrationRunOutcome.Completed));
        Assert.That(deleted, Is.False);
        Assert.That(_bed.Log.Lines, Has.Some.Contains("could not be removed").And.Contains("denied"));
    }

    [Test]
    public async Task AnUnreadableFolder_LooksAbsent_SoTheConditionSkips()
    {
        // File.Exists cannot tell "denied" from "absent"; conditions see an absent file.
        _bed.FileSystem.DenyReads(OldFolder);
        var result = await Run(t => t.DeleteFilesAsync(Old(t), ["ExampleApp.exe"], default),
            when: t => t.FileExists(KnownFolder.LocalAppData, "ExampleApp/ExampleApp.exe"));
        Assert.That(result.Outcome, Is.EqualTo(MigrationRunOutcome.Skipped));
        Assert.That(_context.Migrations.Completed, Is.Empty);
    }

    [Test]
    public async Task AConditionThatCannotBeEvaluated_Skips_NeverFails()
    {
        _bed.FileSystemOverride = new ThrowingReads(_bed.FileSystem);
        foreach (var timing in new[] { MigrationTiming.BeforeCommit, MigrationTiming.AfterCommit })
        {
            var result = await Run(_ => Task.CompletedTask, timing, t => t.FileExists(KnownFolder.LocalAppData, "ExampleApp/ExampleApp.exe"));
            Assert.That(result.Outcome, Is.EqualTo(MigrationRunOutcome.Skipped), timing.ToString());
            Assert.That(result.Reason, Does.Contain("could not evaluate the condition").And.Contain("UnauthorizedAccessException"));
        }
        Assert.That(_bed.Log.Warnings, Has.Some.Contains("tried again the next time"));
    }

    [Test]
    public async Task InTheInstallPipeline_ABeforeCommitConditionThatCannotBeRead_DoesNotFailTheInstall()
    {
        _bed.FileSystemOverride = new ThrowingReads(_bed.FileSystem);
        _context = _bed.Context();
        var step = new MigrationStep(new TestMigration("b", MigrationTiming.BeforeCommit)
            { WhenFactory = t => t.FileExists(KnownFolder.LocalAppData, "ExampleApp/ExampleApp.exe") });
        var result = await new StepExecutor([step]).ExecuteAsync(_context, null, CancellationToken.None);
        Assert.That(result.Success, Is.True, result.Error);
    }

    // ---- Registry --------------------------------------------------------------------------

    [Test]
    public async Task RepointingAWriteProtectedRunValue_FailsWithTheReason()
    {
        _bed.SetRunValue("ExampleApp", $"\"{OldExe}\"");
        _bed.Platform.DenyRegistryWrites(RegistryHive.CurrentUser, RunCommand.RunKey);
        var result = await Run(t => t.RepointRunValueAsync("ExampleApp", Old(t), default));
        Assert.That(result.Outcome, Is.EqualTo(MigrationRunOutcome.Failed));
        Assert.That(result.Reason, Does.Contain("could not write Run value 'ExampleApp'").And.Contain("denied"));
        Assert.That(_bed.RunValue("ExampleApp"), Is.EqualTo($"\"{OldExe}\""));
    }

    [Test]
    public async Task DeletingAWriteProtectedRunValue_FailsWithTheReason()
    {
        _bed.SetRunValue("ExampleApp", $"\"{OldExe}\"");
        _bed.Platform.DenyRegistryWrites(RegistryHive.CurrentUser, RunCommand.RunKey);
        var result = await Run(t => t.DeleteRunValueAsync("ExampleApp", Old(t), default));
        Assert.That(result.Outcome, Is.EqualTo(MigrationRunOutcome.Failed));
        Assert.That(result.Reason, Does.Contain("could not delete Run value").And.Contain("denied"));
        Assert.That(_bed.RunValue("ExampleApp"), Is.Not.Null);
    }

    [Test]
    public async Task AnUnreadableRunKey_LooksEmpty()
    {
        _bed.SetRunValue("ExampleApp", $"\"{OldExe}\"");
        _bed.Platform.DenyRegistryReads(RegistryHive.CurrentUser, RunCommand.RunKey);
        bool changed = true;
        var result = await Run(async t => changed = await t.RepointRunValueAsync("ExampleApp", Old(t), default),
            when: t => !t.RunValueExists("Other"));
        Assert.That(result.Outcome, Is.EqualTo(MigrationRunOutcome.Completed));
        Assert.That(changed, Is.False, "nothing it can see to repoint");
    }

    [Test]
    public async Task BeforeCommit_ARunValueThatCannotBeRestored_IsAWarning()
    {
        _bed.SetRunValue("ExampleApp", $"\"{OldExe}\"");
        var result = await Run(async t =>
        {
            await t.RepointRunValueAsync("ExampleApp", Old(t), default);
            _bed.Platform.DenyRegistryWrites(RegistryHive.CurrentUser, RunCommand.RunKey);
            throw new InvalidOperationException("custom code failed");
        }, MigrationTiming.BeforeCommit);
        Assert.That(result.Outcome, Is.EqualTo(MigrationRunOutcome.RolledBack));
        Assert.That(_bed.Log.Warnings, Has.Some.Contains("rollback: could not restore Run value 'ExampleApp'"));
    }

    [Test]
    public async Task Uninstall_AnAdoptedValueThatCannotBeDeleted_IsAWarning()
    {
        var installed = Path.Combine(_bed.InstallPath, "ExampleApp.exe");
        _bed.SetRunValue("ExampleApp", $"\"{installed}\"");
        _bed.Platform.DenyRegistryWrites(RegistryHive.CurrentUser, RunCommand.RunKey);
        var manifest = new InstalledManifest
        {
            AppName = "ExampleApp", AppId = "com.example.app", Version = new Version(2, 0, 0), InstallDirectory = _bed.InstallPath,
            ExecutableName = "ExampleApp.exe", InstalledAt = DateTime.UtcNow, Files = [], InstalledPerUser = true,
            AdoptedItems = [new ManifestAdoptedItem("run-value", "ExampleApp", true, "m")],
        };
        await MigrationPipeline.RemoveAdoptedItemsAsync(manifest, _bed.InstallPath, [], _bed.Platform, _bed.Log, CancellationToken.None);
        Assert.That(_bed.Log.Warnings, Has.Some.Contains("could not remove Run value 'ExampleApp'").And.Contains("denied"));
    }

    /// <summary>A file system whose existence checks throw, as a broken network share or driver can.</summary>
    private sealed class ThrowingReads(IFileSystem inner) : IFileSystem
    {
        public bool Exists(string path) => throw new UnauthorizedAccessException($"Access to the path '{path}' is denied.");
        public bool DirectoryExists(string path) => throw new UnauthorizedAccessException($"Access to the path '{path}' is denied.");
        public Task<FileSystemResult> CopyFileAsync(string source, string dest, bool overwrite, CancellationToken ct) => inner.CopyFileAsync(source, dest, overwrite, ct);
        public Task<FileSystemResult> MoveFileAsync(string source, string dest, bool overwrite, CancellationToken ct) => inner.MoveFileAsync(source, dest, overwrite, ct);
        public Task<FileSystemResult> DeleteFileAsync(string path, CancellationToken ct) => inner.DeleteFileAsync(path, ct);
        public Task<FileSystemResult> CreateDirectoryAsync(string path, CancellationToken ct) => inner.CreateDirectoryAsync(path, ct);
        public Task<FileSystemResult> DeleteDirectoryAsync(string path, bool recursive, CancellationToken ct) => inner.DeleteDirectoryAsync(path, recursive, ct);
        public Task<FileSystemResult<byte[]>> ReadAllBytesAsync(string path, CancellationToken ct) => inner.ReadAllBytesAsync(path, ct);
        public Task<FileSystemResult> WriteAllBytesAsync(string path, byte[] data, CancellationToken ct) => inner.WriteAllBytesAsync(path, data, ct);
        public Task<FileSystemResult<Stream>> OpenReadAsync(string path, CancellationToken ct) => inner.OpenReadAsync(path, ct);
        public Task<FileSystemResult<Stream>> OpenWriteAsync(string path, CancellationToken ct) => inner.OpenWriteAsync(path, ct);
        public Task<string> ComputeSha256Async(string path, CancellationToken ct) => inner.ComputeSha256Async(path, ct);
        public IEnumerable<string> EnumerateFiles(string path, string searchPattern = "*", bool recursive = false) => inner.EnumerateFiles(path, searchPattern, recursive);
        public long GetFileSize(string path) => inner.GetFileSize(path);
    }
}
