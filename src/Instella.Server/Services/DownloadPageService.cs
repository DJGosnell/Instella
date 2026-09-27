using Instella.Core.Wire;
using Instella.Server.Api;
using Instella.Server.Data;
using Instella.Server.Data.Entities;
using Instella.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace Instella.Server.Services;

/// <summary>What the public download page shows for one package and channel.</summary>
/// <param name="DisplayName">Package display name.</param>
/// <param name="Description">Package description.</param>
/// <param name="Channel">The channel shown (lower case).</param>
/// <param name="Versions">Non-deprecated versions on the channel that have an installer, newest first.</param>
public sealed record DownloadPageModel(string DisplayName, string Description, string Channel, IReadOnlyList<DownloadPageVersion> Versions);

/// <summary>One version on the download page.</summary>
public sealed record DownloadPageVersion(string Version, DateTime ReleasedAt, string Changelog, IReadOnlyList<DownloadPageInstaller> Installers);

/// <summary>One installer link. <see cref="Url"/> is site-relative.</summary>
public sealed record DownloadPageInstaller(string Os, string Arch, string Kind, string FileName, long Size, string Url);

/// <summary>
/// Builds the public download page (<c>/download/{packageId}</c>). Only packages with open
/// download access have one: a private package's page is indistinguishable from an unknown
/// package, like the package list. The newest version's links use <c>latest</c>, so a copied
/// link keeps pointing at the newest installer.
/// </summary>
public sealed class DownloadPageService(AppDbContext db)
{
    /// <summary>
    /// The page model, or null when there is no public page for <paramref name="packageId"/> on
    /// <paramref name="channel"/>: an invalid channel name, or one the package does not have,
    /// looks like an unknown package.
    /// </summary>
    public async Task<DownloadPageModel?> GetAsync(string packageId, string channel, CancellationToken ct = default)
    {
        if (!ChannelNames.TryNormalize(channel, out var name)) return null;
        channel = name;
        var package = await db.Packages.AsNoTracking()
            .FirstOrDefaultAsync(p => p.PackageId == packageId && p.DownloadAccessMode == DownloadAccessMode.Open, ct);
        if (package is null) return null;
        if (channel != ChannelNames.Stable
            && !await db.PackageChannels.AnyAsync(c => c.PackageId == package.Id && c.Name == channel, ct))
            return null;

        var versions = await db.PackageVersions.AsNoTracking()
            .Include(v => v.Builds).ThenInclude(b => b.Installers)
            .Where(v => v.PackageId == package.Id && !v.IsDeprecated && v.Channel == channel)
            .OrderByDescending(v => v.VersionKey)
            .ToListAsync(ct);
        // The channel's pin caps "latest": versions above it are listed but not linked as latest.
        var ceiling = await db.PackageChannels
            .Where(c => c.PackageId == package.Id && c.Name == channel && c.PinnedVersionId != null)
            .Select(c => c.PinnedVersion!.VersionKey)
            .FirstOrDefaultAsync(ct);

        // "latest" is per platform: the highest version (at or below the pin) that has a build for it.
        var newestPerPlatform = new HashSet<(TargetOS, Architecture)>();
        var result = new List<DownloadPageVersion>();
        foreach (var version in versions)
        {
            var installers = new List<DownloadPageInstaller>();
            foreach (var build in version.Builds.Where(b => !b.IsDraft).OrderBy(b => b.OS).ThenBy(b => b.Architecture))
            {
                var isNewest = (ceiling is null || string.CompareOrdinal(version.VersionKey, ceiling) <= 0)
                               && newestPerPlatform.Add((build.OS, build.Architecture));
                foreach (var installer in build.Installers.OrderBy(i => i.Kind, StringComparer.Ordinal))
                {
                    var url = SiteLinks.Installer(package.PackageId, isNewest ? ApiRoutes.LatestVersion : version.VersionString,
                        build.OS, build.Architecture, installer.Kind);
                    if (isNewest && channel != ChannelNames.Stable) url += "?channel=" + channel;
                    installers.Add(new DownloadPageInstaller(PlatformMapping.ToWire(build.OS), PlatformMapping.ToWire(build.Architecture),
                        installer.Kind, installer.FileName, installer.Size, url));
                }
            }
            if (installers.Count > 0)
                result.Add(new DownloadPageVersion(version.VersionString, version.ReleasedAt, version.Changelog, installers));
        }

        return new DownloadPageModel(package.DisplayName, package.Description, channel, result);
    }
}
