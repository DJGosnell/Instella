using System.IO.Compression;
using System.Security.Cryptography;
using Instella.Core.Trust;
using Instella.Core.Utilities;
using Instella.Core.Wire;
using Instella.Server.Models;
using Instella.Server.Data;
using Instella.Server.Data.Entities;
using Instella.Server.Extensions;
using Instella.Server.Services;
using Instella.Server.Storage;
using Microsoft.AspNetCore.Authorization;
using Instella.Core.FileSystem;
using Instella.Server.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Instella.Server.Api;

[ApiController]
[Route(ApiRoutes.Prefix)]
[EnableRateLimiting(DownloadRateLimit.Policy)]
public class DownloadController(
    AppDbContext db,
    PackageService packageService,
    ContentStorageService contentStorage,
    StorageSettingsService storageSettingsService,
    IStorageProvider storage,
    DownloadAccess downloadAccess) : ControllerBase
{
    /// <summary>
    /// Download full build as ZIP archive.
    /// </summary>
    [HttpGet(ApiRoutes.DownloadBuild)]
    public async Task<IActionResult> DownloadBuild(
        string packageId,
        string version,
        string os,
        string arch,
        CancellationToken ct)
    {
        if (!PlatformMapping.TryParseOs(os, out var targetOs))
            return BadRequest(new ApiError { Error = PlatformMapping.InvalidOsMessage });

        if (!PlatformMapping.TryParseArch(arch, out var targetArch))
            return BadRequest(new ApiError { Error = PlatformMapping.InvalidArchMessage });

        var build = await packageService.GetBuildAsync(packageId, version, targetOs, targetArch, ct);
        if (build == null)
            return NotFound(new ApiError { Error = "Build not found" });

        // Validate download access
        var (allowed, error) = await ValidateDownloadAccessAsync(build.Version.Package, ct);
        if (!allowed) return error!;

        // Track download
        var ipHash = HashIpAddress(HttpContext.GetClientIpAddress());
        var userAgent = Request.Headers.UserAgent.ToString();
        await packageService.IncrementDownloadCountAsync(build.Id, false, ipHash, userAgent, ct);

        // Every blob must exist before a single response byte is written: a ZIP that stops
        // half-way looks like a network error to the client. Entry names pass
        // through SafePath again, as defence in depth for rows older than upload validation.
        foreach (var file in build.Files)
        {
            if (!SafePath.TryNormalizeRelative(file.RelativePath, out var normalized, out _) || normalized != file.RelativePath)
                return StatusCode(500, new ApiError { Error = $"Build contains an unsafe path '{file.RelativePath}'" });
            if (!await contentStorage.ExistsAsync(file.ContentHash, ct))
                return StatusCode(500, new ApiError { Error = $"Content for '{file.RelativePath}' is missing from storage" });
        }

        // Stream the archive straight into the response; ZIP creation supports non-seekable output.
        // An entry's writer stream disposes synchronously and flushes its final deflate block
        // with a blocking Write, so this one response allows synchronous IO for that small tail.
        if (HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpBodyControlFeature>() is { } bodyControl)
            bodyControl.AllowSynchronousIO = true;
        Response.ContentType = "application/zip";
        Response.Headers.ContentDisposition = $"attachment; filename=\"{packageId}-{version}-{os}-{arch}.zip\"";
        await using (var archive = await ZipArchive.CreateAsync(Response.Body, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: null, ct))
        {
            foreach (var file in build.Files)
            {
                await using var stream = await contentStorage.RetrieveAsync(file.ContentHash, ct)
                    ?? throw new InvalidOperationException($"Content for '{file.RelativePath}' disappeared during the download");
                var entry = archive.CreateEntry(file.RelativePath, CompressionLevel.Fastest);
                await using var entryStream = await entry.OpenAsync(ct);
                await stream.CopyToAsync(entryStream, ct);
            }
        }
        return new EmptyResult();
    }

    /// <summary>
    /// The publisher-signed release manifest of one build, exactly as uploaded. Clients
    /// verify it against the keys compiled into their installer; the server only relays it.
    /// </summary>
    [HttpGet(ApiRoutes.Release)]
    public async Task<IActionResult> GetRelease(
        string packageId,
        string version,
        string os,
        string arch,
        CancellationToken ct)
    {
        if (!PlatformMapping.TryParseOs(os, out var targetOs))
            return BadRequest(new ApiError { Error = PlatformMapping.InvalidOsMessage });

        if (!PlatformMapping.TryParseArch(arch, out var targetArch))
            return BadRequest(new ApiError { Error = PlatformMapping.InvalidArchMessage });

        var build = await packageService.GetBuildAsync(packageId, version, targetOs, targetArch, ct);
        if (build == null)
            return NotFound(new ApiError { Error = "Build not found" });

        var (allowed, error) = await ValidateDownloadAccessAsync(build.Version.Package, ct);
        if (!allowed) return error!;

        var release = SignedReleaseOf(build);
        if (release is null)
            return NotFound(new ApiError { Error = "This build was uploaded without a publisher signature" });
        return Ok(release);
    }

    internal static SignedRelease? SignedReleaseOf(VersionBuild build) =>
        build.ReleaseManifestBytes is { } bytes && build.ReleaseSignature is { } signature && build.ReleaseKeyId is { } keyId
            ? new SignedRelease(Convert.ToBase64String(bytes), signature, keyId)
            : null;

    /// <summary>
    /// Download a single file from a build.
    /// </summary>
    [HttpGet(ApiRoutes.DownloadFile)]
    public async Task<IActionResult> DownloadFile(
        string packageId,
        string version,
        string os,
        string arch,
        string path,
        CancellationToken ct)
    {
        if (!PlatformMapping.TryParseOs(os, out var targetOs))
            return BadRequest(new ApiError { Error = PlatformMapping.InvalidOsMessage });

        if (!PlatformMapping.TryParseArch(arch, out var targetArch))
            return BadRequest(new ApiError { Error = PlatformMapping.InvalidArchMessage });

        var build = await packageService.GetBuildAsync(packageId, version, targetOs, targetArch, ct);
        if (build == null)
            return NotFound(new ApiError { Error = "Build not found" });

        // Validate download access
        var (allowed, error) = await ValidateDownloadAccessAsync(build.Version.Package, ct);
        if (!allowed) return error!;

        // Find the file
        var file = build.Files.FirstOrDefault(f => f.RelativePath.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (file == null)
            return NotFound(new ApiError { Error = "File not found in build" });

        // Try presigned URL first
        var settings = await storageSettingsService.GetSettingsAsync();
        if (settings.StorageProvider == StorageProviderType.S3)
        {
            var expiry = TimeSpan.FromMinutes(settings.S3UrlExpiryMinutes > 0 ? settings.S3UrlExpiryMinutes : 60);
            var url = await contentStorage.GetPresignedUrlAsync(file.ContentHash, expiry, Path.GetFileName(path), ct);
            if (url != null)
                return Redirect(url);
        }

        // Stream from storage
        var stream = await contentStorage.RetrieveAsync(file.ContentHash, ct);
        if (stream == null)
            return NotFound(new ApiError { Error = "File not found in storage" });

        return File(stream, "application/octet-stream", Path.GetFileName(path));
    }

    /// <summary>
    /// Download patch archive.
    /// </summary>
    [HttpGet(ApiRoutes.Patch)]
    public async Task<IActionResult> DownloadPatch(
        string packageId,
        string fromVersion,
        string toVersion,
        string os,
        string arch,
        CancellationToken ct)
    {
        if (!PlatformMapping.TryParseOs(os, out var targetOs))
            return BadRequest(new ApiError { Error = PlatformMapping.InvalidOsMessage });

        if (!PlatformMapping.TryParseArch(arch, out var targetArch))
            return BadRequest(new ApiError { Error = PlatformMapping.InvalidArchMessage });

        // Find builds
        var fromBuild = await packageService.GetBuildAsync(packageId, fromVersion, targetOs, targetArch, ct);
        var toBuild = await packageService.GetBuildAsync(packageId, toVersion, targetOs, targetArch, ct);

        if (fromBuild == null || toBuild == null)
            return NotFound(new ApiError { Error = "Build not found" });

        // Validate download access
        var (allowed, error) = await ValidateDownloadAccessAsync(toBuild.Version.Package, ct);
        if (!allowed) return error!;

        // Find patch
        var patch = await db.BuildPatches
            .FirstOrDefaultAsync(p => p.FromBuildId == fromBuild.Id && p.ToBuildId == toBuild.Id, ct);

        if (patch == null)
            return NotFound(new ApiError { Error = "Patch not available for these versions" });

        // Track download
        var ipHash = HashIpAddress(HttpContext.GetClientIpAddress());
        var userAgent = Request.Headers.UserAgent.ToString();
        await packageService.IncrementDownloadCountAsync(toBuild.Id, true, ipHash, userAgent, ct);

        var fileName = $"{packageId}-{fromVersion}-to-{toVersion}-{os}-{arch}.patch.zip";

        // Try presigned URL first
        var settings = await storageSettingsService.GetSettingsAsync();
        if (settings.StorageProvider == StorageProviderType.S3)
        {
            var expiry = TimeSpan.FromMinutes(settings.S3UrlExpiryMinutes > 0 ? settings.S3UrlExpiryMinutes : 60);
            var url = await storage.GetPresignedUrlAsync(patch.StoragePath, expiry, fileName, ct);
            if (url != null)
                return Redirect(url);
        }

        // Stream from storage
        var stream = await storage.DownloadAsync(patch.StoragePath, ct);
        if (stream == null)
            return NotFound(new ApiError { Error = "Patch file not found in storage" });

        return File(stream, "application/zip", fileName);
    }

    /// <summary>
    /// Get patch manifest JSON.
    /// </summary>
    [HttpGet(ApiRoutes.PatchManifest)]
    public async Task<IActionResult> GetPatchManifest(
        string packageId,
        string fromVersion,
        string toVersion,
        string os,
        string arch,
        CancellationToken ct)
    {
        if (!PlatformMapping.TryParseOs(os, out var targetOs))
            return BadRequest(new ApiError { Error = PlatformMapping.InvalidOsMessage });

        if (!PlatformMapping.TryParseArch(arch, out var targetArch))
            return BadRequest(new ApiError { Error = PlatformMapping.InvalidArchMessage });

        // Find builds
        var fromBuild = await packageService.GetBuildAsync(packageId, fromVersion, targetOs, targetArch, ct);
        var toBuild = await packageService.GetBuildAsync(packageId, toVersion, targetOs, targetArch, ct);

        if (fromBuild == null || toBuild == null)
            return NotFound(new ApiError { Error = "Build not found" });

        // Validate download access
        var (allowed, error) = await ValidateDownloadAccessAsync(toBuild.Version.Package, ct);
        if (!allowed) return error!;

        // Find patch
        var patch = await db.BuildPatches
            .FirstOrDefaultAsync(p => p.FromBuildId == fromBuild.Id && p.ToBuildId == toBuild.Id, ct);

        if (patch == null)
            return NotFound(new ApiError { Error = "Patch not available for these versions" });

        return Content(patch.ManifestJson, "application/json");
    }

    /// <summary>
    /// Check for updates.
    /// </summary>
    [HttpGet(ApiRoutes.CheckUpdate)]
    public async Task<IActionResult> CheckUpdate(
        [FromQuery] string packageId,
        [FromQuery] string currentVersion,
        [FromQuery] string os,
        [FromQuery] string arch,
        [FromQuery] string? channel,
        CancellationToken ct)
    {
        if (!PlatformMapping.TryParseOs(os, out var targetOs))
            return BadRequest(new ApiError { Error = PlatformMapping.InvalidOsMessage });

        if (!PlatformMapping.TryParseArch(arch, out var targetArch))
            return BadRequest(new ApiError { Error = PlatformMapping.InvalidArchMessage });

        var targetChannel = ChannelNames.Stable;
        if (channel != null && !ChannelNames.TryNormalize(channel, out targetChannel))
            return BadRequest(new ApiError { Error = ChannelNames.Rule });

        var latestVersion = await packageService.GetLatestVersionAsync(packageId, targetChannel, targetOs, targetArch, ct);
        if (latestVersion == null)
            return Ok(NoUpdate);

        // Whether a private package has an update is private too.
        var (allowed, error) = await ValidateDownloadAccessAsync(latestVersion.Package, ct, Ok(NoUpdate));
        if (!allowed) return error!;

        var latestBuild = latestVersion.Builds.FirstOrDefault(b => b.OS == targetOs && b.Architecture == targetArch && !b.IsDraft);
        if (latestBuild == null)
            return Ok(NoUpdate);

        // Compare canonical versions: 1.3 and 1.3.0.0 are 1.3.0.
        if (!AppVersions.TryParse(currentVersion, out var current) || !AppVersions.TryParse(latestVersion.VersionString, out var latest))
            return Ok(NoUpdate);

        if (AppVersions.Compare(latest, current) <= 0)
            return Ok(NoUpdate);

        // Check for patch (from the build of the canonical current version)
        var currentBuild = await packageService.GetBuildAsync(packageId, AppVersions.ToCanonicalString(current), targetOs, targetArch, ct);
        BuildPatch? patch = null;
        if (currentBuild != null)
        {
            patch = await db.BuildPatches
                .FirstOrDefaultAsync(p => p.FromBuildId == currentBuild.Id && p.ToBuildId == latestBuild.Id, ct);
        }

        return Ok(new CheckUpdateResponse
        {
            UpdateAvailable = true,
            Version = latestVersion.VersionString,
            Changelog = latestVersion.Changelog,
            FullSize = latestBuild.TotalSize,
            PatchAvailable = patch != null,
            PatchSize = patch?.PatchSize,
            PatchSha256 = patch?.PatchHash,
            Release = SignedReleaseOf(latestBuild),
        });
    }

    private static readonly CheckUpdateResponse NoUpdate = new() { UpdateAvailable = false };

    private static string HashIpAddress(string? ip)
    {
        if (string.IsNullOrEmpty(ip)) return "";
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(ip));
        return Convert.ToHexStringLower(hash)[..16];
    }

    /// <summary>
    /// The download access rule; without valid credentials the answer is the route's answer for an
    /// unknown build (<paramref name="unknown"/>, default 404 "Build not found"), so a private
    /// package's existence does not leak.
    /// </summary>
    private async Task<(bool allowed, IActionResult? errorResult)> ValidateDownloadAccessAsync(
        Package package,
        CancellationToken ct,
        IActionResult? unknown = null)
    {
        var denied = await downloadAccess.CheckAsync(HttpContext, package, ct,
            unknown ?? NotFound(new ApiError { Error = "Build not found" }));
        return (denied is null, denied);
    }
}
