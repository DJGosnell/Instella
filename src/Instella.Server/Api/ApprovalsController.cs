using System.Security.Claims;
using Instella.Core.Wire;
using Instella.Server.Auth;
using Instella.Server.Data;
using Instella.Server.Data.Entities;
using Instella.Server.Extensions;
using Instella.Server.Models;
using Instella.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Instella.Server.Api;

/// <summary>
/// Release approval over the API (<c>instella pending</c>, <c>approve</c>, <c>reject</c>). Every action
/// needs <see cref="ApiPermission.ApproveReleases"/> on the package; a key never approves its own
/// upload. Decisions are made and audited by <see cref="ReleaseApprovalService"/>.
/// </summary>
[ApiController]
[Route(ApiRoutes.Prefix)]
[Authorize(Policy = "ApiKey")]
public class ApprovalsController(AppDbContext db, ReleaseApprovalService approvals) : ControllerBase
{
    /// <summary>The package's drafts and pending releases, oldest first.</summary>
    [HttpGet(ApiRoutes.Approvals)]
    public async Task<IActionResult> List(string packageId, CancellationToken ct)
    {
        var (_, package, denied) = await AuthorizeAsync(packageId, ct);
        if (denied is not null) return denied;

        var builds = await approvals.ListUnpublishedAsync(package!.Id, ct);
        return Ok(builds.Select(ToSummary).ToArray());
    }

    /// <summary>One draft or pending release, with its manifest bytes to review.</summary>
    [HttpGet(ApiRoutes.Approval)]
    public async Task<IActionResult> Get(string packageId, string version, string os, string arch, CancellationToken ct)
    {
        var (_, _, denied) = await AuthorizeAsync(packageId, ct);
        if (denied is not null) return denied;
        var (build, missing) = await FindAsync(packageId, version, os, arch, ct);
        if (missing is not null) return missing;

        return Ok(new UnpublishedReleaseResponse
        {
            Summary = ToSummary(build!),
            Manifest = Convert.ToBase64String(build!.Manifest ?? []),
            Changelog = build.Changelog,
        });
    }

    /// <summary>Publishes a pending release the caller reviewed (<see cref="ApproveReleaseRequest.ManifestSha256"/>).</summary>
    [HttpPost(ApiRoutes.ApproveRelease)]
    public async Task<IActionResult> Approve(string packageId, string version, string os, string arch,
        [FromBody] ApproveReleaseRequest request, CancellationToken ct)
    {
        var (key, _, denied) = await AuthorizeAsync(packageId, ct);
        if (denied is not null) return denied;
        var (build, missing) = await FindAsync(packageId, version, os, arch, ct);
        if (missing is not null) return missing;

        var decision = await approvals.ApproveAsync(build!.BuildId, request.ManifestSha256,
            ReleaseActor.Key(key!, HttpContext.GetClientIpAddress()), ct);
        return ToResult(decision);
    }

    /// <summary>Rejects (deletes) a pending release or a draft.</summary>
    [HttpPost(ApiRoutes.RejectRelease)]
    public async Task<IActionResult> Reject(string packageId, string version, string os, string arch,
        [FromBody] RejectReleaseRequest? request, CancellationToken ct)
    {
        var (key, _, denied) = await AuthorizeAsync(packageId, ct);
        if (denied is not null) return denied;
        var (build, missing) = await FindAsync(packageId, version, os, arch, ct);
        if (missing is not null) return missing;

        var decision = await approvals.RejectAsync(build!.BuildId, request?.ManifestSha256, request?.Reason,
            ReleaseActor.Key(key!, HttpContext.GetClientIpAddress()), ct);
        return ToResult(decision);
    }

    private IActionResult ToResult(ReleaseDecision decision) => decision.Status switch
    {
        ReleaseDecisionStatus.Done => Ok(new MessageResponse { Message = decision.Message }),
        ReleaseDecisionStatus.NotFound => NotFound(new ApiError { Error = decision.Message }),
        ReleaseDecisionStatus.SelfApproval => StatusCode(403, new ApiError { Error = decision.Message }),
        _ => Conflict(new ApiError { Error = decision.Message }),
    };

    private async Task<(UnpublishedBuild? Build, IActionResult? Missing)> FindAsync(
        string packageId, string version, string os, string arch, CancellationToken ct)
    {
        if (!PlatformMapping.TryParseOs(os, out var targetOs))
            return (null, BadRequest(new ApiError { Error = PlatformMapping.InvalidOsMessage }));
        if (!PlatformMapping.TryParseArch(arch, out var targetArch))
            return (null, BadRequest(new ApiError { Error = PlatformMapping.InvalidArchMessage }));
        var build = await approvals.GetUnpublishedAsync(packageId, version, targetOs, targetArch, ct);
        return build is null
            ? (null, NotFound(new ApiError { Error = $"No draft or pending release of {packageId} {version} {os}/{arch}" }))
            : (build, null);
    }

    private static UnpublishedReleaseSummary ToSummary(UnpublishedBuild b) => new()
    {
        Version = b.Version,
        Channel = b.Channel,
        Os = PlatformMapping.ToWire(b.OS),
        Arch = PlatformMapping.ToWire(b.Architecture),
        State = ReleaseApprovalService.ToWire(b.State),
        UploadedAt = b.UploadedAt,
        PublishAfter = b.PublishAfter,
        KeyId = b.KeyId,
        KeyLabel = b.KeyLabel,
        UploadedBy = b.UploadedByKeyName,
        ManifestSha256 = b.ManifestSha256,
        FileCount = b.FileCount,
        TotalSize = b.TotalSize,
    };

    /// <summary>
    /// The calling key and whether it may approve releases of <paramref name="packageId"/>: 401 without a
    /// key, 404 for an unknown package, 403 without the permission.
    /// </summary>
    private async Task<(ApiKey? Key, Package? Package, IActionResult? Denied)> AuthorizeAsync(string packageId, CancellationToken ct)
    {
        var claim = User.FindFirst(ClaimTypes.NameIdentifier);
        var key = claim is not null && long.TryParse(claim.Value, out var keyId)
            ? await db.ApiKeys.AsNoTracking().FirstOrDefaultAsync(k => k.Id == keyId && !k.IsRevoked, ct)
            : null;
        if (key is null)
            return (null, null, Unauthorized(new ApiError { Error = "A valid API key is required" }));

        var package = await db.Packages.AsNoTracking().FirstOrDefaultAsync(p => p.PackageId == packageId, ct);
        if (package is null)
            return (key, null, NotFound(new ApiError { Error = $"Package '{packageId}' not found" }));

        if (!ApiPermissions.Allows(key, package, ApiPermission.ApproveReleases))
            return (key, package, StatusCode(403, new ApiError { Error = "Key lacks ApproveReleases permission for this package" }));

        return (key, package, null);
    }
}
