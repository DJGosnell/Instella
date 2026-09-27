using Instella.Core.Wire;
using Instella.Server.Data;
using Instella.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Instella.Server.Services;

/// <summary>
/// Download tokens: created and revoked in the admin UI, checked on every download of a
/// <see cref="DownloadAccessMode.PackageKeyRequired"/> package. Lookups are cached for 60 s by
/// token hash (a busy app base checks for updates constantly); a revoke drops the cache entry.
/// </summary>
public sealed class DownloadTokenService(AppDbContext db, IMemoryCache cache)
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(60);

    /// <summary>How stale <see cref="DownloadToken.LastUsedAt"/> may get before it is written again.</summary>
    internal static readonly TimeSpan LastUsedResolution = TimeSpan.FromHours(1);

    /// <summary>What a cached lookup remembers of a token.</summary>
    private sealed class Entry(long id, long packageId, bool isRevoked, DateTime? expiresAt, DateTime? lastUsedAt)
    {
        public long Id { get; } = id;
        public long PackageId { get; } = packageId;
        public bool IsRevoked { get; } = isRevoked;
        public DateTime? ExpiresAt { get; } = expiresAt;
        public DateTime? LastUsedAt { get; set; } = lastUsedAt;
    }

    /// <summary>Test seam: the current time.</summary>
    internal Func<DateTime> UtcNow { get; init; } = () => DateTime.UtcNow;

    /// <summary>
    /// Creates a token for <paramref name="packageDbId"/>. The plain token is returned once and
    /// never stored; only its hash is. Recorded as a <see cref="SecurityEventType.DownloadTokenCreated"/> event.
    /// </summary>
    public async Task<(DownloadToken Token, string Plain)> CreateAsync(
        long packageDbId, string name, DateTime? expiresAt, string? actor = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > 100) throw new ArgumentException("A token name has at most 100 characters", nameof(name));
        var package = await db.Packages.FirstOrDefaultAsync(p => p.Id == packageDbId, ct)
            ?? throw new InvalidOperationException("Package not found");

        var plain = DownloadTokens.NewToken();
        var token = new DownloadToken
        {
            PackageId = packageDbId,
            Name = name.Trim(),
            TokenHash = DownloadTokens.Hash(plain),
            DisplayPrefix = DownloadTokens.DisplayPrefix(plain),
            CreatedAt = UtcNow(),
            ExpiresAt = expiresAt,
        };
        db.DownloadTokens.Add(token);
        db.SecurityEvents.Add(new SecurityEvent
        {
            EventType = SecurityEventType.DownloadTokenCreated,
            IpAddress = "admin UI",
            Username = actor,
            PackageId = package.PackageId,
            Details = $"'{token.Name}' ({token.DisplayPrefix}…), expires {(expiresAt is { } e ? e.ToString("u") : "never")}",
        });
        await db.SaveChangesAsync(ct);
        return (token, plain);
    }

    /// <summary>The package's tokens, newest first.</summary>
    public Task<List<DownloadToken>> ListAsync(long packageDbId, CancellationToken ct = default) =>
        db.DownloadTokens.AsNoTracking()
            .Where(t => t.PackageId == packageDbId)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync(ct);

    /// <summary>Revokes a token at once (the cache entry is dropped). False when there is no such token.</summary>
    public async Task<bool> RevokeAsync(long tokenId, string? actor = null, CancellationToken ct = default)
    {
        var token = await db.DownloadTokens.Include(t => t.Package).FirstOrDefaultAsync(t => t.Id == tokenId, ct);
        if (token is null) return false;
        token.IsRevoked = true;
        db.SecurityEvents.Add(new SecurityEvent
        {
            EventType = SecurityEventType.DownloadTokenRevoked,
            IpAddress = "admin UI",
            Username = actor,
            PackageId = token.Package.PackageId,
            Details = $"'{token.Name}' ({token.DisplayPrefix}…)",
        });
        await db.SaveChangesAsync(ct);
        cache.Remove(CacheKey(token.TokenHash));
        return true;
    }

    /// <summary>
    /// Whether <paramref name="plainToken"/> is a valid token for <paramref name="packageDbId"/>:
    /// known, not revoked, not expired, and issued for that package. A valid use updates
    /// <see cref="DownloadToken.LastUsedAt"/> when it is more than an hour old.
    /// </summary>
    public async Task<bool> IsValidForAsync(string plainToken, long packageDbId, CancellationToken ct = default)
    {
        if (!DownloadTokens.LooksLikeToken(plainToken)) return false;
        var hash = DownloadTokens.Hash(plainToken);
        var entry = await cache.GetOrCreateAsync(CacheKey(hash), async e =>
        {
            e.AbsoluteExpirationRelativeToNow = CacheLifetime;
            var row = await db.DownloadTokens.AsNoTracking()
                .Where(t => t.TokenHash == hash)
                .Select(t => new { t.Id, t.PackageId, t.IsRevoked, t.ExpiresAt, t.LastUsedAt })
                .FirstOrDefaultAsync(ct);
            return row is null ? null : new Entry(row.Id, row.PackageId, row.IsRevoked, row.ExpiresAt, row.LastUsedAt);
        });

        var now = UtcNow();
        var valid = entry is not null && !entry.IsRevoked && (entry.ExpiresAt is null || entry.ExpiresAt > now)
                    && entry.PackageId == packageDbId;
        if (valid && (entry!.LastUsedAt is null || now - entry.LastUsedAt > LastUsedResolution))
        {
            entry.LastUsedAt = now;
            await db.DownloadTokens.Where(t => t.Id == entry.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(t => t.LastUsedAt, now), ct);
        }
        return valid;
    }

    private static string CacheKey(string hash) => "download-token:" + hash;
}
