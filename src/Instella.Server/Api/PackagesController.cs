using Instella.Core.Wire;
using Instella.Server.Auth;
using Instella.Server.Data.Entities;
using Instella.Server.Models;
using Instella.Server.Services;
using Microsoft.AspNetCore.Mvc;

namespace Instella.Server.Api;

[ApiController]
[Route(ApiRoutes.Prefix)]
[Microsoft.AspNetCore.RateLimiting.EnableRateLimiting(ApiRateLimit.Policy)]
public class PackagesController(PackageService packageService, DownloadAccess downloadAccess) : ControllerBase
{
    /// <summary>
    /// List the open-access packages. Private packages are never listed, so their ids stay private.
    /// </summary>
    [HttpGet(ApiRoutes.Packages)]
    public async Task<ActionResult<PackageSummary[]>> GetPackages(CancellationToken ct)
    {
        var packages = await packageService.GetAllPackagesAsync(ct);
        var summaries = new List<PackageSummary>();
        foreach (var p in packages.Where(p => p.DownloadAccessMode == DownloadAccessMode.Open))
            summaries.Add(await ToSummaryAsync(p, ct));
        return summaries.ToArray();
    }

    /// <summary>
    /// One package, under the same access rule as downloading it.
    /// </summary>
    [HttpGet(ApiRoutes.Package)]
    public async Task<ActionResult<PackageSummary>> GetPackage(string packageId, CancellationToken ct)
    {
        var package = await packageService.GetPackageAsync(packageId, ct);
        if (package == null)
            return NotFound(new ApiError { Error = "Package not found" });
        if (await downloadAccess.CheckAsync(HttpContext, package, ct) is { } denied)
            return (ActionResult)denied;

        return await ToSummaryAsync(package, ct);
    }

    /// <summary>
    /// Versions of a package, under the same access rule as downloading it.
    /// </summary>
    [HttpGet(ApiRoutes.PackageVersions)]
    public async Task<ActionResult<VersionSummary[]>> GetVersions(string packageId, CancellationToken ct)
    {
        var package = await packageService.GetPackageAsync(packageId, ct);
        if (package == null)
            return NotFound(new ApiError { Error = "Package not found" });

        if (await downloadAccess.CheckAsync(HttpContext, package, ct) is { } denied)
            return (ActionResult)denied;

        // Drafts are invisible to clients: a version shows only its published builds, and a
        // version with none is not listed.
        var versions = (await packageService.GetVersionsAsync(packageId, ct))
            .Where(v => v.Builds.Any(b => !b.IsDraft))
            .ToList();
        foreach (var v in versions)
            v.Builds = v.Builds.Where(b => !b.IsDraft).ToList();
        return versions.Select(v => new VersionSummary
        {
            VersionString = v.VersionString,
            Channel = v.Channel,
            Changelog = v.Changelog,
            ReleasedAt = v.ReleasedAt,
            IsDeprecated = v.IsDeprecated,
            DownloadCount = v.Builds.Sum(b => b.DownloadCount),
            Builds = v.Builds.Select(b => new BuildSummary
            {
                Os = PlatformMapping.ToWire(b.OS),
                Arch = PlatformMapping.ToWire(b.Architecture),
                FileSize = b.TotalSize,
                FileCount = b.Files.Count,
                Installers = b.Installers
                    .OrderBy(i => i.Kind, StringComparer.Ordinal)
                    .Select(i => new InstallerSummary { Kind = i.Kind, FileName = i.FileName, Size = i.Size })
                    .ToList(),
            }).ToList(),
        }).ToArray();
    }

    /// <summary>
    /// <c>LatestVersion</c> is "latest" on stable for any platform (the pin included);
    /// <c>VersionCount</c> counts the versions a client can see: those with a published build.
    /// </summary>
    private async Task<PackageSummary> ToSummaryAsync(Package p, CancellationToken ct) => new()
    {
        PackageId = p.PackageId,
        DisplayName = p.DisplayName,
        Description = p.Description,
        VersionCount = p.Versions.Count(v => v.Builds.Any(b => !b.IsDraft)),
        LatestVersion = (await packageService.GetLatestVersionAsync(p.Id, ChannelNames.Stable, ct: ct))?.VersionString,
    };
}
