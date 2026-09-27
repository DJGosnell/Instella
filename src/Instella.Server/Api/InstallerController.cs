using Instella.Core.Wire;
using Instella.Server.Auth;
using Instella.Server.Data.Entities;
using Instella.Server.Models;
using Instella.Server.Services;
using Instella.Server.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Instella.Server.Api;

/// <summary>
/// Installer downloads: the online and offline installers the publisher uploaded with a build,
/// for one version or the newest (<c>latest</c>). Same access rule as downloading the build.
/// </summary>
[ApiController]
[Route(ApiRoutes.Prefix)]
[EnableRateLimiting(DownloadRateLimit.Policy)]
public class InstallerController(
    PackageService packageService,
    ContentStorageService contentStorage,
    StorageSettingsService storageSettingsService,
    DownloadAccess downloadAccess) : ControllerBase
{
    /// <summary>Download an installer; <c>{version}</c> may be <c>latest</c> (<c>?channel=</c>, default stable).</summary>
    [HttpGet(ApiRoutes.DownloadInstaller)]
    public async Task<IActionResult> DownloadInstaller(
        string packageId,
        string version,
        string os,
        string arch,
        string kind,
        [FromQuery] string? channel,
        CancellationToken ct)
    {
        if (!PlatformMapping.TryParseOs(os, out var targetOs))
            return BadRequest(new ApiError { Error = PlatformMapping.InvalidOsMessage });
        if (!PlatformMapping.TryParseArch(arch, out var targetArch))
            return BadRequest(new ApiError { Error = PlatformMapping.InvalidArchMessage });
        if (!InstallerKinds.IsValid(kind))
            return BadRequest(new ApiError { Error = $"Unknown installer kind '{kind}' (expected '{InstallerKinds.Online}' or '{InstallerKinds.Offline}')" });
        var targetChannel = ChannelNames.Stable;
        if (channel != null && !ChannelNames.TryNormalize(channel, out targetChannel))
            return BadRequest(new ApiError { Error = ChannelNames.Rule });

        var installer = await packageService.GetInstallerAsync(packageId, version, targetOs, targetArch, kind, targetChannel, ct);
        var unknown = NotFound(new ApiError { Error = version == ApiRoutes.LatestVersion
            ? $"The latest {os}/{arch} version has no {kind} installer"
            : $"No {kind} installer for {packageId} {version} {os}/{arch}" });
        if (installer is null)
            return unknown;

        // Without valid credentials a private package's installer looks like a missing one.
        if (await downloadAccess.CheckAsync(HttpContext, installer.Build.Version.Package, ct, unknown) is { } denied)
            return denied;

        await packageService.IncrementInstallerDownloadCountAsync(installer.Id, ct);

        var settings = await storageSettingsService.GetSettingsAsync();
        if (settings.StorageProvider == StorageProviderType.S3)
        {
            var expiry = TimeSpan.FromMinutes(settings.S3UrlExpiryMinutes > 0 ? settings.S3UrlExpiryMinutes : 60);
            var url = await contentStorage.GetPresignedUrlAsync(installer.ContentHash, expiry, installer.FileName, ct);
            if (url != null)
                return Redirect(url);
        }

        var stream = await contentStorage.RetrieveAsync(installer.ContentHash, ct);
        if (stream == null)
            return NotFound(new ApiError { Error = "Installer not found in storage" });
        return File(stream, "application/octet-stream", installer.FileName);
    }
}
