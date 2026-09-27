using System.Security.Claims;
using Instella.Core.Trust;
using Instella.Core.Wire;
using Instella.Server.Auth;
using Instella.Server.Data;
using Instella.Server.Data.Entities;
using Instella.Server.Extensions;
using Instella.Server.Models;
using Instella.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Instella.Server.Api;

/// <summary>
/// Uploads and version management. Every action checks the calling key against the single
/// permission matrix (<see cref="ApiPermissions"/>), and every session call must come
/// from the key that started the session.
/// </summary>
[ApiController]
[Route(ApiRoutes.Prefix)]
[Authorize(Policy = "ApiKey")]
public class UploadController(
    PackageService packageService,
    UploadService uploadService,
    AppDbContext db,
    IRateLimitService rateLimitService,
    ISecurityLogService securityLog,
    IOptions<UploadLimits> limits) : ControllerBase
{
    /// <summary>
    /// Start a new upload session.
    /// </summary>
    [HttpPost(ApiRoutes.UploadStart)]
    public async Task<IActionResult> StartUpload([FromBody] StartUploadRequest request, CancellationToken ct)
    {
        var ip = HttpContext.GetClientIpAddress();
        var (apiKey, _, denied) = await AuthorizeAsync(request.PackageId, ApiPermission.Upload, ct);
        if (denied is not null) return denied;

        if (!PlatformMapping.TryParseOs(request.Os, out var targetOs))
            return BadRequest(new ApiError { Error = PlatformMapping.InvalidOsMessage });
        if (!PlatformMapping.TryParseArch(request.Arch, out var targetArch))
            return BadRequest(new ApiError { Error = PlatformMapping.InvalidArchMessage });

        try
        {
            var session = await uploadService.StartSessionAsync(
                request.PackageId, request.Version, request.Channel ?? "stable", targetOs, targetArch, apiKey!.Id, ct);

            await securityLog.LogEventAsync(
                SecurityEventType.UploadStarted, ip, apiKeyName: apiKey.Name, packageId: request.PackageId,
                details: $"Session: {session.Id}, Version: {request.Version}", ct: ct);

            return Ok(new StartUploadResponse
            {
                SessionId = session.Id,
                PackageId = request.PackageId,
                Version = session.Version,
                Os = PlatformMapping.ToWire(targetOs),
                Arch = PlatformMapping.ToWire(targetArch),
            });
        }
        catch (UploadConflictException ex)
        {
            return Conflict(new ApiError { Error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new ApiError { Error = ex.Message });
        }
    }

    /// <summary>
    /// Upload a file to an active session. The request body limit is <c>Upload:MaxFileBytes</c>
    /// (default 2 GiB), enforced both by the server and by a streaming counter.
    /// </summary>
    [HttpPost(ApiRoutes.UploadFile)]
    [DisableRequestSizeLimit]
    public async Task<IActionResult> UploadFile(
        Guid sessionId,
        [FromQuery] string path,
        [FromQuery] string sha256,
        CancellationToken ct)
    {
        var ip = HttpContext.GetClientIpAddress();
        if (HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodyLimit)
            bodyLimit.MaxRequestBodySize = limits.Value.MaxFileBytes;

        var (apiKey, session, denied) = await AuthorizeSessionAsync(sessionId, ct);
        if (denied is not null) return denied;

        var rateLimitKey = $"upload:{apiKey!.KeyHash[..16]}@{ip}";
        var (allowed, delay) = rateLimitService.CheckRequest(rateLimitKey);
        if (!allowed)
        {
            await securityLog.LogEventAsync(SecurityEventType.UploadBlocked, ip, apiKeyName: apiKey.Name, ct: ct);
            Response.Headers["Retry-After"] = delay.ToString();
            return StatusCode(429, new ApiError { Error = $"Too many attempts. Retry after {delay} seconds." });
        }

        try
        {
            var result = await uploadService.UploadFileAsync(sessionId, path, sha256, Request.Body, ct);
            rateLimitService.RecordSuccess(rateLimitKey);
            return Ok(new UploadFileResponse
            {
                Stored = true,
                Deduplicated = result.Deduplicated,
                Path = result.RelativePath,
                Size = result.Size,
                Hash = result.ContentHash,
            });
        }
        catch (InvalidOperationException ex)
        {
            rateLimitService.RecordFailure(rateLimitKey);
            await securityLog.LogEventAsync(
                SecurityEventType.HashMismatch, ip, apiKeyName: apiKey.Name, packageId: session!.PackageId,
                details: ex.Message, ct: ct);
            return BadRequest(new ApiError { Error = ex.Message });
        }
    }

    /// <summary>
    /// Upload an installer (<c>online</c> or <c>offline</c>) to an active session. Same limits
    /// and rate limiting as <see cref="UploadFile"/>; the signed release must list it.
    /// </summary>
    [HttpPost(ApiRoutes.UploadInstaller)]
    [DisableRequestSizeLimit]
    public async Task<IActionResult> UploadInstaller(
        Guid sessionId,
        [FromQuery] string kind,
        [FromQuery] string fileName,
        [FromQuery] string sha256,
        CancellationToken ct)
    {
        var ip = HttpContext.GetClientIpAddress();
        if (HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodyLimit)
            bodyLimit.MaxRequestBodySize = limits.Value.MaxFileBytes;

        var (apiKey, session, denied) = await AuthorizeSessionAsync(sessionId, ct);
        if (denied is not null) return denied;

        var rateLimitKey = $"upload:{apiKey!.KeyHash[..16]}@{ip}";
        var (allowed, delay) = rateLimitService.CheckRequest(rateLimitKey);
        if (!allowed)
        {
            await securityLog.LogEventAsync(SecurityEventType.UploadBlocked, ip, apiKeyName: apiKey.Name, ct: ct);
            Response.Headers["Retry-After"] = delay.ToString();
            return StatusCode(429, new ApiError { Error = $"Too many attempts. Retry after {delay} seconds." });
        }

        try
        {
            var result = await uploadService.UploadInstallerAsync(sessionId, kind, fileName, sha256, Request.Body, ct);
            rateLimitService.RecordSuccess(rateLimitKey);
            return Ok(new UploadFileResponse
            {
                Stored = true,
                Deduplicated = result.Deduplicated,
                Path = result.RelativePath,
                Size = result.Size,
                Hash = result.ContentHash,
            });
        }
        catch (InvalidOperationException ex)
        {
            rateLimitService.RecordFailure(rateLimitKey);
            await securityLog.LogEventAsync(
                SecurityEventType.HashMismatch, ip, apiKeyName: apiKey.Name, packageId: session!.PackageId,
                details: ex.Message, ct: ct);
            return BadRequest(new ApiError { Error = ex.Message });
        }
    }

    /// <summary>
    /// Complete an upload session.
    /// </summary>
    [HttpPost(ApiRoutes.UploadComplete)]
    public async Task<IActionResult> CompleteUpload(
        Guid sessionId,
        [FromBody] CompleteUploadRequest? request,
        CancellationToken ct)
    {
        var ip = HttpContext.GetClientIpAddress();
        var (apiKey, session, denied) = await AuthorizeSessionAsync(sessionId, ct);
        if (denied is not null) return denied;

        try
        {
            if (request?.DraftManifest is not null && request.Release is not null)
                return BadRequest(new ApiError { Error = "A draft upload is unsigned; send either a release or a draft manifest" });
            UploadCompleteResult result;
            if (request?.DraftManifest is { } draft)
            {
                byte[] draftBytes;
                try { draftBytes = Convert.FromBase64String(draft); }
                catch (FormatException) { return BadRequest(new ApiError { Error = "Draft manifest is not base64" }); }
                result = await uploadService.CompleteDraftSessionAsync(sessionId, request.Changelog, draftBytes, ct);
            }
            else
            {
                result = await uploadService.CompleteSessionAsync(sessionId, request?.Changelog, request?.Release, ct);
            }

            await securityLog.LogEventAsync(
                SecurityEventType.UploadSuccess, ip, apiKeyName: apiKey!.Name, packageId: session!.PackageId,
                details: $"Version: {session.Version}, Files: {result.FileCount}, Size: {result.TotalSize}", ct: ct);

            return Ok(new CompleteUploadResponse
            {
                Success = true,
                BuildId = result.BuildId,
                VersionId = result.VersionId,
                FileCount = result.FileCount,
                TotalSize = result.TotalSize,
                DeduplicatedCount = result.DeduplicatedCount,
            });
        }
        catch (UploadConflictException ex)
        {
            return Conflict(new ApiError { Error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new ApiError { Error = ex.Message });
        }
    }

    /// <summary>The unsigned release manifest of a draft build, for <c>instella publish</c> to check and sign.</summary>
    [HttpGet(ApiRoutes.Draft)]
    public async Task<IActionResult> GetDraft(string packageId, string version, string os, string arch, CancellationToken ct)
    {
        var (_, _, denied) = await AuthorizeAsync(packageId, ApiPermission.Upload, ct);
        if (denied is not null) return denied;
        if (!PlatformMapping.TryParseOs(os, out var targetOs))
            return BadRequest(new ApiError { Error = PlatformMapping.InvalidOsMessage });
        if (!PlatformMapping.TryParseArch(arch, out var targetArch))
            return BadRequest(new ApiError { Error = PlatformMapping.InvalidArchMessage });

        var draft = await uploadService.GetDraftAsync(packageId, version, targetOs, targetArch, ct);
        if (draft?.ReleaseManifestBytes is null)
            return NotFound(new ApiError { Error = $"No draft of {packageId} {version} {os}/{arch}" });
        return Ok(new DraftResponse
        {
            Manifest = Convert.ToBase64String(draft.ReleaseManifestBytes),
            UploadedAt = draft.UploadedAt,
            Changelog = draft.Version.Changelog,
        });
    }

    /// <summary>Publishes a draft build with the publisher's signature over its manifest.</summary>
    [HttpPost(ApiRoutes.PublishDraft)]
    public async Task<IActionResult> PublishDraft(
        string packageId, string version, string os, string arch, [FromBody] SignedRelease release, CancellationToken ct)
    {
        var ip = HttpContext.GetClientIpAddress();
        var (apiKey, _, denied) = await AuthorizeAsync(packageId, ApiPermission.Upload, ct);
        if (denied is not null) return denied;
        if (!PlatformMapping.TryParseOs(os, out var targetOs))
            return BadRequest(new ApiError { Error = PlatformMapping.InvalidOsMessage });
        if (!PlatformMapping.TryParseArch(arch, out var targetArch))
            return BadRequest(new ApiError { Error = PlatformMapping.InvalidArchMessage });

        try
        {
            if (!await uploadService.PublishDraftAsync(packageId, version, targetOs, targetArch, release, ct))
                return NotFound(new ApiError { Error = $"No draft of {packageId} {version} {os}/{arch}" });
        }
        catch (UploadConflictException ex)
        {
            return Conflict(new ApiError { Error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new ApiError { Error = ex.Message });
        }

        await securityLog.LogEventAsync(
            SecurityEventType.UploadSuccess, ip, apiKeyName: apiKey!.Name, packageId: packageId,
            details: $"Published draft {version} {os}/{arch}, key {release.KeyId}", ct: ct);
        return Ok(new MessageResponse { Message = $"Published {packageId} {version} {os}/{arch}" });
    }

    /// <summary>
    /// Cancel an upload session.
    /// </summary>
    [HttpDelete(ApiRoutes.UploadCancel)]
    public async Task<IActionResult> CancelUpload(Guid sessionId, CancellationToken ct)
    {
        var (_, _, denied) = await AuthorizeSessionAsync(sessionId, ct);
        if (denied is not null) return denied;

        await uploadService.CancelSessionAsync(sessionId, ct);
        return Ok(new MessageResponse { Message = "Session cancelled" });
    }

    /// <summary>
    /// Delete a specific version.
    /// </summary>
    [HttpDelete(ApiRoutes.PackageVersion)]
    public async Task<IActionResult> DeleteVersion(string packageId, string version, CancellationToken ct)
    {
        var (_, _, denied) = await AuthorizeAsync(packageId, ApiPermission.ManageVersions, ct);
        if (denied is not null) return denied;

        var versionEntity = await packageService.GetVersionAsync(packageId, version, ct);
        if (versionEntity == null)
            return NotFound(new ApiError { Error = "Version not found" });

        await packageService.DeleteVersionAsync(versionEntity.Id, ct);
        return Ok(new MessageResponse { Message = "Version deleted" });
    }

    /// <summary>
    /// Update version metadata.
    /// </summary>
    [HttpPut(ApiRoutes.PackageVersion)]
    public async Task<IActionResult> UpdateVersion(
        string packageId,
        string version,
        [FromBody] UpdateVersionRequest request,
        CancellationToken ct)
    {
        var (_, _, denied) = await AuthorizeAsync(packageId, ApiPermission.ManageVersions, ct);
        if (denied is not null) return denied;

        var versionEntity = await packageService.GetVersionAsync(packageId, version, ct);
        if (versionEntity == null)
            return NotFound(new ApiError { Error = "Version not found" });

        await packageService.UpdateVersionAsync(versionEntity.Id, changelog: request.Changelog, isDeprecated: request.IsDeprecated, ct: ct);
        return Ok(new MessageResponse { Message = "Version updated" });
    }

    /// <summary>
    /// The calling key (authenticated by the ApiKey scheme) and whether it may perform
    /// <paramref name="permission"/> on <paramref name="packageId"/>: 401 without a key, 404 for an
    /// unknown package, 403 when the key lacks the permission.
    /// </summary>
    private async Task<(ApiKey? Key, Package? Package, IActionResult? Denied)> AuthorizeAsync(
        string packageId, ApiPermission permission, CancellationToken ct)
    {
        var key = await CurrentKeyAsync(ct);
        if (key is null)
            return (null, null, Unauthorized(new ApiError { Error = "A valid API key is required" }));

        var package = await db.Packages.AsNoTracking().FirstOrDefaultAsync(p => p.PackageId == packageId, ct);
        if (package is null)
            return (key, null, NotFound(new ApiError { Error = $"Package '{packageId}' not found" }));

        if (!ApiPermissions.Allows(key, package, permission))
            return (key, package, StatusCode(403, new ApiError { Error = $"Key lacks {permission} permission for this package" }));

        return (key, package, null);
    }

    /// <summary>An open session, which must belong to the calling key and allow uploads.</summary>
    private async Task<(ApiKey? Key, UploadSession? Session, IActionResult? Denied)> AuthorizeSessionAsync(Guid sessionId, CancellationToken ct)
    {
        var session = await uploadService.GetSessionAsync(sessionId, ct);
        if (session == null)
            return (null, null, NotFound(new ApiError { Error = "Session not found or expired" }));

        var (key, _, denied) = await AuthorizeAsync(session.PackageId, ApiPermission.Upload, ct);
        if (denied is not null)
            return (key, session, denied);

        if (session.ApiKeyId != key!.Id)
            return (key, session, StatusCode(403, new ApiError { Error = "This upload session belongs to another API key" }));

        return (key, session, null);
    }

    private async Task<ApiKey?> CurrentKeyAsync(CancellationToken ct)
    {
        var claim = User.FindFirst(ClaimTypes.NameIdentifier);
        if (claim is null || !long.TryParse(claim.Value, out var keyId))
            return null;
        return await db.ApiKeys.AsNoTracking().FirstOrDefaultAsync(k => k.Id == keyId && !k.IsRevoked, ct);
    }
}
