using System.Collections.Concurrent;
using Instella.Server.Data.Entities;

namespace Instella.Server.Services;

/// <summary>
/// Keeps a flood of identical security events from filling the log: after an event of
/// one (type, address, package) is written, repeats within <see cref="Window"/> are only counted,
/// and the next event written for that key says "(+N similar)". A singleton: the state spans
/// requests.
/// </summary>
public sealed class SecurityEventThrottle
{
    /// <summary>How long repeats of a written event are suppressed.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    private const int MaxKeys = 100_000;

    private readonly ConcurrentDictionary<(SecurityEventType Type, string Ip, string? Package), Entry> _entries = new();

    private sealed class Entry
    {
        public DateTime WrittenAt;
        public int Suppressed;
    }

    /// <summary>Test seam: the current time.</summary>
    internal Func<DateTime> UtcNow { get; init; } = () => DateTime.UtcNow;

    /// <summary>
    /// Whether to write this event now. When it is written after suppressed repeats,
    /// <paramref name="suppressed"/> is how many were not written.
    /// </summary>
    public bool ShouldWrite(SecurityEventType type, string ip, string? packageId, out int suppressed)
    {
        suppressed = 0;
        if (IsNeverThrottled(type))
            return true;
        var now = UtcNow();
        if (_entries.Count >= MaxKeys) Prune(now);
        var entry = _entries.GetOrAdd((type, ip, packageId), _ => new Entry { WrittenAt = DateTime.MinValue });
        lock (entry)
        {
            if (now - entry.WrittenAt < Window)
            {
                entry.Suppressed++;
                suppressed = 0;
                return false;
            }
            suppressed = entry.Suppressed;
            entry.Suppressed = 0;
            entry.WrittenAt = now;
            return true;
        }
    }

    /// <summary>
    /// Events that record a release decision or a trust change. Each is written in full: approving
    /// three platforms within a minute must leave three entries, not one "(+2 similar)".
    /// </summary>
    public static bool IsNeverThrottled(SecurityEventType type) => type is
        SecurityEventType.ReleasePending or SecurityEventType.ReleaseApproved or SecurityEventType.ReleaseRejected
        or SecurityEventType.ReleaseAutoPublished or SecurityEventType.ReleaseAutoPublishBlocked
        or SecurityEventType.ReleaseApprovalChanged or SecurityEventType.PublisherKeyAdded
        or SecurityEventType.PublisherKeyRemoved or SecurityEventType.DraftSigned;

    /// <summary>Drops keys idle for longer than the window.</summary>
    private void Prune(DateTime now)
    {
        foreach (var (key, entry) in _entries)
        {
            lock (entry)
            {
                if (now - entry.WrittenAt >= Window && entry.Suppressed == 0)
                    _entries.TryRemove(key, out _);
            }
        }
    }
}
