using Instella.Server.Data;
using Instella.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Instella.Server.Services;

public interface IIpBanService
{
    Task<List<IpBan>> GetAllBansAsync(CancellationToken ct = default);

    Task<IpBan> AddBanAsync(
        string ipAddress,
        string reason,
        long adminUserId,
        CancellationToken ct = default);

    Task<bool> RemoveBanAsync(long banId, long adminUserId, CancellationToken ct = default);

    Task<bool> IsBannedAsync(string ipAddress, CancellationToken ct = default);

    Task<IpBan?> GetBanAsync(string ipAddress, CancellationToken ct = default);
}

public class IpBanService(AppDbContext db, ISecurityLogService securityLog, IpBanCache cache) : IIpBanService
{
    public async Task<List<IpBan>> GetAllBansAsync(CancellationToken ct = default)
    {
        return await db.IpBans
            .Include(b => b.CreatedByAdmin)
            .OrderByDescending(b => b.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<IpBan> AddBanAsync(
        string ipAddress,
        string reason,
        long adminUserId,
        CancellationToken ct = default)
    {
        // Stored in the one spelling requests are matched with: "::ffff:198.51.100.7" is 198.51.100.7.
        if (!Instella.Server.Extensions.IpAddresses.TryNormalize(ipAddress, out var normalized))
            throw new ArgumentException($"'{ipAddress}' is not an IP address", nameof(ipAddress));
        ipAddress = normalized;

        var ban = new IpBan
        {
            IpAddress = ipAddress,
            Reason = reason,
            CreatedByAdminId = adminUserId,
            CreatedAt = DateTime.UtcNow
        };

        db.IpBans.Add(ban);
        await db.SaveChangesAsync(ct);
        cache.Invalidate();

        await securityLog.LogEventAsync(
            SecurityEventType.IpBanned,
            ipAddress,
            details: $"Banned by admin ID {adminUserId}: {reason}",
            ct: ct);

        return ban;
    }

    public async Task<bool> RemoveBanAsync(long banId, long adminUserId, CancellationToken ct = default)
    {
        var ban = await db.IpBans.FindAsync([banId], ct);
        if (ban == null) return false;

        var ipAddress = ban.IpAddress;
        db.IpBans.Remove(ban);
        await db.SaveChangesAsync(ct);
        cache.Invalidate();

        await securityLog.LogEventAsync(
            SecurityEventType.IpUnbanned,
            ipAddress,
            details: $"Unbanned by admin ID {adminUserId}",
            ct: ct);

        return true;
    }

    public async Task<bool> IsBannedAsync(string ipAddress, CancellationToken ct = default)
    {
        return await db.IpBans.AnyAsync(b => b.IpAddress == ipAddress, ct);
    }

    public async Task<IpBan?> GetBanAsync(string ipAddress, CancellationToken ct = default)
    {
        return await db.IpBans.FirstOrDefaultAsync(b => b.IpAddress == ipAddress, ct);
    }
}
