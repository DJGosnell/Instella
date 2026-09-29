using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Installation;
using Instella.Installer.Runtime.Core.Transactions;
using Instella.Installer.Testing;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Transactions;

/// <summary>
/// A held commit (<c>CommitAsync(hold: true)</c>) moves every file but leaves the journal at
/// <see cref="TxnState.Committing"/> until it is confirmed, so a crash while the app's upgrade
/// program runs is rolled back by recovery instead of keeping new files over un-upgraded data.
/// </summary>
[TestFixture]
public class HeldCommitTests
{
    private static readonly TimeSpan[] NoDelays = [TimeSpan.Zero];
    private static readonly Version V1 = new(1, 0, 0);
    private static readonly Version V2 = new(2, 0, 0);
    private string _root = null!;

    [SetUp]
    public void SetUp() => _root = Path.Combine(Path.GetTempPath(), "instella-held-tests", Guid.NewGuid().ToString("N"));

    [Test]
    public async Task AHeldCommit_MovesTheFiles_ButTheJournalStaysCommitting()
    {
        var (fs, txn) = await UpdateAsync();
        await txn.CommitAsync(hold: true);

        Assert.That(Read(fs, "app.exe"), Is.EqualTo("v2"), "the files have moved");
        Assert.That(txn.IsHeld, Is.True);
        Assert.That(txn.State, Is.EqualTo(TxnState.Committing));
        Assert.That(JournalState(fs), Is.EqualTo(TxnState.Committing));
    }

    [Test]
    public async Task ACrashDuringAHeldCommit_IsRolledBackByRecovery()
    {
        var (fs, txn) = await UpdateAsync();
        await txn.CommitAsync(hold: true);
        // The process dies here: nothing else runs on txn.

        var rolledBack = await InstallTransaction.RecoverAsync(fs, _root, CancellationToken.None, retryDelays: NoDelays);

        Assert.That(rolledBack, Is.True);
        Assert.That(Read(fs, "app.exe"), Is.EqualTo("v1"));
        Assert.That(Read(fs, "old.dll"), Is.EqualTo("old"), "a file the update deleted is back");
        Assert.That(fs.Exists(Path.Combine(_root, "new.dll")), Is.False, "a file the update added is gone");
        Assert.That(Read(fs, InstellaOwnedPaths.InstalledManifest), Is.EqualTo("manifest-v1"));
    }

    [Test]
    public async Task AConfirmedCommit_IsKeptByRecovery()
    {
        var (fs, txn) = await UpdateAsync();
        await txn.CommitAsync(hold: true);
        await txn.ConfirmCommitAsync();

        Assert.That(txn.IsHeld, Is.False);
        Assert.That(JournalState(fs), Is.EqualTo(TxnState.Committed));
        var rolledBack = await InstallTransaction.RecoverAsync(fs, _root, CancellationToken.None, retryDelays: NoDelays);
        Assert.That(rolledBack, Is.False);
        Assert.That(Read(fs, "app.exe"), Is.EqualTo("v2"));
    }

    [Test]
    public async Task RollingBackAHeldCommit_RestoresThePreviousFiles()
    {
        var (fs, txn) = await UpdateAsync();
        await txn.CommitAsync(hold: true);
        await txn.RollbackAsync();
        await txn.CompleteAsync();

        Assert.That(txn.IsHeld, Is.False);
        Assert.That(Read(fs, "app.exe"), Is.EqualTo("v1"));
        Assert.That(Read(fs, "old.dll"), Is.EqualTo("old"));
        Assert.That(fs.Exists(Path.Combine(_root, "new.dll")), Is.False);
        Assert.That(fs.EnumerateFiles(Path.Combine(_root, ".instella"), "*", recursive: true), Is.Empty);
    }

    [Test]
    public async Task Confirm_IsANoOp_WithoutAHold_TwiceAndAfterARollback()
    {
        var (fs, txn) = await UpdateAsync();
        await txn.CommitAsync();
        Assert.That(txn.IsHeld, Is.False);
        await txn.ConfirmCommitAsync();
        Assert.That(JournalState(fs), Is.EqualTo(TxnState.Committed));

        var (fs2, held) = await UpdateAsync();
        await held.CommitAsync(hold: true);
        await held.ConfirmCommitAsync();
        await held.ConfirmCommitAsync();
        Assert.That(JournalState(fs2), Is.EqualTo(TxnState.Committed));

        var (fs3, rolled) = await UpdateAsync();
        await rolled.CommitAsync(hold: true);
        await rolled.RollbackAsync();
        await rolled.ConfirmCommitAsync();
        Assert.That(JournalState(fs3), Is.EqualTo(TxnState.RolledBack), "a rolled-back commit is never confirmed");
    }

    private async Task<(InMemoryFileSystem Fs, InstallTransaction Txn)> UpdateAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "instella-held-tests", Guid.NewGuid().ToString("N"));
        var fs = new InMemoryFileSystem();
        fs.AddFile(Path.Combine(_root, "app.exe"), B("v1"));
        fs.AddFile(Path.Combine(_root, "old.dll"), B("old"));
        fs.AddFile(Path.Combine(_root, InstellaOwnedPaths.InstalledManifest), B("manifest-v1"));

        var txn = await InstallTransaction.BeginAsync(fs, _root, TxnKind.Update, V1, V2, CancellationToken.None, retryDelays: NoDelays);
        await Stage(txn, "app.exe", "v2");
        await Stage(txn, "new.dll", "new");
        txn.Delete("old.dll");
        await txn.StageOwnedFileAsync(InstellaOwnedPaths.InstalledManifest, new MemoryStream(B("manifest-v2")), false, CancellationToken.None);
        return (fs, txn);
    }

    private TxnState JournalState(InMemoryFileSystem fs)
    {
        var journal = fs.Snapshot().Single(kv => kv.Key.EndsWith(TransactionJournal.FileName, StringComparison.OrdinalIgnoreCase)
                                                 && kv.Key.StartsWith(Path.GetFullPath(_root), StringComparison.OrdinalIgnoreCase));
        return System.Text.Json.JsonSerializer.Deserialize(journal.Value, TransactionJournalJsonContext.Default.TransactionJournal)!.State;
    }

    private string Read(InMemoryFileSystem fs, string rel) =>
        Encoding.UTF8.GetString(fs.Snapshot()[Path.GetFullPath(Path.Combine(_root, rel))]);

    private static Task<string> Stage(InstallTransaction txn, string path, string text) =>
        txn.StageFileAsync(path, new MemoryStream(B(text)), Sha(B(text)), text.Length, false, CancellationToken.None);

    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    private static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));
}
