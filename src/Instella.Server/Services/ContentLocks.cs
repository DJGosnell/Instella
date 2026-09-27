using System.Collections.Concurrent;

namespace Instella.Server.Services;

/// <summary>
/// Per-content-hash locks. An upload holds a hash's lock from its dedup lookup to its
/// session-row insert; a purge holds the locks of the hashes it touches from before its
/// transaction until its blobs are deleted; the sweeper locks each batch. So no upload can
/// recreate a row between a purge's commit and its blob delete, and no purge can delete a row
/// an upload is about to reference.
/// </summary>
/// <remarks>
/// In-process locks are enough because the server is one process per database (SQLite;
/// docs/server-deployment.md). Take them before starting a database transaction: SQLite
/// transactions take the write lock at once, so waiting for a hash inside one could deadlock
/// with an upload that holds the hash and waits to write.
/// </remarks>
public sealed class ContentLocks
{
    /// <summary>The server's locks: one process, one set.</summary>
    public static ContentLocks Shared { get; } = new();

    private readonly ConcurrentDictionary<string, Entry> _locks = new(StringComparer.Ordinal);

    private sealed class Entry
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int Users;   // holders and waiters; guarded by the dictionary entry's lock
    }

    /// <summary>Number of hashes with a holder or waiter (tests).</summary>
    internal int ActiveCount => _locks.Count;

    /// <summary>
    /// Takes the locks of <paramref name="hashes"/> (distinct, in ordinal order, so two
    /// multi-hash holders never deadlock). Dispose the result to release them.
    /// </summary>
    public async Task<IAsyncDisposable> AcquireAsync(IEnumerable<string> hashes, CancellationToken ct = default)
    {
        var ordered = hashes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var held = new List<(string Hash, Entry Entry)>(ordered.Length);
        try
        {
            foreach (var hash in ordered)
            {
                var entry = Join(hash);
                try
                {
                    await entry.Semaphore.WaitAsync(ct);
                }
                catch
                {
                    Leave(hash, entry);
                    throw;
                }
                held.Add((hash, entry));
            }
        }
        catch
        {
            Release(held);
            throw;
        }
        return new Handle(this, held);
    }

    /// <summary>The lock of one hash.</summary>
    public Task<IAsyncDisposable> AcquireAsync(string hash, CancellationToken ct = default) => AcquireAsync([hash], ct);

    private Entry Join(string hash)
    {
        while (true)
        {
            var entry = _locks.GetOrAdd(hash, _ => new Entry());
            lock (entry)
            {
                // An entry being removed has Users == -1; take a fresh one.
                if (entry.Users < 0) continue;
                entry.Users++;
                return entry;
            }
        }
    }

    private void Leave(string hash, Entry entry)
    {
        lock (entry)
        {
            if (--entry.Users > 0) return;
            entry.Users = -1;
            _locks.TryRemove(new KeyValuePair<string, Entry>(hash, entry));
        }
    }

    private void Release(List<(string Hash, Entry Entry)> held)
    {
        for (var i = held.Count - 1; i >= 0; i--)
        {
            held[i].Entry.Semaphore.Release();
            Leave(held[i].Hash, held[i].Entry);
        }
        held.Clear();
    }

    private sealed class Handle(ContentLocks owner, List<(string Hash, Entry Entry)> held) : IAsyncDisposable
    {
        private int _released;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) owner.Release(held);
            return ValueTask.CompletedTask;
        }
    }
}
