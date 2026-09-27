using System.Collections.Concurrent;
using Instella.Core.Logging;

namespace Instella.Installer.Testing;

/// <summary>
/// <see cref="IInstellaLogSink"/> that captures every emitted
/// <see cref="InstellaLogEntry"/> for post-run inspection. Thread-safe —
/// tests may assert from a different thread than the one that produced the
/// log entry. No Logsmith symbols leak: the sink consumes and exposes only
/// types from <c>Instella.Core.Logging</c>.
/// </summary>
public sealed class RecordingSink : IInstellaLogSink
{
    private readonly ConcurrentQueue<InstellaLogEntry> _entries = new();

    /// <summary>
    /// Snapshot of every captured entry, in arrival order.
    /// </summary>
    public IReadOnlyList<InstellaLogEntry> Entries => _entries.ToArray();

    /// <summary>Clear the captured entries, typically between test steps.</summary>
    public void Clear()
    {
        while (_entries.TryDequeue(out _)) { }
    }

    /// <summary>
    /// Returns only the messages at or above <paramref name="minimum"/>. Useful
    /// when a test wants to ignore debug chatter and assert on <c>Warn</c>+.
    /// </summary>
    public IReadOnlyList<InstellaLogEntry> AtOrAbove(InstellaLogLevel minimum)
    {
        var snapshot = Entries;
        var filtered = new List<InstellaLogEntry>(snapshot.Count);
        foreach (var entry in snapshot)
            if (entry.Level >= minimum) filtered.Add(entry);
        return filtered;
    }

    /// <inheritdoc />
    public void Write(in InstellaLogEntry entry) => _entries.Enqueue(entry);

    /// <inheritdoc />
    public ValueTask FlushAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
