using Instella.Server.Data;
using Instella.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Instella.Server.Services;

public interface ISecurityLogService
{
    Task LogEventAsync(
        SecurityEventType eventType,
        string ipAddress,
        string? username = null,
        string? apiKeyName = null,
        string? packageId = null,
        string? details = null,
        CancellationToken ct = default);

    Task<List<SecurityEvent>> GetRecentEventsAsync(
        int count = 100,
        SecurityEventType? filterType = null,
        CancellationToken ct = default);

    Task<(List<SecurityEvent> Events, int TotalCount)> GetEventsPagedAsync(
        int page,
        int pageSize,
        SecurityEventType? filterType = null,
        CancellationToken ct = default);

    Task<int> GetEventCountAsync(
        SecurityEventType eventType,
        DateTime since,
        CancellationToken ct = default);
}

/// <param name="db">Database.</param>
/// <param name="throttle">Suppresses repeats; the server registers one singleton. Null: a
/// private one, so a directly constructed service (tests) behaves the same within itself.</param>
public class SecurityLogService(AppDbContext db, SecurityEventThrottle? throttle = null) : ISecurityLogService
{
    private readonly SecurityEventThrottle _throttle = throttle ?? new SecurityEventThrottle();

    public async Task LogEventAsync(
        SecurityEventType eventType,
        string ipAddress,
        string? username = null,
        string? apiKeyName = null,
        string? packageId = null,
        string? details = null,
        CancellationToken ct = default)
    {
        // A repeat of the same (type, address, package) within a minute is counted, not written.
        if (!_throttle.ShouldWrite(eventType, ipAddress, packageId, out var suppressed))
            return;
        if (suppressed > 0)
            details = string.IsNullOrEmpty(details) ? $"(+{suppressed} similar)" : $"{details} (+{suppressed} similar)";

        var securityEvent = new SecurityEvent
        {
            EventType = eventType,
            IpAddress = ipAddress,
            Username = username,
            ApiKeyName = apiKeyName,
            PackageId = packageId,
            Details = details,
            Timestamp = DateTime.UtcNow
        };

        db.SecurityEvents.Add(securityEvent);
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<SecurityEvent>> GetRecentEventsAsync(
        int count = 100,
        SecurityEventType? filterType = null,
        CancellationToken ct = default)
    {
        var query = db.SecurityEvents.AsQueryable();

        if (filterType.HasValue)
            query = query.Where(e => e.EventType == filterType.Value);

        return await query
            .OrderByDescending(e => e.Timestamp)
            .Take(count)
            .ToListAsync(ct);
    }

    public async Task<(List<SecurityEvent> Events, int TotalCount)> GetEventsPagedAsync(
        int page,
        int pageSize,
        SecurityEventType? filterType = null,
        CancellationToken ct = default)
    {
        var query = db.SecurityEvents.AsQueryable();

        if (filterType.HasValue)
            query = query.Where(e => e.EventType == filterType.Value);

        var totalCount = await query.CountAsync(ct);

        var events = await query
            .OrderByDescending(e => e.Timestamp)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return (events, totalCount);
    }

    public async Task<int> GetEventCountAsync(
        SecurityEventType eventType,
        DateTime since,
        CancellationToken ct = default)
    {
        return await db.SecurityEvents
            .CountAsync(e => e.EventType == eventType && e.Timestamp >= since, ct);
    }
}
