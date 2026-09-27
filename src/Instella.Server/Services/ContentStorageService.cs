using Instella.Server.Data;
using Instella.Server.Data.Entities;
using Instella.Server.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Instella.Server.Services;

/// <summary>
/// Content-addressed storage over the storage provider: one blob per unique SHA-256.
/// Reference counts are not touched here; they change only in bulk SQL inside the transactions
/// that complete an upload session or delete versions.
/// </summary>
public class ContentStorageService(
    AppDbContext db,
    IStorageProvider storage,
    ILogger<ContentStorageService> logger)
{
    /// <summary>The content row for <paramref name="contentHash"/>, if stored.</summary>
    public virtual async Task<StoredFile?> GetByHashAsync(string contentHash, CancellationToken ct = default) =>
        await db.StoredFiles.FirstOrDefaultAsync(f => f.ContentHash == contentHash, ct);

    /// <summary>
    /// Stores content uploaded into a session. New content gets reference count 0 and
    /// <see cref="StoredFile.PendingSince"/>; completing the session counts it. Content that
    /// already exists — including content another request inserts concurrently — is reused;
    /// when its blob has gone missing, this upload puts it back. Callers hold the
    /// hash's <see cref="ContentLocks"/> lock.
    /// </summary>
    /// <returns>The row, and whether the content already existed.</returns>
    public async Task<(StoredFile File, bool Deduplicated)> StorePendingAsync(
        string contentHash, Stream content, long size, CancellationToken ct = default)
    {
        if (await GetByHashAsync(contentHash, ct) is { } existing)
        {
            if (!await storage.ExistsAsync(existing.StoragePath, ct))
            {
                var healed = await storage.UploadAsync(existing.StoragePath, content, ct);
                if (!healed.Success)
                    throw new InvalidOperationException($"Failed to upload content: {healed.Error}");
                logger.LogWarning("Content {Hash} had a row but no blob; this upload restored {Path}", contentHash, existing.StoragePath);
            }
            return (existing, true);
        }

        // Format: {hash[0:2]}/{hash[2:4]}/{hash}
        var storagePath = $"{contentHash[..2]}/{contentHash[2..4]}/{contentHash}";
        var result = await storage.UploadAsync(storagePath, content, ct);
        if (!result.Success)
            throw new InvalidOperationException($"Failed to upload content: {result.Error}");

        var stored = new StoredFile
        {
            ContentHash = contentHash,
            Size = size,
            StoragePath = storagePath,
            ReferenceCount = 0,
            PendingSince = DateTime.UtcNow,
            FirstUploadedAt = DateTime.UtcNow,
        };
        db.StoredFiles.Add(stored);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 19 })
        {
            // Another upload of the same content won the insert. Content is addressed by
            // hash, so its blob is identical to ours: use its row.
            db.Entry(stored).State = EntityState.Detached;
            var winner = await GetByHashAsync(contentHash, ct)
                ?? throw new InvalidOperationException($"Content {contentHash} vanished during a concurrent insert", ex);
            return (winner, true);
        }

        logger.LogInformation("Stored new content {Hash} at {Path} ({Size} bytes)", contentHash, storagePath, size);
        return (stored, false);
    }

    /// <summary>Opens content by hash.</summary>
    public async Task<Stream?> RetrieveAsync(string contentHash, CancellationToken ct = default)
    {
        var storedFile = await GetByHashAsync(contentHash, ct);
        return storedFile == null ? null : await storage.DownloadAsync(storedFile.StoragePath, ct);
    }

    /// <summary>True when the blob for <paramref name="contentHash"/> exists in storage.</summary>
    public async Task<bool> ExistsAsync(string contentHash, CancellationToken ct = default)
    {
        var storedFile = await GetByHashAsync(contentHash, ct);
        return storedFile != null && await storage.ExistsAsync(storedFile.StoragePath, ct);
    }

    /// <summary>Opens content by storage path.</summary>
    public async Task<Stream?> RetrieveByPathAsync(string storagePath, CancellationToken ct = default) =>
        await storage.DownloadAsync(storagePath, ct);

    /// <summary>A presigned URL for direct download, if the storage provider supports it.</summary>
    public async Task<string?> GetPresignedUrlAsync(string contentHash, TimeSpan expiry, string? fileName = null, CancellationToken ct = default)
    {
        var storedFile = await GetByHashAsync(contentHash, ct);
        return storedFile == null ? null : await storage.GetPresignedUrlAsync(storedFile.StoragePath, expiry, fileName, ct);
    }

    /// <summary>
    /// Deletes a blob after the transaction that dropped its row committed. Best effort: a
    /// failure leaves an orphan the sweeper reconciles.
    /// </summary>
    public async Task TryDeleteBlobAsync(string storagePath, CancellationToken ct = default)
    {
        try
        {
            await storage.DeleteAsync(storagePath, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not delete blob {Path}; the orphan sweeper will retry", storagePath);
        }
    }

    /// <summary>Storage statistics.</summary>
    public async Task<StorageStats> GetStatsAsync(CancellationToken ct = default)
    {
        var files = await db.StoredFiles.ToListAsync(ct);
        return new StorageStats
        {
            TotalSize = files.Sum(f => f.Size),
            UniqueFiles = files.Count,
            TotalReferences = files.Sum(f => f.ReferenceCount),
            DeduplicationSavings = files.Sum(f => f.Size * Math.Max(0, f.ReferenceCount - 1)),
        };
    }
}

/// <summary>
/// Storage statistics.
/// </summary>
public record StorageStats
{
    public long TotalSize { get; init; }
    public int UniqueFiles { get; init; }
    public int TotalReferences { get; init; }
    public long DeduplicationSavings { get; init; }
}
