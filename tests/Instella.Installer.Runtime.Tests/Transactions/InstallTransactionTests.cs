using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.FileSystem;
using Instella.Core.Installation;
using Instella.Core.Trust;
using Instella.Installer.Runtime.Core.Transactions;
using Instella.Installer.Testing;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Transactions;

[TestFixture]
public class InstallTransactionTests
{
    private static readonly TimeSpan[] NoDelays = [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero];
    private static readonly Version V1 = new(1, 0, 0);
    private static readonly Version V2 = new(2, 0, 0);

    private string _root = null!;

    [SetUp]
    public void SetUp() => _root = Path.Combine(Path.GetTempPath(), "instella-txn-tests", Guid.NewGuid().ToString("N"));

    [Test]
    public async Task Commit_ReplacesAddsAndDeletes_ThenCompleteRemovesTxnFolder()
    {
        var fs = new InMemoryFileSystem();
        var old = new Dictionary<string, byte[]> { ["app.exe"] = B("v1"), ["old.dll"] = B("old"), ["same.txt"] = B("same") };
        Seed(fs, old);

        var txn = await InstallTransaction.BeginAsync(fs, _root, TxnKind.Update, V1, V2, CancellationToken.None, retryDelays: NoDelays);
        await Stage(txn, "app.exe", B("v2"));
        await Stage(txn, "sub/new.dll", B("new"));
        txn.Delete("old.dll");
        await txn.StageOwnedFileAsync(InstellaOwnedPaths.InstalledManifest, new MemoryStream(B("manifest-v2")), false, CancellationToken.None);
        await txn.CommitAsync();
        await txn.CompleteAsync();

        var tree = Tree(fs);
        Assert.That(tree.Keys, Is.EquivalentTo(new[] { "app.exe", "same.txt", "sub/new.dll", InstellaOwnedPaths.InstalledManifest }));
        Assert.That(Encoding.UTF8.GetString(tree["app.exe"]), Is.EqualTo("v2"));
        Assert.That(fs.EnumerateFiles(Path.Combine(_root, ".instella"), "*", recursive: true), Is.Empty);
    }

    [Test]
    public async Task StageFile_HashMismatch_ThrowsTrustException_AndTouchesNothingLive()
    {
        var fs = new InMemoryFileSystem();
        Seed(fs, new Dictionary<string, byte[]> { ["app.exe"] = B("v1") });
        var txn = await InstallTransaction.BeginAsync(fs, _root, TxnKind.Update, V1, V2, CancellationToken.None);

        Assert.ThrowsAsync<UpdateTrustException>(() =>
            txn.StageFileAsync("app.exe", new MemoryStream(B("evil")), Sha(B("v2")), null, false, CancellationToken.None));
        Assert.That(txn.IsStaged("app.exe"), Is.False);
        Assert.That(Encoding.UTF8.GetString(Tree(fs)["app.exe"]), Is.EqualTo("v1"));
    }

    [TestCase(".instella-manifest.json")]
    [TestCase("instella.exe")]
    [TestCase(".instella/txn/x/journal.json")]
    [TestCase("../escape.dll")]
    public async Task StageFile_RejectsOwnedAndUnsafePaths(string path)
    {
        var fs = new InMemoryFileSystem();
        var txn = await InstallTransaction.BeginAsync(fs, _root, TxnKind.Update, V1, V2, CancellationToken.None);
        Assert.ThrowsAsync<UnsafePathException>(() =>
            txn.StageFileAsync(path, new MemoryStream(B("x")), null, null, false, CancellationToken.None));
    }

    [Test]
    public async Task Commit_RetriesTransientLocks()
    {
        var fs = new LockingFileSystem(new InMemoryFileSystem(), failuresBeforeSuccess: 2);
        Seed(fs.Inner, new Dictionary<string, byte[]> { ["app.exe"] = B("v1") });
        var txn = await InstallTransaction.BeginAsync(fs, _root, TxnKind.Update, V1, V2, CancellationToken.None, retryDelays: NoDelays);
        await Stage(txn, "app.exe", B("v2"));
        fs.LockPath = Path.Combine(_root, "app.exe");

        await txn.CommitAsync();

        Assert.That(Encoding.UTF8.GetString(Tree(fs.Inner)["app.exe"]), Is.EqualTo("v2"));
        Assert.That(fs.LockedAttempts, Is.EqualTo(2));
    }

    [Test]
    public async Task RollbackAfterCommitted_RestoresOldTree()
    {
        var fs = new InMemoryFileSystem();
        var old = new Dictionary<string, byte[]> { ["app.exe"] = B("v1"), ["gone.dll"] = B("g") };
        Seed(fs, old);
        var txn = await InstallTransaction.BeginAsync(fs, _root, TxnKind.Upgrade, V1, V2, CancellationToken.None);
        await Stage(txn, "app.exe", B("v2"));
        await Stage(txn, "deep/er/new.dll", B("n"));
        txn.Delete("gone.dll");
        await txn.CommitAsync();

        await txn.RollbackAsync();
        await txn.CompleteAsync();

        AssertTree(fs, old);
        Assert.That(fs.DirectoryExists(Path.Combine(_root, "deep")), Is.False, "directories created by commit are removed");
    }

    [Test]
    public async Task HasInterruptedCommit_SeesCommittingJournalOnDisk()
    {
        var root = Path.Combine(Path.GetTempPath(), "instella-txn-real", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "app.exe"), "v1");
            var fs = new FaultInjectingFileSystem(RealFileSystem.Instance);
            var txn = await InstallTransaction.BeginAsync(fs, root, TxnKind.Update, V1, V2, CancellationToken.None, retryDelays: NoDelays);
            await Stage(txn, "app.exe", B("v2"));
            await txn.VerifyAsync(CancellationToken.None);
            Assert.That(TransactionJournal.HasInterruptedCommit(root), Is.False);

            // Journal tmp write + rename to Committing succeed, the first live rename fails.
            fs.Arm(2);
            Assert.ThrowsAsync<IOException>(() => txn.CommitAsync());
            fs.Arm(null);
            Assert.That(TransactionJournal.HasInterruptedCommit(root), Is.True);

            Assert.That(await InstallTransaction.RecoverAsync(fs, root, CancellationToken.None), Is.True);
            Assert.That(TransactionJournal.HasInterruptedCommit(root), Is.False);
            Assert.That(File.ReadAllText(Path.Combine(root, "app.exe")), Is.EqualTo("v1"));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task Recover_JournalOfAnotherVersion_RefusesAndTouchesNothing()
    {
        // A journal this version cannot read is never acted on (exit 23 upstream).
        var fs = new InMemoryFileSystem();
        Seed(fs, new Dictionary<string, byte[]> { ["app.exe"] = B("v1") });
        var txn = await InstallTransaction.BeginAsync(fs, _root, TxnKind.Update, V1, V2, CancellationToken.None, retryDelays: NoDelays);
        await Stage(txn, "app.exe", B("v2"));
        var journalPath = fs.EnumerateFiles(Path.Combine(_root, ".instella"), TransactionJournal.FileName, recursive: true).Single();
        var json = Encoding.UTF8.GetString((await fs.ReadAllBytesAsync(journalPath, CancellationToken.None)).Value!);
        Assert.That(json, Does.Contain("\"journalVersion\": 1"));
        await fs.WriteAllBytesAsync(journalPath, B(json.Replace("\"journalVersion\": 1", "\"journalVersion\": 2")), CancellationToken.None);
        var before = Tree(fs);

        var ex = Assert.ThrowsAsync<UnsupportedJournalException>(() => InstallTransaction.RecoverAsync(fs, _root, CancellationToken.None));

        Assert.That(ex!.Version, Is.EqualTo(2));
        Assert.That(fs.Exists(journalPath), Is.True, "the journal is left for the version that wrote it");
        Assert.That(SameTree(Tree(fs), before), Is.True);
    }

    // ---- an unreadable journal is never deleted ----

    [Test]
    public async Task Recover_UnparsableJournal_RefusesAndKeepsBackup()
    {
        var (fs, journalPath) = await InterruptedCommitAsync();
        await fs.WriteAllBytesAsync(journalPath, B("{ this is not json"), CancellationToken.None);
        var before = Tree(fs);
        var backups = fs.EnumerateFiles(Path.Combine(_root, ".instella"), "*", recursive: true).Count(f => f.Contains("backup"));

        var ex = Assert.ThrowsAsync<UnreadableJournalException>(() => InstallTransaction.RecoverAsync(fs, _root, CancellationToken.None));

        Assert.That(ex!.Message, Does.Contain("cannot read").And.Contain("Nothing was changed"));
        Assert.That(backups, Is.GreaterThan(0));
        Assert.That(fs.EnumerateFiles(Path.Combine(_root, ".instella"), "*", recursive: true).Count(f => f.Contains("backup")), Is.EqualTo(backups),
            "backup/ holds the only copy of the originals");
        Assert.That(SameTree(Tree(fs), before), Is.True);
    }

    [Test]
    public async Task Recover_JournalWithoutVersion_Refuses()
    {
        var (fs, journalPath) = await InterruptedCommitAsync();
        var json = Encoding.UTF8.GetString((await fs.ReadAllBytesAsync(journalPath, CancellationToken.None)).Value!);
        await fs.WriteAllBytesAsync(journalPath, B(json.Replace("\"journalVersion\": 1,", "")), CancellationToken.None);

        var ex = Assert.ThrowsAsync<UnreadableJournalException>(() => InstallTransaction.RecoverAsync(fs, _root, CancellationToken.None));
        Assert.That(ex!.Message, Does.Contain("no journalVersion"));
        Assert.That(fs.Exists(journalPath), Is.True);
    }

    [Test]
    public async Task Recover_MissingJournalWithBackupFiles_Refuses()
    {
        var (fs, journalPath) = await InterruptedCommitAsync();
        await fs.DeleteFileAsync(journalPath, CancellationToken.None);

        var ex = Assert.ThrowsAsync<UnreadableJournalException>(() => InstallTransaction.RecoverAsync(fs, _root, CancellationToken.None));
        Assert.That(ex!.Message, Does.Contain("backup/ holds files"));
    }

    [Test]
    public async Task Recover_MissingJournalEmptyBackup_DeletesFolder()
    {
        var fs = new InMemoryFileSystem();
        Seed(fs, new Dictionary<string, byte[]> { ["app.exe"] = B("v1") });
        // A crash before the first journal write: only staged files.
        fs.AddFile(Path.Combine(_root, ".instella", "txn", Guid.NewGuid().ToString("N"), "stage", "app.exe"), B("v2"));

        Assert.That(await InstallTransaction.RecoverAsync(fs, _root, CancellationToken.None), Is.False);
        Assert.That(fs.EnumerateFiles(Path.Combine(_root, ".instella"), "*", recursive: true), Is.Empty);
        Assert.That(Encoding.UTF8.GetString(Tree(fs)["app.exe"]), Is.EqualTo("v1"));
    }

    [Test]
    public async Task Recover_TxnIdMismatch_Refuses()
    {
        var (fs, journalPath) = await InterruptedCommitAsync();
        var json = Encoding.UTF8.GetString((await fs.ReadAllBytesAsync(journalPath, CancellationToken.None)).Value!);
        var folder = Path.GetFileName(Path.GetDirectoryName(journalPath)!);
        await fs.WriteAllBytesAsync(journalPath, B(json.Replace(folder, new string('0', 32))), CancellationToken.None);

        var ex = Assert.ThrowsAsync<UnreadableJournalException>(() => InstallTransaction.RecoverAsync(fs, _root, CancellationToken.None));
        Assert.That(ex!.Message, Does.Contain("belongs to transaction"));
    }

    /// <summary>A commit interrupted after its first live rename: the journal says Committing and backup/ holds v1.</summary>
    private async Task<(InMemoryFileSystem Fs, string JournalPath)> InterruptedCommitAsync()
    {
        var fs = new InMemoryFileSystem();
        Seed(fs, new Dictionary<string, byte[]> { ["app.exe"] = B("v1"), ["lib.dll"] = B("lib1") });
        var faulty = new FaultInjectingFileSystem(fs);
        var txn = await InstallTransaction.BeginAsync(faulty, _root, TxnKind.Update, V1, V2, CancellationToken.None, retryDelays: NoDelays);
        await Stage(txn, "app.exe", B("v2"));
        await Stage(txn, "lib.dll", B("lib2"));
        await txn.VerifyAsync(CancellationToken.None);
        faulty.Arm(4);
        Assert.CatchAsync<IOException>(() => txn.CommitAsync());
        var journalPath = fs.EnumerateFiles(Path.Combine(_root, ".instella"), TransactionJournal.FileName, recursive: true).Single();
        return (fs, journalPath);
    }

    // ---- staged bytes are on the disk before the commit is journalled ----

    [Test]
    public async Task EveryStagedFile_IsFlushed_BeforeTheJournalSaysCommitting()
    {
        var events = new List<string>();
        var fs = new InMemoryFileSystem();
        Seed(fs, new Dictionary<string, byte[]> { ["app.exe"] = B("v1") });
        var recording = new JournalRecordingFileSystem(fs, events);
        InstallTransaction.StagedFileFlushed = rel => events.Add($"flushed:{rel}");
        try
        {
            var txn = await InstallTransaction.BeginAsync(recording, _root, TxnKind.Update, V1, V2, CancellationToken.None, retryDelays: NoDelays);
            await Stage(txn, "app.exe", B("v2"));
            await Stage(txn, "lib/a.dll", B("a"));
            await txn.VerifyAsync(CancellationToken.None);
            await txn.CommitAsync();
        }
        finally
        {
            InstallTransaction.StagedFileFlushed = null;
        }

        var committing = events.IndexOf("journal:Committing");
        Assert.That(committing, Is.GreaterThan(0), string.Join(", ", events));
        Assert.That(events.Take(committing).Where(e => e.StartsWith("flushed:")), Is.EquivalentTo(new[] { "flushed:app.exe", "flushed:lib/a.dll" }));
        Assert.That(events.Skip(committing).Where(e => e.StartsWith("flushed:")), Is.Empty);
    }

    // ---- no staged file can exceed its signed size ----

    [Test]
    public async Task StageFile_LongerThanExpected_StopsAtLimit()
    {
        var fs = new InMemoryFileSystem();
        var txn = await InstallTransaction.BeginAsync(fs, _root, TxnKind.Update, V1, V2, CancellationToken.None, retryDelays: NoDelays);

        var ex = Assert.ThrowsAsync<UpdateTrustException>(() =>
            txn.StageFileAsync("app.exe", new EndlessStream(), Sha(B("x")), expectedSize: 1000, false, CancellationToken.None));

        Assert.That(ex!.Message, Does.Contain("larger than the 1000 bytes"));
        Assert.That(txn.IsStaged("app.exe"), Is.False);
    }

    private sealed class EndlessStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) { Array.Fill(buffer, (byte)'x', offset, count); return count; }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Records each journal state as it is renamed into place.</summary>
    private sealed class JournalRecordingFileSystem(InMemoryFileSystem inner, List<string> events) : IFileSystem
    {
        public async Task<FileSystemResult> MoveFileAsync(string source, string dest, bool overwrite, CancellationToken ct)
        {
            if (source.EndsWith("journal.tmp", StringComparison.Ordinal))
            {
                var json = Encoding.UTF8.GetString((await inner.ReadAllBytesAsync(source, ct)).Value!);
                var state = System.Text.Json.JsonDocument.Parse(json).RootElement.GetProperty("state").GetString();
                events.Add($"journal:{state}");
            }
            return await inner.MoveFileAsync(source, dest, overwrite, ct);
        }

        public Task<FileSystemResult> CopyFileAsync(string source, string dest, bool overwrite, CancellationToken ct) => inner.CopyFileAsync(source, dest, overwrite, ct);
        public Task<FileSystemResult> DeleteFileAsync(string path, CancellationToken ct) => inner.DeleteFileAsync(path, ct);
        public Task<FileSystemResult> CreateDirectoryAsync(string path, CancellationToken ct) => inner.CreateDirectoryAsync(path, ct);
        public Task<FileSystemResult> DeleteDirectoryAsync(string path, bool recursive, CancellationToken ct) => inner.DeleteDirectoryAsync(path, recursive, ct);
        public Task<FileSystemResult<byte[]>> ReadAllBytesAsync(string path, CancellationToken ct) => inner.ReadAllBytesAsync(path, ct);
        public Task<FileSystemResult> WriteAllBytesAsync(string path, byte[] data, CancellationToken ct) => inner.WriteAllBytesAsync(path, data, ct);
        public Task<FileSystemResult<Stream>> OpenReadAsync(string path, CancellationToken ct) => inner.OpenReadAsync(path, ct);
        public Task<FileSystemResult<Stream>> OpenWriteAsync(string path, CancellationToken ct) => inner.OpenWriteAsync(path, ct);
        public bool Exists(string path) => inner.Exists(path);
        public bool DirectoryExists(string path) => inner.DirectoryExists(path);
        public Task<string> ComputeSha256Async(string path, CancellationToken ct) => inner.ComputeSha256Async(path, ct);
        public IEnumerable<string> EnumerateFiles(string path, string searchPattern = "*", bool recursive = false) => inner.EnumerateFiles(path, searchPattern, recursive);
        public long GetFileSize(string path) => inner.GetFileSize(path);
    }

    // ---- Fault-injection property test ----

    [Test]
    public async Task FaultAtEveryCommitStep_RecoveryConvergesToOldOrNewTree([Range(0, 24)] int seed)
    {
        var scenario = Scenario.Random(seed);
        var commitCalls = await CountCommitCallsAsync(scenario);

        for (var k = 0; k <= commitCalls; k++)
        {
            var (fs, faulty, txn) = await PrepareAsync(scenario);
            faulty.Arm(k);
            var crashed = false;
            try { await txn.CommitAsync(); }
            catch (IOException) { crashed = true; }
            faulty.Arm(null);

            await InstallTransaction.RecoverAsync(faulty, _root, CancellationToken.None, retryDelays: NoDelays);

            var tree = Tree(fs);
            var isNew = SameTree(tree, scenario.NewTree);
            var isOld = SameTree(tree, scenario.OldTree);
            Assert.That(isNew || isOld, $"seed {seed}, fault at {k}: tree is neither old nor new");
            if (!crashed) Assert.That(isNew, $"seed {seed}: a commit that finished must leave the new tree");
            AssertManifestMatchesTree(tree);
            Assert.That(fs.EnumerateFiles(Path.Combine(_root, ".instella"), "*", recursive: true), Is.Empty);
        }
    }

    [Test]
    public async Task FaultDuringRecovery_RerunConverges([Range(0, 7)] int seed)
    {
        var scenario = Scenario.Random(100 + seed);
        var commitCalls = await CountCommitCallsAsync(scenario);

        for (var k = 0; k < commitCalls; k += Math.Max(1, commitCalls / 6))
        {
            for (var j = 0; ; j++)
            {
                var (fs, faulty, txn) = await PrepareAsync(scenario);
                faulty.Arm(k);
                try { await txn.CommitAsync(); } catch (IOException) { }

                faulty.Arm(j);
                var recoveryFailed = false;
                try { await InstallTransaction.RecoverAsync(faulty, _root, CancellationToken.None, retryDelays: NoDelays); }
                catch (IOException) { recoveryFailed = true; }
                faulty.Arm(null);
                await InstallTransaction.RecoverAsync(faulty, _root, CancellationToken.None, retryDelays: NoDelays);

                var tree = Tree(fs);
                Assert.That(SameTree(tree, scenario.NewTree) || SameTree(tree, scenario.OldTree),
                    $"seed {seed}, commit fault {k}, recovery fault {j}: did not converge");
                AssertManifestMatchesTree(tree);
                if (!recoveryFailed) break;
            }
        }
    }

    private async Task<int> CountCommitCallsAsync(Scenario scenario)
    {
        var (_, faulty, txn) = await PrepareAsync(scenario);
        faulty.Arm(null);
        await txn.CommitAsync();
        return faulty.MutatingCalls;
    }

    private async Task<(InMemoryFileSystem Fs, FaultInjectingFileSystem Faulty, InstallTransaction Txn)> PrepareAsync(Scenario s)
    {
        var fs = new InMemoryFileSystem();
        Seed(fs, s.OldTree);
        var faulty = new FaultInjectingFileSystem(fs);
        var txn = await InstallTransaction.BeginAsync(faulty, _root, TxnKind.Update, V1, V2, CancellationToken.None, retryDelays: NoDelays);
        foreach (var (path, bytes) in s.NewTree)
        {
            if (InstellaOwnedPaths.IsOwned(path))
                await txn.StageOwnedFileAsync(path, new MemoryStream(bytes), false, CancellationToken.None);
            else if (!s.OldTree.TryGetValue(path, out var oldBytes) || !oldBytes.SequenceEqual(bytes))
                await Stage(txn, path, bytes);
        }
        foreach (var path in s.OldTree.Keys.Where(p => !s.NewTree.ContainsKey(p)))
            txn.Delete(path);
        await txn.VerifyAsync(CancellationToken.None);
        faulty.Arm(null);
        return (fs, faulty, txn);
    }

    private sealed record Scenario(Dictionary<string, byte[]> OldTree, Dictionary<string, byte[]> NewTree)
    {
        public static Scenario Random(int seed)
        {
            var rng = new Random(seed);
            var dirs = new[] { "", "bin/", "bin/x64/", "data/", "lib/deep/er/" };
            var oldTree = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            var count = rng.Next(0, 51);
            for (var i = 0; i < count; i++)
                oldTree[$"{dirs[rng.Next(dirs.Length)]}f{i}.bin"] = RandomBytes(rng);

            var newTree = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var (path, bytes) in oldTree)
            {
                switch (rng.Next(4))
                {
                    case 0: break;                                   // deleted
                    case 1: newTree[path] = RandomBytes(rng); break;  // changed
                    default: newTree[path] = bytes; break;           // unchanged
                }
            }
            var added = rng.Next(0, 10);
            for (var i = 0; i < added; i++)
                newTree[$"{dirs[rng.Next(dirs.Length)]}new/n{i}.bin"] = RandomBytes(rng);

            oldTree[InstellaOwnedPaths.InstalledManifest] = ManifestFor(oldTree);
            newTree[InstellaOwnedPaths.InstalledManifest] = ManifestFor(newTree);
            return new Scenario(oldTree, newTree);
        }

        private static byte[] RandomBytes(Random rng)
        {
            var b = new byte[rng.Next(1, 64)];
            rng.NextBytes(b);
            return b;
        }
    }

    /// <summary>A toy installed manifest: one "path:sha" line per payload file.</summary>
    private static byte[] ManifestFor(Dictionary<string, byte[]> tree) =>
        B(string.Join('\n', tree.Where(kv => !InstellaOwnedPaths.IsOwned(kv.Key))
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => $"{kv.Key}:{Sha(kv.Value)}")));

    private static void AssertManifestMatchesTree(Dictionary<string, byte[]> tree)
    {
        Assert.That(tree.TryGetValue(InstellaOwnedPaths.InstalledManifest, out var manifest), Is.True, "manifest missing");
        Assert.That(manifest, Is.EqualTo(ManifestFor(tree)), "installed manifest does not describe the tree");
    }

    private void Seed(InMemoryFileSystem fs, Dictionary<string, byte[]> tree)
    {
        fs.AddDirectory(_root);
        foreach (var (path, bytes) in tree)
            fs.AddFile(SafePath.Combine(_root, path), bytes);
    }

    private Dictionary<string, byte[]> Tree(InMemoryFileSystem fs)
    {
        var prefix = Path.GetFullPath(_root) + Path.DirectorySeparatorChar;
        return fs.Snapshot()
            .Where(kv => kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(kv => (Rel: kv.Key[prefix.Length..].Replace(Path.DirectorySeparatorChar, '/'), kv.Value))
            .Where(x => !x.Rel.StartsWith(".instella/", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(x => x.Rel, x => x.Value, StringComparer.OrdinalIgnoreCase);
    }

    private void AssertTree(InMemoryFileSystem fs, Dictionary<string, byte[]> expected) =>
        Assert.That(SameTree(Tree(fs), expected), Is.True);

    private static bool SameTree(Dictionary<string, byte[]> a, Dictionary<string, byte[]> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v.SequenceEqual(kv.Value));

    private static Task<string> Stage(InstallTransaction txn, string path, byte[] bytes) =>
        txn.StageFileAsync(path, new MemoryStream(bytes), Sha(bytes), bytes.Length, false, CancellationToken.None);

    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    private static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    /// <summary>Reports <see cref="FileSystemErrorType.InUse"/> for moves of <see cref="LockPath"/> a few times.</summary>
    private sealed class LockingFileSystem(InMemoryFileSystem inner, int failuresBeforeSuccess) : IFileSystem
    {
        public InMemoryFileSystem Inner => inner;
        public string? LockPath { get; set; }
        public int LockedAttempts { get; private set; }

        public Task<FileSystemResult> MoveFileAsync(string source, string dest, bool overwrite, CancellationToken ct)
        {
            if (LockPath is not null && string.Equals(Path.GetFullPath(source), Path.GetFullPath(LockPath), StringComparison.OrdinalIgnoreCase)
                && LockedAttempts < failuresBeforeSuccess)
            {
                LockedAttempts++;
                return Task.FromResult(FileSystemResult.Fail(new FileSystemError(FileSystemErrorType.InUse, "locked")));
            }
            return inner.MoveFileAsync(source, dest, overwrite, ct);
        }

        public Task<FileSystemResult> CopyFileAsync(string source, string dest, bool overwrite, CancellationToken ct) => inner.CopyFileAsync(source, dest, overwrite, ct);
        public Task<FileSystemResult> DeleteFileAsync(string path, CancellationToken ct) => inner.DeleteFileAsync(path, ct);
        public Task<FileSystemResult> CreateDirectoryAsync(string path, CancellationToken ct) => inner.CreateDirectoryAsync(path, ct);
        public Task<FileSystemResult> DeleteDirectoryAsync(string path, bool recursive, CancellationToken ct) => inner.DeleteDirectoryAsync(path, recursive, ct);
        public Task<FileSystemResult<byte[]>> ReadAllBytesAsync(string path, CancellationToken ct) => inner.ReadAllBytesAsync(path, ct);
        public Task<FileSystemResult> WriteAllBytesAsync(string path, byte[] data, CancellationToken ct) => inner.WriteAllBytesAsync(path, data, ct);
        public Task<FileSystemResult<Stream>> OpenReadAsync(string path, CancellationToken ct) => inner.OpenReadAsync(path, ct);
        public Task<FileSystemResult<Stream>> OpenWriteAsync(string path, CancellationToken ct) => inner.OpenWriteAsync(path, ct);
        public bool Exists(string path) => inner.Exists(path);
        public bool DirectoryExists(string path) => inner.DirectoryExists(path);
        public Task<string> ComputeSha256Async(string path, CancellationToken ct) => inner.ComputeSha256Async(path, ct);
        public IEnumerable<string> EnumerateFiles(string path, string searchPattern = "*", bool recursive = false) => inner.EnumerateFiles(path, searchPattern, recursive);
        public long GetFileSize(string path) => inner.GetFileSize(path);
    }
}
