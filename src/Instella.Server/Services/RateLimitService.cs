using System.Collections.Concurrent;
using Instella.Server.Data;
using Instella.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Instella.Server.Services;

public static class RateLimitConfig
{
    public const int InitialDelaySeconds = 1;
    public const int MaxDelaySeconds = 900;        // 15 minutes
    public const int ResetWindowSeconds = 900;     // 15 minutes
    public const double BackoffMultiplier = 2.0;

    /// <summary>Failures from one address, across any usernames, before <c>login-ip:{ip}</c> blocks it.</summary>
    public const int LoginFailuresPerIp = 20;

    /// <summary>Most keys kept; beyond it the entry whose reset is oldest is evicted.</summary>
    public const int MaxKeys = 100_000;
}

public class TemporaryBlock
{
    public string Key { get; set; } = string.Empty;      // "login:user@ip" or "upload:keyhash@ip"
    public int FailureCount { get; set; }
    public DateTime LastFailure { get; set; }
    public DateTime BlockedUntil { get; set; }
    public int CurrentDelaySeconds { get; set; }
}

public interface IRateLimitService
{
    /// <summary>
    /// Check if request is allowed. Returns (allowed, delaySeconds).
    /// If not allowed, delaySeconds indicates how long until retry.
    /// </summary>
    (bool Allowed, int DelaySeconds) CheckRequest(string key);

    /// <summary>
    /// Record a failed attempt, incrementing the backoff counter. The first
    /// <paramref name="freeFailures"/> failures count without blocking; the backoff starts after them.
    /// </summary>
    void RecordFailure(string key, int freeFailures = 0);

    /// <summary>
    /// Record a successful attempt, resetting the backoff counter.
    /// </summary>
    void RecordSuccess(string key);

    /// <summary>
    /// Get all current temporary blocks (for admin UI).
    /// </summary>
    IReadOnlyList<TemporaryBlock> GetTemporaryBlocks();

    /// <summary>
    /// Clear all temporary blocks (testing/installation).
    /// </summary>
    void ClearAllTemporaryBlocks();

    /// <summary>
    /// Clear temporary block for specific key.
    /// </summary>
    void ClearTemporaryBlock(string key);
}

public class RateLimitService : IRateLimitService
{
    private readonly ConcurrentDictionary<string, TemporaryBlock> _blocks = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private long _lastCleanupTicks;

    /// <summary>Test seam: the current time.</summary>
    internal Func<DateTime> UtcNow { get; init; } = () => DateTime.UtcNow;

    public RateLimitService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public (bool Allowed, int DelaySeconds) CheckRequest(string key)
    {
        CleanupExpiredBlocks();

        if (!_blocks.TryGetValue(key, out var block))
        {
            return (true, 0);
        }

        var now = UtcNow();

        // Check if block has expired (reset window passed since last failure)
        if ((now - block.LastFailure).TotalSeconds > RateLimitConfig.ResetWindowSeconds)
        {
            _blocks.TryRemove(key, out _);
            return (true, 0);
        }

        // Check if still in backoff period
        if (now < block.BlockedUntil)
        {
            var remainingSeconds = (int)Math.Ceiling((block.BlockedUntil - now).TotalSeconds);
            return (false, remainingSeconds);
        }

        return (true, 0);
    }

    public void RecordFailure(string key, int freeFailures = 0)
    {
        var now = UtcNow();
        if (!_blocks.ContainsKey(key) && _blocks.Count >= RateLimitConfig.MaxKeys)
            EvictOldest();

        // The n-th failure after the free ones blocks for Initial * 2^(n-1) seconds, capped.
        TemporaryBlock Next(int failures)
        {
            var counted = failures - freeFailures;
            if (counted <= 0)
                return new TemporaryBlock { Key = key, FailureCount = failures, LastFailure = now, BlockedUntil = now, CurrentDelaySeconds = 0 };
            var delay = counted == 1
                ? RateLimitConfig.InitialDelaySeconds
                : (int)Math.Min(RateLimitConfig.InitialDelaySeconds * Math.Pow(RateLimitConfig.BackoffMultiplier, counted - 1),
                    RateLimitConfig.MaxDelaySeconds);
            return new TemporaryBlock
            {
                Key = key, FailureCount = failures, LastFailure = now, BlockedUntil = now.AddSeconds(delay), CurrentDelaySeconds = delay,
            };
        }

        _blocks.AddOrUpdate(
            key,
            _ => Next(1),
            // A reset window without failures starts afresh.
            (_, existing) => Next((now - existing.LastFailure).TotalSeconds > RateLimitConfig.ResetWindowSeconds
                ? 1
                : existing.FailureCount + 1));
    }

    /// <summary>At the key cap: drops the entry whose reset time is oldest.</summary>
    private void EvictOldest()
    {
        var oldest = _blocks.Values.MinBy(b => b.LastFailure);
        if (oldest is not null) _blocks.TryRemove(oldest.Key, out _);
    }

    public void RecordSuccess(string key)
    {
        _blocks.TryRemove(key, out _);
    }

    public IReadOnlyList<TemporaryBlock> GetTemporaryBlocks()
    {
        CleanupExpiredBlocks();
        return _blocks.Values.ToList();
    }

    public void ClearAllTemporaryBlocks()
    {
        _blocks.Clear();
    }

    public void ClearTemporaryBlock(string key)
    {
        _blocks.TryRemove(key, out _);
    }

    /// <summary>Drops expired entries, at most once a minute: not a full scan on every check.</summary>
    private void CleanupExpiredBlocks()
    {
        var now = UtcNow();
        var last = Interlocked.Read(ref _lastCleanupTicks);
        if (now.Ticks - last < TimeSpan.TicksPerMinute
            || Interlocked.CompareExchange(ref _lastCleanupTicks, now.Ticks, last) != last)
            return;
        var expiredKeys = _blocks
            .Where(kvp => (now - kvp.Value.LastFailure).TotalSeconds > RateLimitConfig.ResetWindowSeconds)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in expiredKeys)
        {
            _blocks.TryRemove(key, out _);
        }
    }
}
