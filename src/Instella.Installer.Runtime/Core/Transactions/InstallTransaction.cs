using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.FileSystem;
using Instella.Core.Installation;
using Instella.Core.Logging;
using Instella.Core.Trust;

namespace Instella.Installer.Runtime.Core.Transactions;

/// <summary>
/// Replaces files in an install directory so that the operation either completes fully or
/// leaves the previous tree intact, across crashes, power loss and cancellation. New files are staged and verified under <c>.instella/txn/{id}/stage</c>; commit then
/// swaps each live file into <c>backup/</c> and the staged file into place. Everything lives
/// under the install root, so every commit step is a same-volume rename.
/// </summary>
/// <remarks>
/// The journal is written only at state transitions. Which step of an operation has run is
/// read back from which of the live, stage and backup files exist, so rollback and recovery
/// are idempotent by inspection: they can be interrupted and re-run safely.
/// </remarks>
internal sealed class InstallTransaction
{
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    /// <summary>Backoff between retries of a rename that hit a transient lock (AV scanner, indexer).</summary>
    internal static readonly IReadOnlyList<TimeSpan> DefaultRetryDelays =
        [TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(400),
         TimeSpan.FromMilliseconds(800), TimeSpan.FromMilliseconds(1600)];

    private readonly IFileSystem _fs;
    private readonly IInstellaLogger? _log;
    private readonly IReadOnlyList<TimeSpan> _retryDelays;
    private readonly string _txnDir;
    private readonly string _stageDir;
    private readonly string _backupDir;
    private readonly TxnKind _kind;
    private readonly Version? _fromVersion;
    private readonly Version _toVersion;

    // Staged replaces keyed by canonical relative path; value = expected SHA-256 (lower hex).
    private readonly Dictionary<string, string> _staged = new(PathComparer);
    private readonly HashSet<string> _deletes = new(PathComparer);
    private IReadOnlyList<TxnOperation> _ops = [];
    private IReadOnlyList<string> _createdDirs = [];
    private bool _completed;

    private InstallTransaction(
        IFileSystem fs, string root, string txnId, TxnKind kind, Version? from, Version to,
        IInstellaLogger? log, IReadOnlyList<TimeSpan>? retryDelays)
    {
        _fs = fs;
        _log = log;
        _retryDelays = retryDelays ?? DefaultRetryDelays;
        Root = Path.GetFullPath(root);
        TxnId = txnId;
        _kind = kind;
        _fromVersion = from;
        _toVersion = to;
        _txnDir = TxnDirectoryOf(Root, txnId);
        _stageDir = Path.Combine(_txnDir, "stage");
        _backupDir = Path.Combine(_txnDir, "backup");
    }

    /// <summary>The install root the transaction replaces files in.</summary>
    public string Root { get; }

    /// <summary>Transaction id (folder name under <c>.instella/txn</c>).</summary>
    public string TxnId { get; }

    /// <summary>Current state.</summary>
    public TxnState State { get; private set; } = TxnState.Staging;

    /// <summary>Relative paths staged so far (canonical form).</summary>
    public IReadOnlyCollection<string> StagedPaths => _staged.Keys;

    /// <summary>Starts a transaction and writes its first journal.</summary>
    public static async Task<InstallTransaction> BeginAsync(
        IFileSystem fs, string root, TxnKind kind, Version? fromVersion, Version toVersion,
        CancellationToken ct, IInstellaLogger? log = null, IReadOnlyList<TimeSpan>? retryDelays = null)
    {
        var txn = new InstallTransaction(fs, root, Guid.NewGuid().ToString("N"), kind, fromVersion, toVersion, log, retryDelays);
        await Check(fs.CreateDirectoryAsync(txn._stageDir, ct), "create the staging folder");
        await txn.WriteJournalAsync(TxnState.Staging, ct);
        return txn;
    }

    /// <summary>True when <paramref name="relativePath"/> has been staged.</summary>
    public bool IsStaged(string relativePath) => _staged.ContainsKey(Canonical(relativePath));

    /// <summary>Full path of the staged copy of <paramref name="relativePath"/>.</summary>
    public string StagedPathOf(string relativePath) => SafePath.Combine(_stageDir, Canonical(relativePath));

    /// <summary>
    /// Writes <paramref name="source"/> into the stage as <paramref name="relativePath"/>,
    /// hashing while it streams. Nothing is written anywhere live.
    /// </summary>
    /// <param name="relativePath">Path under the install root.</param>
    /// <param name="source">The file's content.</param>
    /// <param name="expectedSha256">When set, a mismatch deletes the staged file and throws <see cref="UpdateTrustException"/>.</param>
    /// <param name="expectedSize">When set, a size mismatch is treated the same way.</param>
    /// <param name="executable">Mark the file executable (POSIX).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The SHA-256 (lower hex) of the staged bytes.</returns>
    public Task<string> StageFileAsync(
        string relativePath, Stream source, string? expectedSha256, long? expectedSize, bool executable,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        return StageFileAsync(relativePath, (target, token) => source.CopyToAsync(target, token),
            expectedSha256, expectedSize, executable, ct);
    }

    /// <summary>
    /// Stages <paramref name="relativePath"/> from bytes that <paramref name="write"/> writes into
    /// a forward-only stream (for example a patch being applied), hashing as they arrive.
    /// </summary>
    public Task<string> StageFileAsync(
        string relativePath, Func<Stream, CancellationToken, Task> write, string? expectedSha256, long? expectedSize,
        bool executable, CancellationToken ct)
    {
        var rel = Canonical(relativePath);
        if (InstellaOwnedPaths.IsOwned(rel))
            throw new UnsafePathException(relativePath, "is an Instella-owned path");
        return StageCoreAsync(rel, write, expectedSha256, expectedSize, executable, ct);
    }

    /// <summary>
    /// Stages an Instella-owned file (the stub, the app icon or the installed manifest). The installed
    /// manifest is always committed last, so it names the new version only once every
    /// other operation has completed.
    /// </summary>
    public Task<string> StageOwnedFileAsync(string relativePath, Stream source, bool executable, CancellationToken ct)
    {
        var rel = Canonical(relativePath);
        var isStateFile = rel.StartsWith(InstellaOwnedPaths.StateDirectory + "/", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(rel, InstellaOwnedPaths.AppIcon, StringComparison.OrdinalIgnoreCase);
        if (!InstellaOwnedPaths.IsOwned(rel) || isStateFile)
            throw new ArgumentException($"'{relativePath}' is not a stageable Instella-owned file", nameof(relativePath));
        return StageCoreAsync(rel, (target, token) => source.CopyToAsync(target, token),
            expectedSha256: null, expectedSize: null, executable, ct);
    }

    /// <summary>Records that the live file at <paramref name="relativePath"/> is removed on commit.</summary>
    public void Delete(string relativePath)
    {
        var rel = Canonical(relativePath);
        if (InstellaOwnedPaths.IsOwned(rel))
            throw new UnsafePathException(relativePath, "is an Instella-owned path");
        EnsureState(TxnState.Staging, "add operations");
        _deletes.Add(rel);
    }

    /// <summary>Removes a staged file from the transaction (the live file is then left alone).</summary>
    public async Task UnstageAsync(string relativePath, CancellationToken ct)
    {
        var rel = Canonical(relativePath);
        EnsureState(TxnState.Staging, "remove operations");
        if (_staged.Remove(rel))
            await Check(_fs.DeleteFileAsync(SafePath.Combine(_stageDir, rel), ct), $"remove staged '{rel}'");
    }

    /// <summary>
    /// Re-hashes every staged file against the hash recorded when it was staged (catches
    /// caller bugs and on-disk corruption), then records <see cref="TxnState.Staged"/>.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="onFile">Called after each file with (files verified, total).</param>
    public async Task VerifyAsync(CancellationToken ct, Action<int, int>? onFile = null)
    {
        EnsureState(TxnState.Staging, "verify");
        var done = 0;
        foreach (var (rel, expected) in _staged)
        {
            ct.ThrowIfCancellationRequested();
            var path = SafePath.Combine(_stageDir, rel);
            if (!_fs.Exists(path))
                throw new UpdateTrustException($"staged file '{rel}' is missing");
            var actual = await _fs.ComputeSha256Async(path, ct);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                throw new UpdateTrustException($"staged file '{rel}' changed after staging");
            onFile?.Invoke(++done, _staged.Count);
        }
        await WriteJournalAsync(TxnState.Staged, ct);
    }

    /// <summary>
    /// Swaps every staged file into place and moves deleted files aside. Ignores
    /// cancellation: once the first rename has happened the operation must not stop half-way.
    /// Throws when a rename keeps failing; the caller then calls <see cref="RollbackAsync"/>.
    /// </summary>
    /// <param name="onOperation">Called after each operation with (operations done, total).</param>
    public async Task CommitAsync(Action<int, int>? onOperation = null)
    {
        var ct = CancellationToken.None;
        if (State == TxnState.Staging)
            await VerifyAsync(ct);
        EnsureState(TxnState.Staged, "commit");

        // Payload replaces, then deletes, then owned files, with the installed manifest last.
        var ops = new List<TxnOperation>();
        var manifest = InstellaOwnedPaths.InstalledManifest;
        foreach (var rel in _staged.Keys.Where(p => !InstellaOwnedPaths.IsOwned(p)).Order(PathComparer))
            ops.Add(new TxnOperation(TxnOpKind.Replace, rel, _fs.Exists(Live(rel))));
        foreach (var rel in _deletes.Where(d => !_staged.ContainsKey(d)).Order(PathComparer))
            ops.Add(new TxnOperation(TxnOpKind.Delete, rel));
        foreach (var rel in _staged.Keys.Where(p => InstellaOwnedPaths.IsOwned(p) && !PathComparer.Equals(p, manifest)).Order(PathComparer))
            ops.Add(new TxnOperation(TxnOpKind.Replace, rel, _fs.Exists(Live(rel))));
        if (_staged.ContainsKey(manifest))
            ops.Add(new TxnOperation(TxnOpKind.Replace, manifest, _fs.Exists(Live(manifest))));

        _ops = ops;
        _createdDirs = MissingDirectories(ops.Where(o => o.Op == TxnOpKind.Replace).Select(o => o.Path));
        await WriteJournalAsync(TxnState.Committing, ct);

        var renames = 0;
        var done = 0;
        foreach (var op in _ops)
        {
            var live = Live(op.Path);
            if (op.Op == TxnOpKind.Delete || op.HadOriginal)
            {
                if (_fs.Exists(live))
                {
                    await MoveWithRetryAsync(live, SafePath.Combine(_backupDir, op.Path), ct);
                    TestHooks.AfterCommitRename(++renames);
                }
            }

            if (op.Op == TxnOpKind.Replace)
            {
                await MoveWithRetryAsync(SafePath.Combine(_stageDir, op.Path), live, ct);
                TestHooks.AfterCommitRename(++renames);
            }
            onOperation?.Invoke(++done, _ops.Count);
        }

        await WriteJournalAsync(TxnState.Committed, ct);
    }

    /// <summary>
    /// Undoes whatever part of the commit happened, walking operations in reverse and acting
    /// only on what the file system shows. Safe to call in any state and to re-run.
    /// </summary>
    public async Task RollbackAsync()
    {
        if (_completed || State == TxnState.RolledBack) return;
        if (State is TxnState.Staging or TxnState.Staged)
        {
            await WriteJournalAsync(TxnState.RolledBack, CancellationToken.None);
            return;
        }
        await UndoOperationsAsync(_ops, _createdDirs);
        await WriteJournalAsync(TxnState.RolledBack, CancellationToken.None);
    }

    /// <summary>
    /// Deletes the transaction folder. A backup that cannot be deleted (typically a running
    /// executable that was renamed aside) is left for <see cref="RecoverAsync"/> to sweep.
    /// </summary>
    public async Task CompleteAsync()
    {
        if (_completed) return;
        _completed = true;
        await DeleteTxnDirectoryAsync(_fs, _txnDir, _log);
        await RemoveEmptyStateDirectoriesAsync(_fs, Root);
    }

    /// <summary>
    /// Brings <paramref name="installRoot"/> to a consistent state before anything else runs:
    /// interrupted commits are rolled back, finished or abandoned transactions are removed.
    /// </summary>
    /// <returns>True when an interrupted commit was rolled back.</returns>
    /// <exception cref="UnsupportedJournalException">A journal has an unknown format version;
    /// nothing was touched.</exception>
    public static async Task<bool> RecoverAsync(
        IFileSystem fs, string installRoot, CancellationToken ct, IInstellaLogger? log = null,
        IReadOnlyList<TimeSpan>? retryDelays = null)
    {
        var root = Path.GetFullPath(installRoot);
        var txnRoot = Path.Combine(root, TransactionJournal.TxnDirectory.Replace('/', Path.DirectorySeparatorChar));
        if (!fs.DirectoryExists(txnRoot)) return false;

        // Every journal is checked before any is acted on, so an unreadable one stops recovery
        // before it changes anything.
        var journals = new List<(string Dir, TransactionJournal? Journal)>();
        foreach (var dir in TransactionDirectories(fs, txnRoot))
        {
            var journalPath = Path.Combine(dir, TransactionJournal.FileName);
            switch (await ReadJournalAsync(fs, dir, ct))
            {
                case JournalRead.Missing:
                    // Crashed before the first journal write: nothing live was touched, unless
                    // something moved live files into backup/ without a journal.
                    var backup = Path.Combine(dir, "backup");
                    if (fs.DirectoryExists(backup) && fs.EnumerateFiles(backup, "*", recursive: true).Any())
                        throw new UnreadableJournalException(journalPath, "the journal is missing but backup/ holds files");
                    journals.Add((dir, null));
                    break;
                case JournalRead.Unreadable unreadable:
                    throw new UnreadableJournalException(journalPath, unreadable.Reason);
                case JournalRead.Ok { Journal: var candidate }:
                    if (candidate.JournalVersion != TransactionJournal.CurrentVersion)
                        throw new UnsupportedJournalException(journalPath, candidate.JournalVersion);
                    // Recovery acts on the folder it found; a journal naming another one is not trusted.
                    if (!string.Equals(candidate.TxnId, Path.GetFileName(dir), StringComparison.Ordinal))
                        throw new UnreadableJournalException(journalPath, $"it belongs to transaction '{candidate.TxnId}'");
                    journals.Add((dir, candidate));
                    break;
            }
        }

        var rolledBack = false;
        foreach (var (dir, journal) in journals)
        {
            ct.ThrowIfCancellationRequested();
            if (journal is { State: TxnState.Committing })
            {
                log?.Warn($"recover: transaction {journal.TxnId} ({journal.Kind} {journal.FromVersion} -> {journal.ToVersion}) was interrupted; rolling back");
                var txn = new InstallTransaction(fs, root, journal.TxnId, journal.Kind, journal.FromVersion, journal.ToVersion, log, retryDelays)
                {
                    State = TxnState.Committing,
                    _ops = journal.Operations,
                    _createdDirs = journal.CreatedDirectories,
                };
                await txn.RollbackAsync();
                rolledBack = true;
            }
            else if (journal is not null)
            {
                log?.Info($"recover: removing {journal.State} transaction {journal.TxnId}");
            }

            // Staging / Staged / RolledBack / Committed, a missing journal, or a rollback just
            // done: nothing live depends on this folder any more.
            await DeleteTxnDirectoryAsync(fs, dir, log);
        }
        await RemoveEmptyStateDirectoriesAsync(fs, root);
        return rolledBack;
    }

    private async Task UndoOperationsAsync(IReadOnlyList<TxnOperation> ops, IReadOnlyList<string> createdDirs)
    {
        var ct = CancellationToken.None;
        for (var i = ops.Count - 1; i >= 0; i--)
        {
            var op = ops[i];
            var live = Live(op.Path);
            var staged = SafePath.Combine(_stageDir, op.Path);
            var backup = SafePath.Combine(_backupDir, op.Path);

            if (op.Op == TxnOpKind.Replace && !_fs.Exists(staged) && _fs.Exists(live))
                await MoveWithRetryAsync(live, staged, ct);        // take the new file back out

            if (_fs.Exists(backup))
                await MoveWithRetryAsync(backup, live, ct);        // put the original back
        }

        foreach (var dir in createdDirs)
        {
            var full = SafePath.Combine(Root, dir);
            if (_fs.DirectoryExists(full) && !_fs.EnumerateFiles(full, "*", recursive: true).Any())
                await _fs.DeleteDirectoryAsync(full, recursive: false, ct);
        }
    }

    private async Task<string> StageCoreAsync(
        string rel, Func<Stream, CancellationToken, Task> write, string? expectedSha256, long? expectedSize,
        bool executable, CancellationToken ct)
    {
        EnsureState(TxnState.Staging, "stage files");
        var path = SafePath.Combine(_stageDir, rel);
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent))
            await Check(_fs.CreateDirectoryAsync(parent, ct), $"create staging folder for '{rel}'");

        var open = await _fs.OpenWriteAsync(path, ct);
        if (!open.Success || open.Value is null)
            throw new IOException($"cannot stage '{rel}': {open.Error?.Message ?? "unknown error"}", open.Error?.Exception);

        string actual;
        long size;
        await using (var target = open.Value)
        await using (var hashing = new HashingWriteStream(target, rel, maxBytes: expectedSize))
        {
            await write(hashing, ct);
            await hashing.FlushAsync(ct);
            // The staged bytes must be on the disk before the commit is journalled: after
            // Committed the backup is deleted, so a lazy write lost to a power cut would leave
            // zeros with no original to go back to. FlushFileBuffers needs this write handle.
            if (target is FileStream fileStream)
                fileStream.Flush(flushToDisk: true);
            actual = hashing.GetHash();
            size = hashing.Length;
        }
        StagedFileFlushed?.Invoke(rel);

        var mismatch =
            expectedSha256 is not null && !string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase) ? "hash mismatch"
            : expectedSize is { } s && s != size ? $"{size} bytes, expected {s}"
            : null;
        if (mismatch is not null)
        {
            await _fs.DeleteFileAsync(path, CancellationToken.None);
            _staged.Remove(rel);
            throw new UpdateTrustException($"'{rel}' does not match the signed release ({mismatch})");
        }

        if (executable)
            await Check(_fs.SetUnixFileModeAsync(path, ExecutableMode, ct), $"mark '{rel}' executable");

        _staged[rel] = actual;
        return actual;
    }

    /// <summary>
    /// Renames <paramref name="source"/> to <paramref name="dest"/>, retrying sharing
    /// violations with backoff. Works for a running .exe on Windows (rename, not overwrite).
    /// </summary>
    private async Task MoveWithRetryAsync(string source, string dest, CancellationToken ct)
    {
        var parent = Path.GetDirectoryName(dest);
        if (!string.IsNullOrEmpty(parent) && !_fs.DirectoryExists(parent))
            await Check(_fs.CreateDirectoryAsync(parent, ct), $"create '{parent}'");

        for (var attempt = 0; ; attempt++)
        {
            FileSystemResult result;
            try
            {
                result = await _fs.MoveFileAsync(source, dest, overwrite: true, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result = FileSystemResult.FromException(ex);
            }

            if (result.Success) return;
            var transient = result.Error?.Type is FileSystemErrorType.InUse or FileSystemErrorType.AccessDenied;
            if (!transient || attempt >= _retryDelays.Count)
                throw new IOException($"cannot move '{source}' to '{dest}': {result.Error?.Message ?? "unknown error"}", result.Error?.Exception);

            _log?.Warn($"txn: '{source}' is locked; retrying in {_retryDelays[attempt].TotalMilliseconds} ms");
            await Task.Delay(_retryDelays[attempt], ct);
        }
    }

    private async Task WriteJournalAsync(TxnState state, CancellationToken ct)
    {
        var journal = new TransactionJournal
        {
            JournalVersion = TransactionJournal.CurrentVersion,
            TxnId = TxnId,
            Kind = _kind,
            FromVersion = _fromVersion,
            ToVersion = _toVersion,
            State = state,
            Operations = _ops,
            CreatedDirectories = _createdDirs,
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(journal, TransactionJournalJsonContext.Default.TransactionJournal);
        var tmp = Path.Combine(_txnDir, "journal.tmp");

        var open = await _fs.OpenWriteAsync(tmp, ct);
        if (!open.Success || open.Value is null)
            throw new IOException($"cannot write the transaction journal: {open.Error?.Message ?? "unknown error"}", open.Error?.Exception);
        await using (var stream = open.Value)
        {
            await stream.WriteAsync(bytes, ct);
            if (stream is FileStream fileStream)
                fileStream.Flush(flushToDisk: true);
        }
        await MoveWithRetryAsync(tmp, Path.Combine(_txnDir, TransactionJournal.FileName), ct);
        State = state;
    }

    private IReadOnlyList<string> MissingDirectories(IEnumerable<string> relativePaths)
    {
        var missing = new HashSet<string>(PathComparer);
        foreach (var rel in relativePaths)
        {
            var dir = Path.GetDirectoryName(rel.Replace('/', Path.DirectorySeparatorChar));
            while (!string.IsNullOrEmpty(dir))
            {
                var canonical = dir.Replace(Path.DirectorySeparatorChar, '/');
                if (_fs.DirectoryExists(SafePath.Combine(Root, canonical)) || !missing.Add(canonical))
                    break;
                dir = Path.GetDirectoryName(dir);
            }
        }
        // Deepest first, so rollback removes children before parents.
        return missing.OrderByDescending(d => d.Count(c => c == '/')).ThenBy(d => d, PathComparer).ToList();
    }

    private string Live(string rel) => SafePath.Combine(Root, rel);

    private void EnsureState(TxnState expected, string action)
    {
        if (State != expected)
            throw new InvalidOperationException($"cannot {action} in transaction state {State}");
    }

    private static string Canonical(string relativePath)
    {
        if (!SafePath.TryNormalizeRelative(relativePath, out var normalized, out var reason))
            throw new UnsafePathException(relativePath, reason);
        return normalized;
    }

    private static string TxnDirectoryOf(string root, string txnId) =>
        Path.Combine(root, TransactionJournal.TxnDirectory.Replace('/', Path.DirectorySeparatorChar), txnId);

    private static IEnumerable<string> TransactionDirectories(IFileSystem fs, string txnRoot)
    {
        // IFileSystem enumerates files only; a transaction folder always holds its journal
        // or staged files, so its name is the first segment of any file below txnRoot.
        var prefix = txnRoot.Length + 1;
        return fs.EnumerateFiles(txnRoot, "*", recursive: true)
            .Select(f => f[prefix..].Split(Path.DirectorySeparatorChar)[0])
            .Where(n => n.Length > 0)
            .Distinct(PathComparer)
            .Select(n => Path.Combine(txnRoot, n))
            .ToList();
    }

    /// <summary>
    /// Reads a transaction folder's journal. Missing and unreadable are different answers:
    /// only a missing journal (with an empty backup) lets recovery delete the folder.
    /// </summary>
    private static async Task<JournalRead> ReadJournalAsync(IFileSystem fs, string dir, CancellationToken ct)
    {
        var path = Path.Combine(dir, TransactionJournal.FileName);
        if (!fs.Exists(path)) return new JournalRead.Missing();
        var read = await fs.ReadAllBytesAsync(path, ct);
        if (!read.Success || read.Value is null)
            return new JournalRead.Unreadable(read.Error?.Message ?? "it cannot be read");
        try
        {
            var journal = JsonSerializer.Deserialize(read.Value, TransactionJournalJsonContext.Default.TransactionJournal);
            if (journal is null) return new JournalRead.Unreadable("it is empty");
            if (journal.JournalVersion == 0) return new JournalRead.Unreadable("it has no journalVersion");
            return new JournalRead.Ok(journal);
        }
        catch (JsonException ex)
        {
            return new JournalRead.Unreadable($"it is not valid JSON ({ex.Message})");
        }
    }

    private static async Task DeleteTxnDirectoryAsync(IFileSystem fs, string dir, IInstellaLogger? log)
    {
        var result = await fs.DeleteDirectoryAsync(dir, recursive: true, CancellationToken.None);
        if (!result.Success)
            log?.Warn($"txn: could not remove '{dir}' ({result.Error?.Message}); it is swept on the next run");
    }

    /// <summary>Removes <c>.instella/txn</c> and <c>.instella</c> when nothing is left in them.</summary>
    private static async Task RemoveEmptyStateDirectoriesAsync(IFileSystem fs, string root)
    {
        var state = Path.Combine(root, InstellaOwnedPaths.StateDirectory);
        foreach (var dir in new[] { Path.Combine(state, "txn"), state })
        {
            if (fs.DirectoryExists(dir) && !fs.EnumerateFiles(dir, "*", recursive: true).Any())
                await fs.DeleteDirectoryAsync(dir, recursive: true, CancellationToken.None);
        }
    }

    private static async Task Check(Task<FileSystemResult> operation, string what)
    {
        var result = await operation;
        if (!result.Success)
            throw new IOException($"cannot {what}: {result.Error?.Message ?? "unknown error"}", result.Error?.Exception);
    }

    /// <summary><c>0755</c>.</summary>
    private const UnixFileMode ExecutableMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    /// <summary>Test seam: raised after each staged file has been flushed to disk.</summary>
    internal static Action<string>? StagedFileFlushed { get; set; }

    /// <summary>
    /// Forward-only write stream that SHA-256 hashes and counts everything written through it,
    /// and refuses to grow past <paramref name="maxBytes"/> (the signed size): a hostile server,
    /// or a patch that claims a huge output, can refuse service but never fill the disk.
    /// </summary>
    private sealed class HashingWriteStream(Stream inner, string rel, long? maxBytes) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private long _length;

        public string GetHash() => Convert.ToHexStringLower(_hash.GetHashAndReset());

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position { get => _length; set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        private void CheckLimit(int count)
        {
            if (maxBytes is { } max && _length + count > max)
                throw new UpdateTrustException($"'{rel}' is larger than the {max} bytes the signed release allows");
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            CheckLimit(buffer.Length);
            _hash.AppendData(buffer);
            inner.Write(buffer);
            _length += buffer.Length;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            CheckLimit(buffer.Length);
            _hash.AppendData(buffer.Span);
            await inner.WriteAsync(buffer, ct);
            _length += buffer.Length;
        }

        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _hash.Dispose();
            base.Dispose(disposing);
        }
    }
}
