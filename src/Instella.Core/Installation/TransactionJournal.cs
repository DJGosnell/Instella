using System.Text.Json;
using System.Text.Json.Serialization;

namespace Instella.Core.Installation;

/// <summary>States of an install transaction, in the order they are reached.</summary>
internal enum TxnState
{
    /// <summary>New files are being written to <c>stage/</c>; nothing live has been touched.</summary>
    Staging,

    /// <summary>Every staged file has been verified; nothing live has been touched.</summary>
    Staged,

    /// <summary>Live files are being swapped. A crash here needs recovery (rollback).</summary>
    Committing,

    /// <summary>Every operation completed; only clean-up of the transaction folder is left.</summary>
    Committed,

    /// <summary>Every operation that had happened was undone.</summary>
    RolledBack,
}

/// <summary>What caused a transaction.</summary>
internal enum TxnKind
{
    FirstInstall,
    Upgrade,
    Repair,
    Update,
}

/// <summary>Kinds of transaction operation.</summary>
internal enum TxnOpKind
{
    /// <summary>Put a staged file at the path, whether or not a live file exists there.</summary>
    Replace,

    /// <summary>Remove a live file the new version no longer contains.</summary>
    Delete,
}

/// <summary>One journal operation. <see cref="HadOriginal"/> is fixed when commit starts.</summary>
internal sealed record TxnOperation(TxnOpKind Op, string Path, bool HadOriginal = false);

/// <summary>
/// The on-disk journal of an install transaction (<c>.instella/txn/{id}/journal.json</c>).
/// It is written only at state transitions; per-operation progress is inferred from which
/// of the live, stage and backup files exist, which makes rollback idempotent.
/// </summary>
internal sealed record TransactionJournal
{
    /// <summary>The journal format this build reads and writes.</summary>
    public const int CurrentVersion = 1;

    /// <summary>
    /// Journal format version. A journal of any other version is never interpreted: acting on
    /// a misread journal could destroy the installation it is meant to protect. No initializer:
    /// an absent field reads as 0 and is refused; the writer always sets it.
    /// </summary>
    public int JournalVersion { get; init; }

    public required string TxnId { get; init; }
    public required TxnKind Kind { get; init; }
    public Version? FromVersion { get; init; }
    public required Version ToVersion { get; init; }
    public required TxnState State { get; init; }
    public IReadOnlyList<TxnOperation> Operations { get; init; } = [];

    /// <summary>Directories commit created for new files (deepest first); rollback removes them when empty.</summary>
    public IReadOnlyList<string> CreatedDirectories { get; init; } = [];

    /// <summary>Folder (under the install root) that holds every transaction.</summary>
    public const string TxnDirectory = InstellaOwnedPaths.StateDirectory + "/txn";

    /// <summary>File name of the journal inside a transaction folder.</summary>
    public const string FileName = "journal.json";

    /// <summary>
    /// True when <paramref name="installRoot"/> holds a transaction that was interrupted
    /// while live files were being swapped. Reads the real file system; used by the SDK,
    /// which cannot repair the tree itself because its own files may be the ones in flux.
    /// </summary>
    public static bool HasInterruptedCommit(string installRoot)
    {
        var txnRoot = Path.Combine(installRoot, TxnDirectory.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(txnRoot)) return false;
        foreach (var dir in Directory.EnumerateDirectories(txnRoot))
        {
            var path = Path.Combine(dir, FileName);
            if (!File.Exists(path)) continue;   // no journal: nothing live was touched under it
            try
            {
                var bytes = File.ReadAllBytes(path);
                var journal = JsonSerializer.Deserialize(bytes, TransactionJournalJsonContext.Default.TransactionJournal);
                // A journal from another version, or one without a version, may describe a
                // commit in flight: assume it does.
                if (journal is null || journal.JournalVersion != CurrentVersion || journal.State == TxnState.Committing)
                    return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                // Unreadable: recovery refuses with a clear message, instead of the app believing
                // everything is fine.
                return true;
            }
        }
        return false;
    }
}

/// <summary>
/// A transaction journal written by a different Instella version. Recovery refuses to touch
/// the installation (exit 23); the Instella version that wrote it must recover it.
/// </summary>
internal class UnsupportedJournalException : Exception
{
    public UnsupportedJournalException(string journalPath, int version)
        : base($"The interrupted-update journal '{journalPath}' has format version {version}, which this " +
               $"version of Instella (journal version {TransactionJournal.CurrentVersion}) cannot read. The " +
               "installation was left untouched; run the installer of the version that started the update with --recover.")
    {
        Version = version;
    }

    protected UnsupportedJournalException(string message) : base(message) { }

    public int Version { get; }
}

/// <summary>
/// A transaction folder recovery cannot act on safely: its journal is unreadable, has no
/// version, belongs to another folder, or is missing while <c>backup/</c> holds files. Nothing
/// is touched (exit 23): for an interrupted commit, <c>backup/</c> holds the only copy of the
/// original files.
/// </summary>
internal sealed class UnreadableJournalException(string journalPath, string reason)
    : UnsupportedJournalException(
        $"The installer found an unfinished update it cannot read ('{journalPath}': {reason}). Nothing was " +
        "changed. Restore the folder from a backup or reinstall.");

/// <summary>What <c>InstallTransaction</c> found in a transaction folder.</summary>
internal abstract record JournalRead
{
    /// <summary>No journal file.</summary>
    public sealed record Missing : JournalRead;

    /// <summary>A journal file that cannot be used.</summary>
    public sealed record Unreadable(string Reason) : JournalRead;

    /// <summary>A parsed journal (its version is checked separately).</summary>
    public sealed record Ok(TransactionJournal Journal) : JournalRead;
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(TransactionJournal))]
internal partial class TransactionJournalJsonContext : JsonSerializerContext;
