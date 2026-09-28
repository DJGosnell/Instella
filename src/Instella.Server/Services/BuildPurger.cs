using Instella.Server.Data;
using Instella.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Instella.Server.Services;

/// <summary>
/// The one way builds are removed (version and package deletion, build deletion, rejecting a release):
/// content locks first, then the rows and reference counts in the caller's transaction, then the
/// blobs after the commit.
/// </summary>
internal sealed class BuildPurger(AppDbContext db, ContentStorageService contentStorage)
{
    /// <summary>
    /// Inside the caller's transaction: removes the builds' patches and patch jobs, their file
    /// rows (one reference-count decrement per row, grouped by hash), content no build references
    /// any more, and the builds themselves (download logs cascade). Returns the storage paths to
    /// delete after commit.
    /// </summary>
    public async Task<List<string>> PurgeBuildsAsync(List<long> buildIds, CancellationToken ct)
    {
        if (buildIds.Count == 0) return [];

        var patchPaths = await db.BuildPatches
            .Where(p => buildIds.Contains(p.FromBuildId) || buildIds.Contains(p.ToBuildId))
            .Select(p => p.StoragePath).ToListAsync(ct);
        await db.BuildPatches
            .Where(p => buildIds.Contains(p.FromBuildId) || buildIds.Contains(p.ToBuildId))
            .ExecuteDeleteAsync(ct);
        await db.PendingPatchJobs
            .Where(j => buildIds.Contains(j.ToBuildId) || (j.FromBuildId != null && buildIds.Contains(j.FromBuildId.Value)))
            .ExecuteDeleteAsync(ct);

        var fileHashes = await db.BuildFiles.Where(f => buildIds.Contains(f.BuildId)).Select(f => f.ContentHash).ToListAsync(ct);
        var installerHashes = await db.BuildInstallers.Where(i => buildIds.Contains(i.BuildId)).Select(i => i.ContentHash).ToListAsync(ct);
        var hashCounts = fileHashes.Concat(installerHashes).GroupBy(h => h).Select(g => new { Hash = g.Key, Count = g.Count() }).ToList();
        await db.BuildFiles.Where(f => buildIds.Contains(f.BuildId)).ExecuteDeleteAsync(ct);
        await db.BuildInstallers.Where(i => buildIds.Contains(i.BuildId)).ExecuteDeleteAsync(ct);
        foreach (var h in hashCounts)
            await db.StoredFiles.Where(f => f.ContentHash == h.Hash)
                .ExecuteUpdateAsync(u => u.SetProperty(f => f.ReferenceCount, f => f.ReferenceCount - h.Count), ct);

        // Content no build references, unless an open upload session is about to reuse it.
        var touched = hashCounts.Select(h => h.Hash).ToList();
        var orphans = db.StoredFiles.Where(f => touched.Contains(f.ContentHash) && f.ReferenceCount <= 0
                                                && !db.UploadSessionFiles.Any(u => u.ContentHash == f.ContentHash)
                                                && !db.UploadSessionInstallers.Any(u => u.ContentHash == f.ContentHash));
        var orphanPaths = await orphans.Select(f => f.StoragePath).ToListAsync(ct);
        await orphans.ExecuteDeleteAsync(ct);

        await db.VersionBuilds.Where(b => buildIds.Contains(b.Id)).ExecuteDeleteAsync(ct);
        return [.. orphanPaths, .. patchPaths];
    }

    /// <summary>
    /// Takes the <see cref="ContentLocks"/> of every content hash the <paramref name="builds"/>
    /// reference (files and installers). Called before the purge's transaction starts.
    /// </summary>
    public async Task<IAsyncDisposable> LockContentAsync(IQueryable<VersionBuild> builds, CancellationToken ct)
    {
        var ids = builds.Select(b => b.Id);
        var hashes = await db.BuildFiles.Where(f => ids.Contains(f.BuildId)).Select(f => f.ContentHash)
            .Concat(db.BuildInstallers.Where(i => ids.Contains(i.BuildId)).Select(i => i.ContentHash))
            .Distinct().ToListAsync(ct);
        return await ContentLocks.Shared.AcquireAsync(hashes, ct);
    }

    public async Task DeleteBlobsAsync(IEnumerable<string> paths, CancellationToken ct)
    {
        foreach (var path in paths)
            await contentStorage.TryDeleteBlobAsync(path, ct);
    }

}
