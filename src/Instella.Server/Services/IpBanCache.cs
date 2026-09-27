using Instella.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Instella.Server.Services;

/// <summary>
/// The set of permanently banned addresses, read by the ban middleware on every request. It is
/// reloaded from the database when older than <see cref="MaxAge"/> or after
/// <see cref="Invalidate"/> (which <see cref="IpBanService"/> calls on every change), so a
/// request costs one set lookup rather than a query.
/// </summary>
public sealed class IpBanCache(IServiceScopeFactory scopeFactory)
{
    /// <summary>How long a loaded set is trusted; bounds the delay for bans written outside <see cref="IpBanService"/>.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(15);

    private readonly SemaphoreSlim _reload = new(1, 1);
    private volatile Snapshot? _snapshot;

    /// <summary>True when <paramref name="ipAddress"/> is permanently banned.</summary>
    public async ValueTask<bool> IsBannedAsync(string ipAddress, CancellationToken ct)
    {
        var snapshot = _snapshot;
        if (snapshot is null || DateTime.UtcNow - snapshot.LoadedAt > MaxAge)
            snapshot = await ReloadAsync(ct);
        return snapshot.Addresses.Contains(ipAddress);
    }

    /// <summary>Forces a reload on the next lookup.</summary>
    public void Invalidate() => _snapshot = null;

    private async Task<Snapshot> ReloadAsync(CancellationToken ct)
    {
        await _reload.WaitAsync(ct);
        try
        {
            if (_snapshot is { } fresh && DateTime.UtcNow - fresh.LoadedAt <= MaxAge)
                return fresh;
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var addresses = await db.IpBans.Select(b => b.IpAddress).ToListAsync(ct);
            // Normalised like request addresses, so a row stored in another spelling still matches.
            var normalized = addresses.Select(a => Instella.Server.Extensions.IpAddresses.TryNormalize(a, out var n) ? n : a);
            var loaded = new Snapshot(new HashSet<string>(normalized, StringComparer.OrdinalIgnoreCase), DateTime.UtcNow);
            _snapshot = loaded;
            return loaded;
        }
        finally
        {
            _reload.Release();
        }
    }

    private sealed record Snapshot(HashSet<string> Addresses, DateTime LoadedAt);
}
