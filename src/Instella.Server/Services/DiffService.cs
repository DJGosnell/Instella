using System.IO.Compression;
using System.Text.Json;
using Instella.Core.BSDiff;
using Instella.Core.Utilities;
using Instella.Core.Update;
using Instella.Core.Wire;
using Instella.Server.Data;
using Instella.Server.Data.Entities;
using Instella.Server.Models;
using Instella.Server.Storage;
using Microsoft.EntityFrameworkCore;

namespace Instella.Server.Services;

/// <summary>What a patch job achieved; every outcome completes the job.</summary>
internal enum PatchOutcome
{
    /// <summary>A patch was stored.</summary>
    Created,

    /// <summary>The patch would be too close to the full download to be worth it.</summary>
    NotWorthIt,

    /// <summary>No earlier version on the channel has a build for the platform.</summary>
    NoPreviousBuild,

    /// <summary><c>Diff:Enabled</c> is false.</summary>
    Disabled,

    /// <summary>A build was deleted before the job ran.</summary>
    BuildGone,
}

/// <summary>The outcome, and the patch when one was created.</summary>
internal sealed record PatchResult(PatchOutcome Outcome, BuildPatch? Patch = null);

/// <summary>A real failure (storage, a missing blob, the archive): the job is retried.</summary>
public sealed class PatchGenerationException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Service for generating binary diff patches between builds.
/// </summary>
public class DiffService(
    AppDbContext db,
    IStorageProvider storage,
    IConfiguration configuration,
    ILogger<DiffService> logger)
{
    private readonly bool _enabled = configuration.GetValue("Diff:Enabled", true);
    private readonly double _maxPatchRatio = configuration.GetValue("Diff:MaxPatchRatio", 0.9);

    /// <summary>
    /// Files larger than this on either side are shipped whole in the patch, never diffed:
    /// BSDiff holds both files and several times their size in memory.
    /// </summary>
    private readonly long _maxFileBytes = configuration.GetValue("Diff:MaxFileBytes", 256L * 1024 * 1024);

    /// <summary>
    /// Generates a patch to <paramref name="toBuildId"/> from the previous build on its channel
    /// (<see cref="FindPreviousBuildAsync"/>).
    /// </summary>
    /// <exception cref="PatchGenerationException">Storage failed, content is missing, or the archive could not be written.</exception>
    internal async Task<PatchResult> GeneratePatchAsync(long toBuildId, CancellationToken ct = default)
    {
        if (!_enabled)
        {
            logger.LogInformation("Diff generation is disabled");
            return new PatchResult(PatchOutcome.Disabled);
        }

        var toBuild = await db.VersionBuilds
            .Include(b => b.Version)
            .ThenInclude(v => v.Package)
            .FirstOrDefaultAsync(b => b.Id == toBuildId, ct);

        if (toBuild == null)
        {
            logger.LogWarning("Build {BuildId} not found", toBuildId);
            return new PatchResult(PatchOutcome.BuildGone);
        }

        var fromBuild = await FindPreviousBuildAsync(toBuild, ct);
        if (fromBuild == null)
        {
            logger.LogInformation("No previous build found for {PackageId} {OS}/{Arch}",
                toBuild.Version.Package.PackageId, toBuild.OS, toBuild.Architecture);
            return new PatchResult(PatchOutcome.NoPreviousBuild);
        }

        return await GeneratePatchAsync(fromBuild.Id, toBuildId, ct);
    }

    /// <summary>Generates a patch between two specific builds.</summary>
    /// <exception cref="PatchGenerationException">Storage failed, content is missing, or the archive could not be written.</exception>
    internal async Task<PatchResult> GeneratePatchAsync(long fromBuildId, long toBuildId, CancellationToken ct = default)
    {
        if (!_enabled)
        {
            logger.LogInformation("Diff generation is disabled");
            return new PatchResult(PatchOutcome.Disabled);
        }

        var fromBuild = await db.VersionBuilds
            .Include(b => b.Version)
            .Include(b => b.Files)
            .ThenInclude(f => f.StoredFile)
            .FirstOrDefaultAsync(b => b.Id == fromBuildId, ct);

        var toBuild = await db.VersionBuilds
            .Include(b => b.Version)
            .ThenInclude(v => v.Package)
            .Include(b => b.Files)
            .ThenInclude(f => f.StoredFile)
            .FirstOrDefaultAsync(b => b.Id == toBuildId, ct);

        if (fromBuild == null || toBuild == null)
        {
            logger.LogWarning("Build(s) not found: from={FromId}, to={ToId}", fromBuildId, toBuildId);
            return new PatchResult(PatchOutcome.BuildGone);
        }

        logger.LogInformation("Generating patch from {FromVersion} to {ToVersion} for {OS}/{Arch}",
            fromBuild.Version.VersionString, toBuild.Version.VersionString, toBuild.OS, toBuild.Architecture);

        // Paths are case-sensitive in the release manifest: App.dll and app.dll are two files
        // of a valid Linux build.
        var fromFiles = fromBuild.Files.ToDictionary(f => f.RelativePath, StringComparer.Ordinal);
        var toFiles = toBuild.Files.ToDictionary(f => f.RelativePath, StringComparer.Ordinal);

        var patchedFiles = new List<PatchedFile>();
        var newFiles = new List<NewFile>();
        var deletedFiles = new List<string>();
        var verificationList = new List<FileHash>();
        var patchSources = new Dictionary<string, string>(StringComparer.Ordinal);   // patch sha -> temp file

        var tempDir = Path.Combine(Path.GetTempPath(), $"instella-patch-{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            foreach (var toFile in toBuild.Files)
            {
                verificationList.Add(new FileHash(toFile.RelativePath, toFile.ContentHash));

                if (!fromFiles.TryGetValue(toFile.RelativePath, out var fromFile))
                {
                    newFiles.Add(new NewFile(toFile.RelativePath, toFile.ContentHash, toFile.Size));
                    continue;
                }
                if (fromFile.ContentHash == toFile.ContentHash)
                    continue;   // unchanged

                if (fromFile.Size > _maxFileBytes || toFile.Size > _maxFileBytes)
                {
                    // Too big to diff in memory: shipped whole.
                    logger.LogInformation("{Path} is over Diff:MaxFileBytes; it is sent whole in the patch", toFile.RelativePath);
                    newFiles.Add(new NewFile(toFile.RelativePath, toFile.ContentHash, toFile.Size));
                    continue;
                }

                var patchPath = Path.Combine(tempDir, $"{toFile.Id}.patch");
                var patch = await GenerateFilePatchAsync(fromFile.StoredFile.StoragePath, toFile.StoredFile.StoragePath, patchPath, ct);
                patchedFiles.Add(new PatchedFile(toFile.RelativePath, patch.PatchHash, patch.PatchSize, toFile.ContentHash));
                patchSources.TryAdd(patch.PatchHash, patchPath);
            }

            foreach (var fromFile in fromBuild.Files)
            {
                if (!toFiles.ContainsKey(fromFile.RelativePath))
                    deletedFiles.Add(fromFile.RelativePath);
            }

            var totalPatchSize = patchedFiles.Sum(p => p.PatchSize) + newFiles.Sum(f => f.Size);
            var fullSize = toBuild.TotalSize;
            var ratio = fullSize > 0 ? (double)totalPatchSize / fullSize : 1.0;
            if (ratio >= _maxPatchRatio)
            {
                logger.LogInformation("Patch size ({PatchSize:N0} bytes) is {Ratio:P1} of full - not worth it",
                    totalPatchSize, ratio);
                return new PatchResult(PatchOutcome.NotWorthIt);
            }

            var manifest = new PatchManifest
            {
                FromVersion = Version.Parse(fromBuild.Version.VersionString),
                ToVersion = Version.Parse(toBuild.Version.VersionString),
                PatchedFiles = patchedFiles,
                NewFiles = newFiles,
                DeletedFiles = deletedFiles,
                VerificationList = verificationList,
            };
            var manifestJson = JsonSerializer.Serialize(manifest, WireJsonContext.Default.PatchManifest);

            var archivePath = Path.Combine(tempDir, "patch.zip");
            try
            {
                using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
                var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
                await using (var entryStream = manifestEntry.Open())
                await using (var writer = new StreamWriter(entryStream))
                {
                    await writer.WriteAsync(manifestJson);
                }
                foreach (var (patchHash, source) in patchSources)
                    archive.CreateEntryFromFile(source, $"{patchHash}.patch", CompressionLevel.Optimal);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                throw new PatchGenerationException($"Could not write the patch archive: {ex.Message}", ex);
            }

            var patchStoragePath = $"patches/{toBuild.Version.Package.PackageId}/{fromBuild.Version.VersionString}-to-{toBuild.Version.VersionString}/{PlatformMapping.ToWire(toBuild.OS)}-{PlatformMapping.ToWire(toBuild.Architecture)}/patch.zip";
            await using (var archiveStream = File.OpenRead(archivePath))
            {
                var uploadResult = await storage.UploadAsync(patchStoragePath, archiveStream, ct);
                if (!uploadResult.Success)
                    throw new PatchGenerationException($"Could not store the patch archive: {uploadResult.Error}");
            }

            string archiveHash;
            await using (var archiveStream = File.OpenRead(archivePath))
                archiveHash = await Checksum.ComputeSHA256Async(archiveStream, ct);
            var archiveSize = new FileInfo(archivePath).Length;

            var buildPatch = new BuildPatch
            {
                FromBuildId = fromBuildId,
                ToBuildId = toBuildId,
                PatchSize = archiveSize,
                PatchHash = archiveHash,
                StoragePath = patchStoragePath,
                ManifestJson = manifestJson,
                GeneratedAt = DateTime.UtcNow,
            };
            db.BuildPatches.Add(buildPatch);
            await db.SaveChangesAsync(ct);

            logger.LogInformation("Generated patch {PatchId}: {PatchSize:N0} bytes ({Ratio:P1} of full), {Patched} patched, {New} new, {Deleted} deleted",
                buildPatch.Id, archiveSize, ratio, patchedFiles.Count, newFiles.Count, deletedFiles.Count);
            return new PatchResult(PatchOutcome.Created, buildPatch);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    /// <summary>
    /// Gets the patch manifest for a build patch.
    /// </summary>
    internal async Task<PatchManifest?> GetPatchManifestAsync(long patchId, CancellationToken ct = default)
    {
        var patch = await db.BuildPatches.FindAsync([patchId], ct);
        if (patch == null)
            return null;

        return JsonSerializer.Deserialize(patch.ManifestJson, WireJsonContext.Default.PatchManifest);
    }

    /// <summary>
    /// Gets a patch between two builds.
    /// </summary>
    public async Task<BuildPatch?> GetPatchAsync(long fromBuildId, long toBuildId, CancellationToken ct = default)
    {
        return await db.BuildPatches
            .FirstOrDefaultAsync(p => p.FromBuildId == fromBuildId && p.ToBuildId == toBuildId, ct);
    }

    private async Task<VersionBuild?> FindPreviousBuildAsync(VersionBuild currentBuild, CancellationToken ct)
    {
        // The patch source: the highest version below this one on the same channel, not
        // deprecated, with a published build for the platform. The pin plays no part, so an
        // older-line hotfix (1.2.5 after 1.3.0) patches from its own predecessor (1.2.4).
        var previousVersion = await db.PackageVersions
            .Include(v => v.Builds)
            .Where(v =>
                v.PackageId == currentBuild.Version.PackageId &&
                v.Channel == currentBuild.Version.Channel &&
                v.Id != currentBuild.VersionId &&
                string.Compare(v.VersionKey, currentBuild.Version.VersionKey) < 0 &&
                !v.IsDeprecated &&
                v.Builds.Any(b => !b.IsDraft && b.OS == currentBuild.OS && b.Architecture == currentBuild.Architecture))
            .OrderByDescending(v => v.VersionKey)
            .FirstOrDefaultAsync(ct);

        if (previousVersion == null)
            return null;

        // Find a published build with the same OS/Architecture (nobody has a draft installed)
        return previousVersion.Builds.FirstOrDefault(b =>
            !b.IsDraft &&
            b.OS == currentBuild.OS &&
            b.Architecture == currentBuild.Architecture);
    }

    /// <exception cref="PatchGenerationException">A blob is missing or storage failed.</exception>
    private async Task<FilePatchResult> GenerateFilePatchAsync(
        string oldStoragePath,
        string newStoragePath,
        string outputPath,
        CancellationToken ct)
    {
        Stream? oldStream;
        Stream? newStream;
        try
        {
            oldStream = await storage.DownloadAsync(oldStoragePath, ct);
            newStream = await storage.DownloadAsync(newStoragePath, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new PatchGenerationException($"Could not read content for a patch: {ex.Message}", ex);
        }

        if (oldStream == null || newStream == null)
        {
            oldStream?.Dispose();
            newStream?.Dispose();
            throw new PatchGenerationException(
                $"Content for a patch is missing from storage: {(oldStream == null ? oldStoragePath : newStoragePath)}");
        }

        using (oldStream)
        using (newStream)
        {
            var oldData = await ReadStreamAsync(oldStream, ct);
            var newData = await ReadStreamAsync(newStream, ct);
            await using var patchStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
            BSDiffEncoder.Create(oldData, newData, patchStream);
        }

        await using var patchReadStream = File.OpenRead(outputPath);
        var patchHash = await Checksum.ComputeSHA256Async(patchReadStream, ct);
        var patchSize = new FileInfo(outputPath).Length;
        return new FilePatchResult(patchHash, patchSize);
    }

    private static async Task<byte[]> ReadStreamAsync(Stream stream, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    private record FilePatchResult(string PatchHash, long PatchSize);
}
