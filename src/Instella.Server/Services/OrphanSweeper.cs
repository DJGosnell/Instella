using Instella.Server.Data;
using Instella.Server.Storage;
using Microsoft.EntityFrameworkCore;

namespace Instella.Server.Services;

/// <summary>
/// Hourly clean-up: expired upload sessions, content no build references (never
/// completed, or whose versions were deleted), and blobs in storage with no database row — which
/// also reconciles post-commit blob deletes that failed during version deletion.
/// </summary>
public sealed partial class OrphanSweeper(IServiceScopeFactory scopeFactory, ILogger<OrphanSweeper> logger) : BackgroundService
{
    /// <summary>
    /// Keys in Instella's layout: content <c>ab/cd/{sha256}</c>, patches
    /// <c>patches/…/patch.zip</c>, and the <c>.{guid}.tmp</c> sibling of either that an interrupted
    /// upload leaves. The sweeper deletes nothing else, whatever shares the folder or bucket.
    /// </summary>
    [System.Text.RegularExpressions.GeneratedRegex(
        @"^(?:[0-9a-f]{2}/[0-9a-f]{2}/[0-9a-f]{64}|patches/.+/patch\.zip)(?:\.[0-9a-f]{32}\.tmp)?$")]
    private static partial System.Text.RegularExpressions.Regex InstellaKey();

    /// <summary>Whether <paramref name="key"/> is in Instella's storage layout.</summary>
    internal static bool IsInstellaKey(string key) => InstellaKey().IsMatch(key);

    /// <summary>Content pending (or unreferenced) for longer than this is removed.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromHours(24);

    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await SweepOnceAsync(
                    db,
                    scope.ServiceProvider.GetRequiredService<IStorageProvider>(),
                    DateTime.UtcNow, logger, stoppingToken);
                var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
                await ApplyRetentionAsync(db, DateTime.UtcNow,
                    configuration.GetValue("Retention:SecurityEventDays", 90),
                    configuration.GetValue("Retention:DownloadLogDays", 365), logger, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Orphan sweep failed");
            }

            await Task.Delay(Interval, stoppingToken);
        }
    }

    /// <summary>One sweep; returns how many content blobs were removed.</summary>
    /// <param name="db">Database.</param>
    /// <param name="storage">Blob storage.</param>
    /// <param name="now">The current time.</param>
    /// <param name="logger">Log.</param>
    /// <param name="ct">Cancellation.</param>
    /// <param name="afterCandidateQuery">Test hook: runs after the orphan candidates are read.</param>
    public static async Task<int> SweepOnceAsync(
        AppDbContext db, IStorageProvider storage, DateTime now, ILogger logger, CancellationToken ct,
        Func<Task>? afterCandidateQuery = null)
    {
        var sessions = await db.UploadSessions.Where(s => s.ExpiresAt < now).ExecuteDeleteAsync(ct);

        var cutoff = now - Grace;
        var candidates = await db.StoredFiles
            .Where(f => f.ReferenceCount <= 0
                        && (f.PendingSince == null || f.PendingSince < cutoff)
                        && !db.UploadSessionFiles.Any(u => u.ContentHash == f.ContentHash)
                        && !db.UploadSessionInstallers.Any(u => u.ContentHash == f.ContentHash))
            .Select(f => new { f.ContentHash, f.StoragePath })
            .ToListAsync(ct);
        if (afterCandidateQuery is not null) await afterCandidateQuery();

        var removed = 0;
        foreach (var batch in candidates.Chunk(500))
        {
            // Under the content locks, repeat every predicate: a session that started (or a
            // build that took a reference) after the query keeps the row, and its blob.
            var hashes = batch.Select(o => o.ContentHash).ToList();
            await using (await ContentLocks.Shared.AcquireAsync(hashes, ct))
            {
                await db.StoredFiles
                    .Where(f => hashes.Contains(f.ContentHash) && f.ReferenceCount <= 0
                                && (f.PendingSince == null || f.PendingSince < cutoff)
                                && !db.UploadSessionFiles.Any(u => u.ContentHash == f.ContentHash)
                                && !db.UploadSessionInstallers.Any(u => u.ContentHash == f.ContentHash))
                    .ExecuteDeleteAsync(ct);
                var kept = new HashSet<string>(
                    await db.StoredFiles.Where(f => hashes.Contains(f.ContentHash)).Select(f => f.ContentHash).ToListAsync(ct),
                    StringComparer.Ordinal);
                foreach (var orphan in batch.Where(o => !kept.Contains(o.ContentHash)))
                    if (await TryDeleteAsync(storage, orphan.StoragePath, logger, ct)) removed++;
            }
        }

        // Blobs with no row at all (a delete that failed after commit, a crash mid-upload), but only
        // in Instella's layout: anything else in the folder or bucket is not ours to delete.
        var known = new HashSet<string>(await db.StoredFiles.Select(f => f.StoragePath).ToListAsync(ct), StringComparer.Ordinal);
        known.UnionWith(await db.BuildPatches.Select(p => p.StoragePath).ToListAsync(ct));
        var foreign = 0;
        string? foreignExample = null;
        await foreach (var blob in storage.ListAsync(ct))
        {
            if (known.Contains(blob.Key)) continue;
            if (!IsInstellaKey(blob.Key))
            {
                foreign++;
                foreignExample ??= blob.Key;
                continue;
            }
            if (blob.LastModified < cutoff && await TryDeleteAsync(storage, blob.Key, logger, ct))
                removed++;
        }
        if (foreign > 0)
            logger.LogWarning("Storage contains {Count} files that are not Instella's (for example {Key}); the storage folder or bucket should be dedicated to Instella",
                foreign, foreignExample);

        if (sessions > 0 || removed > 0)
            logger.LogInformation("Orphan sweep: {Sessions} expired session(s), {Blobs} blob(s) removed", sessions, removed);
        return removed;
    }

    /// <summary>Rows deleted per statement, so a long backlog never holds the write lock for long.</summary>
    private const int RetentionBatch = 10_000;

    /// <summary>
    /// Log retention: security events older than <paramref name="securityEventDays"/> and
    /// download logs older than <paramref name="downloadLogDays"/>, in batches of 10 000.
    /// Returns how many rows were deleted.
    /// </summary>
    public static async Task<int> ApplyRetentionAsync(
        AppDbContext db, DateTime now, int securityEventDays, int downloadLogDays, ILogger logger, CancellationToken ct)
    {
        var events = 0;
        var eventCutoff = now.AddDays(-securityEventDays);
        int n;
        do
        {
            n = await db.SecurityEvents.Where(e => e.Timestamp < eventCutoff)
                .OrderBy(e => e.Id).Take(RetentionBatch).ExecuteDeleteAsync(ct);
            events += n;
        } while (n == RetentionBatch);

        var downloads = 0;
        var downloadCutoff = now.AddDays(-downloadLogDays);
        do
        {
            n = await db.DownloadLogs.Where(l => l.Timestamp < downloadCutoff)
                .OrderBy(l => l.Id).Take(RetentionBatch).ExecuteDeleteAsync(ct);
            downloads += n;
        } while (n == RetentionBatch);

        if (events > 0 || downloads > 0)
            logger.LogInformation("Retention: {Events} security event(s) and {Downloads} download log(s) removed", events, downloads);
        return events + downloads;
    }

    private static async Task<bool> TryDeleteAsync(IStorageProvider storage, string key, ILogger logger, CancellationToken ct)
    {
        try
        {
            return await storage.DeleteAsync(key, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not delete blob {Key}; retried on the next sweep", key);
            return false;
        }
    }
}
