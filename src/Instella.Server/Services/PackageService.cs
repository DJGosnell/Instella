using Instella.Core.Wire;
using Instella.Server.Models;
using Instella.Server.Data;
using Instella.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Instella.Server.Services;

public class PackageService(AppDbContext db, ContentStorageService contentStorage)
{
    private readonly BuildPurger _purger = new(db, contentStorage);

    // Package operations
    public async Task<List<Package>> GetAllPackagesAsync(CancellationToken ct = default)
    {
        return await db.Packages
            .Include(p => p.Versions)
            .ThenInclude(v => v.Builds)
            .OrderBy(p => p.DisplayName)
            .ToListAsync(ct);
    }

    public async Task<Package?> GetPackageAsync(string packageId, CancellationToken ct = default)
    {
        return await db.Packages
            .Include(p => p.Versions)
            .ThenInclude(v => v.Builds)
            .FirstOrDefaultAsync(p => p.PackageId == packageId, ct);
    }

    public async Task<Package?> GetPackageByIdAsync(long id, CancellationToken ct = default)
    {
        return await db.Packages
            .Include(p => p.Versions)
            .ThenInclude(v => v.Builds)
            .FirstOrDefaultAsync(p => p.Id == id, ct);
    }

    public async Task<Package> CreatePackageAsync(
        string packageId,
        string displayName,
        string description = "",
        DownloadAccessMode downloadAccessMode = DownloadAccessMode.Open,
        CancellationToken ct = default)
    {
        var package = new Package
        {
            PackageId = packageId,
            DisplayName = displayName,
            Description = description,
            DownloadAccessMode = downloadAccessMode
        };

        db.Packages.Add(package);
        await db.SaveChangesAsync(ct);
        return package;
    }

    public async Task<Package?> UpdatePackageAsync(
        long id,
        string? displayName = null,
        string? description = null,
        DownloadAccessMode? downloadAccessMode = null,
        CancellationToken ct = default)
    {
        var package = await db.Packages.FindAsync([id], ct);
        if (package == null) return null;

        if (displayName != null) package.DisplayName = displayName;
        if (description != null) package.Description = description;
        if (downloadAccessMode.HasValue) package.DownloadAccessMode = downloadAccessMode.Value;

        await db.SaveChangesAsync(ct);
        return package;
    }

    // Publisher keys (optional upload-time signature enforcement; see PackagePublisherKey)
    public async Task<List<PackagePublisherKey>> GetPublisherKeysAsync(long packageId, CancellationToken ct = default)
    {
        return await db.PackagePublisherKeys
            .Where(k => k.PackageId == packageId)
            .OrderBy(k => k.AddedAt)
            .ToListAsync(ct);
    }

    /// <summary>Registers a publisher key for a package; logged as <see cref="SecurityEventType.PublisherKeyAdded"/>.</summary>
    /// <exception cref="ArgumentException">The value is not a base64 ECDSA P-256 public key.</exception>
    /// <exception cref="InvalidOperationException">The key is already registered for the package.</exception>
    public async Task<PackagePublisherKey> AddPublisherKeyAsync(
        long packageId, string publicKeyBase64, string? label, string? actor = null, CancellationToken ct = default)
    {
        var key = Instella.Core.Trust.KeyIds.FromPublicKey(publicKeyBase64);
        if (await db.PackagePublisherKeys.AnyAsync(k => k.PackageId == packageId && k.KeyId == key.KeyId, ct))
            throw new InvalidOperationException($"Key {key.KeyId} is already registered for this package.");

        var entity = new PackagePublisherKey
        {
            PackageId = packageId,
            KeyId = key.KeyId,
            PublicKey = key.PublicKey,
            Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim()
        };
        db.PackagePublisherKeys.Add(entity);
        await LogKeyChangeAsync(SecurityEventType.PublisherKeyAdded, entity, actor, ct);
        await db.SaveChangesAsync(ct);
        return entity;
    }

    /// <summary>
    /// Removes a publisher key; logged as <see cref="SecurityEventType.PublisherKeyRemoved"/>. Releases
    /// already published are not affected; pending ones signed by it can then only be rejected.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// It is the package's last key while its release approval is not Automatic or a release is pending:
    /// held releases must keep a key to verify against.
    /// </exception>
    public async Task<bool> RemovePublisherKeyAsync(long keyId, string? actor = null, CancellationToken ct = default)
    {
        var key = await db.PackagePublisherKeys.FindAsync([keyId], ct);
        if (key == null) return false;
        if (!await db.PackagePublisherKeys.AnyAsync(k => k.PackageId == key.PackageId && k.Id != key.Id, ct))
        {
            var approval = await db.Packages.Where(p => p.Id == key.PackageId).Select(p => p.ReleaseApproval).FirstAsync(ct);
            if (approval != ReleaseApproval.Automatic)
                throw new InvalidOperationException(
                    $"This is the package's last publisher key and its release approval is {approval}. Add another key, or set release approval to Automatic, first.");
            if (await db.VersionBuilds.AnyAsync(b => b.Version.PackageId == key.PackageId && b.State == BuildState.Pending, ct))
                throw new InvalidOperationException(
                    "This is the package's last publisher key and releases are pending approval. Approve or reject them first.");
        }
        db.PackagePublisherKeys.Remove(key);
        await LogKeyChangeAsync(SecurityEventType.PublisherKeyRemoved, key, actor, ct);
        await db.SaveChangesAsync(ct);
        return true;
    }

    private async Task LogKeyChangeAsync(SecurityEventType type, PackagePublisherKey key, string? actor, CancellationToken ct)
    {
        var packageId = await db.Packages.Where(p => p.Id == key.PackageId).Select(p => p.PackageId).FirstAsync(ct);
        db.SecurityEvents.Add(new SecurityEvent
        {
            EventType = type,
            IpAddress = "admin UI",
            Username = actor,
            PackageId = packageId,
            Details = key.Label is null ? key.KeyId : $"{key.KeyId} ({key.Label})",
        });
    }

    public async Task<bool> DeletePackageAsync(long id, CancellationToken ct = default)
    {
        // The content locks are taken before the transaction and held until the blobs are gone.
        await using var locks = await _purger.LockContentAsync(db.VersionBuilds.Where(b => b.Version.PackageId == id), ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.Packages.AnyAsync(p => p.Id == id, ct)) return false;

        var buildIds = await db.VersionBuilds.Where(b => b.Version.PackageId == id).Select(b => b.Id).ToListAsync(ct);
        var blobs = await _purger.PurgeBuildsAsync(buildIds, ct);
        await db.PackageVersions.Where(v => v.PackageId == id).ExecuteDeleteAsync(ct);
        await db.Packages.Where(p => p.Id == id).ExecuteDeleteAsync(ct);   // keys and publisher keys cascade
        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();

        await _purger.DeleteBlobsAsync(blobs, ct);
        return true;
    }

    // Version operations
    public async Task<List<PackageVersion>> GetVersionsAsync(string packageId, CancellationToken ct = default)
    {
        return await db.PackageVersions
            .Include(v => v.Builds)
            .ThenInclude(b => b.Installers)
            .Where(v => v.Package.PackageId == packageId)
            .OrderByDescending(v => v.VersionKey)
            .ToListAsync(ct);
    }

    public async Task<PackageVersion?> GetVersionAsync(string packageId, string versionString, CancellationToken ct = default)
    {
        // Versions are stored canonically: "1.3" names 1.3.0.
        if (Instella.Core.Utilities.AppVersions.TryParse(versionString, out var parsed))
            versionString = Instella.Core.Utilities.AppVersions.ToCanonicalString(parsed);
        return await db.PackageVersions
            .Include(v => v.Builds)
            .ThenInclude(b => b.Files)
            .Include(v => v.Package)
            .FirstOrDefaultAsync(v => v.Package.PackageId == packageId && v.VersionString == versionString, ct);
    }

    /// <summary>
    /// The versions "latest" is chosen from: on <paramref name="channel"/>, not deprecated,
    /// with a published (not draft or pending) build for <paramref name="os"/>/<paramref name="arch"/> when given.
    /// </summary>
    private IQueryable<PackageVersion> Releasable(long packageDbId, string channel, TargetOS? os, Architecture? arch) =>
        db.PackageVersions.Where(v => v.PackageId == packageDbId && v.Channel == channel && !v.IsDeprecated
            && v.Builds.Any(b => b.State == BuildState.Published && (os == null || b.OS == os) && (arch == null || b.Architecture == arch)));

    /// <summary>
    /// "Latest" on a channel: the highest version (by <see cref="PackageVersion.VersionKey"/>,
    /// never by release date) that is <see cref="Releasable"/>, and at or below the channel's pin
    /// when one is set. A platform without a build at the pin falls back to the highest lower version
    /// that has one. Returns the version with its package and builds, or null.
    /// </summary>
    public async Task<PackageVersion?> GetLatestVersionAsync(long packageDbId, string channel,
        TargetOS? os = null, Architecture? arch = null, CancellationToken ct = default)
    {
        var query = Releasable(packageDbId, channel, os, arch);
        var ceiling = await PinCeilingAsync(packageDbId, channel, ct);
        if (ceiling is not null)
            query = query.Where(v => string.Compare(v.VersionKey, ceiling) <= 0);   // SQLite: text comparison
        return await query.OrderByDescending(v => v.VersionKey)
            .Include(v => v.Package)
            .Include(v => v.Builds).ThenInclude(b => b.Files)
            .FirstOrDefaultAsync(ct);
    }

    /// <inheritdoc cref="GetLatestVersionAsync(long, string, TargetOS?, Architecture?, CancellationToken)"/>
    public async Task<PackageVersion?> GetLatestVersionAsync(string packageId, string channel,
        TargetOS? os = null, Architecture? arch = null, CancellationToken ct = default)
    {
        var id = await db.Packages.Where(p => p.PackageId == packageId).Select(p => (long?)p.Id).FirstOrDefaultAsync(ct);
        return id is null ? null : await GetLatestVersionAsync(id.Value, channel, os, arch, ct);
    }

    /// <summary>The channel's pinned version key (the "latest" ceiling), or null when uncapped.</summary>
    public Task<string?> PinCeilingAsync(long packageDbId, string channel, CancellationToken ct = default) =>
        db.PackageChannels
            .Where(c => c.PackageId == packageDbId && c.Name == channel && c.PinnedVersionId != null)
            .Select(c => c.PinnedVersion!.VersionKey)
            .FirstOrDefaultAsync(ct);

    /// <summary>The package's channels, with their pinned version, stable first then by name.</summary>
    public async Task<List<PackageChannel>> GetChannelsAsync(long packageDbId, CancellationToken ct = default)
    {
        var channels = await db.PackageChannels.AsNoTracking()
            .Include(c => c.PinnedVersion)
            .Where(c => c.PackageId == packageDbId)
            .ToListAsync(ct);
        return channels.OrderBy(c => c.Name == Instella.Core.Wire.ChannelNames.Stable ? 0 : 1)
            .ThenBy(c => c.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Caps "latest" on <paramref name="channel"/> at <paramref name="version"/> (null removes the
    /// cap). The version must exist on that channel. Recorded as a <see cref="SecurityEventType.ChannelPinChanged"/>
    /// security event: a pin decides what every installed app updates to.
    /// </summary>
    /// <exception cref="InvalidOperationException">No such channel, or the version is not on it.</exception>
    public async Task SetChannelPinAsync(long packageDbId, string channel, string? version, string? actor = null,
        CancellationToken ct = default)
    {
        var entry = await db.PackageChannels.Include(c => c.Package)
            .FirstOrDefaultAsync(c => c.PackageId == packageDbId && c.Name == channel, ct)
            ?? throw new InvalidOperationException($"The package has no channel '{channel}'");
        long? pinnedId = null;
        if (version is not null)
        {
            if (!Instella.Core.Utilities.AppVersions.TryParse(version, out var parsed))
                throw new InvalidOperationException($"'{version}' is not a version");
            var canonical = Instella.Core.Utilities.AppVersions.ToCanonicalString(parsed);
            pinnedId = await db.PackageVersions
                .Where(v => v.PackageId == packageDbId && v.Channel == channel && v.VersionString == canonical)
                .Select(v => (long?)v.Id).FirstOrDefaultAsync(ct)
                ?? throw new InvalidOperationException($"Version {canonical} is not on channel '{channel}'");
            version = canonical;
        }
        entry.PinnedVersionId = pinnedId;
        db.SecurityEvents.Add(new SecurityEvent
        {
            EventType = SecurityEventType.ChannelPinChanged,
            IpAddress = "admin UI",
            Username = actor,
            PackageId = entry.Package.PackageId,
            Details = version is null ? $"channel '{channel}': cap removed" : $"channel '{channel}': latest capped at {version}",
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task<PackageVersion?> UpdateVersionAsync(long id, string? changelog = null, bool? isDeprecated = null, CancellationToken ct = default)
    {
        var version = await db.PackageVersions.FindAsync([id], ct);
        if (version == null) return null;

        if (changelog != null) version.Changelog = changelog;
        if (isDeprecated.HasValue) version.IsDeprecated = isDeprecated.Value;

        await db.SaveChangesAsync(ct);
        return version;
    }

    /// <summary>
    /// Deletes a version and its builds in one transaction, in dependency order, with reference
    /// counts adjusted in bulk SQL. Blobs are deleted after the commit, because
    /// storage is not transactional; a failed blob delete is reconciled by the orphan sweeper.
    /// </summary>
    public async Task<bool> DeleteVersionAsync(long id, CancellationToken ct = default)
    {
        await using var locks = await _purger.LockContentAsync(db.VersionBuilds.Where(b => b.VersionId == id), ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.PackageVersions.AnyAsync(v => v.Id == id, ct)) return false;

        var buildIds = await db.VersionBuilds.Where(b => b.VersionId == id).Select(b => b.Id).ToListAsync(ct);
        var blobs = await _purger.PurgeBuildsAsync(buildIds, ct);
        await db.PackageVersions.Where(v => v.Id == id).ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();

        await _purger.DeleteBlobsAsync(blobs, ct);
        return true;
    }

    // Build operations
    /// <summary>One published build; a draft or pending one only with <paramref name="includeUnpublished"/>.</summary>
    public async Task<VersionBuild?> GetBuildAsync(string packageId, string versionString, TargetOS os, Architecture arch,
        CancellationToken ct = default, bool includeUnpublished = false)
    {
        return await db.VersionBuilds
            .Include(b => b.Version)
            .ThenInclude(v => v.Package)
            .Include(b => b.Files)
            .ThenInclude(f => f.StoredFile)
            .FirstOrDefaultAsync(b =>
                b.Version.Package.PackageId == packageId &&
                b.Version.VersionString == versionString &&
                b.OS == os &&
                b.Architecture == arch &&
                (includeUnpublished || b.State == BuildState.Published), ct);
    }

    public async Task<VersionBuild?> GetBuildByIdAsync(long id, CancellationToken ct = default)
    {
        return await db.VersionBuilds
            .Include(b => b.Version)
            .ThenInclude(v => v.Package)
            .Include(b => b.Files)
            .ThenInclude(f => f.StoredFile)
            .FirstOrDefaultAsync(b => b.Id == id, ct);
    }

    public async Task<bool> DeleteBuildAsync(long id, CancellationToken ct = default)
    {
        await using var locks = await _purger.LockContentAsync(db.VersionBuilds.Where(b => b.Id == id), ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.VersionBuilds.AnyAsync(b => b.Id == id, ct)) return false;

        var blobs = await _purger.PurgeBuildsAsync([id], ct);
        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();

        await _purger.DeleteBlobsAsync(blobs, ct);
        return true;
    }

    public async Task<bool> BuildExistsAsync(string packageId, string versionString, TargetOS os, Architecture arch, CancellationToken ct = default)
    {
        return await db.VersionBuilds.AnyAsync(b =>
            b.Version.Package.PackageId == packageId &&
            b.Version.VersionString == versionString &&
            b.OS == os &&
            b.Architecture == arch, ct);
    }

    /// <summary>
    /// The installer of <paramref name="kind"/> for one build, with its package. A
    /// <paramref name="versionString"/> of <c>latest</c> means the newest non-deprecated version
    /// on <paramref name="channel"/> that has a build for the platform; that build must carry
    /// the installer (an older version's installer is never substituted). Null when none.
    /// </summary>
    public async Task<BuildInstaller?> GetInstallerAsync(
        string packageId, string versionString, TargetOS os, Architecture arch, string kind, string channel,
        CancellationToken ct = default)
    {
        var builds = db.VersionBuilds.Where(b =>
            b.Version.Package.PackageId == packageId && b.OS == os && b.Architecture == arch && b.State == BuildState.Published);
        if (versionString == ApiRoutes.LatestVersion)
        {
            // The same "latest" as check-update, including the channel's pin.
            var latest = await GetLatestVersionAsync(packageId, channel, os, arch, ct);
            if (latest is null) return null;
            versionString = latest.VersionString;
        }
        else if (Instella.Core.Utilities.AppVersions.TryParse(versionString, out var parsed))
        {
            versionString = Instella.Core.Utilities.AppVersions.ToCanonicalString(parsed);
        }
        builds = builds.Where(b => b.Version.VersionString == versionString);

        return await db.BuildInstallers
            .Include(i => i.Build).ThenInclude(b => b.Version).ThenInclude(v => v.Package)
            .Where(i => i.Kind == kind && builds.Select(b => b.Id).Contains(i.BuildId))
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Counts one download of an installer.</summary>
    public Task IncrementInstallerDownloadCountAsync(long installerId, CancellationToken ct = default) =>
        db.BuildInstallers.Where(i => i.Id == installerId)
            .ExecuteUpdateAsync(u => u.SetProperty(i => i.DownloadCount, i => i.DownloadCount + 1), ct);

    // Statistics
    public async Task IncrementDownloadCountAsync(long buildId, bool isPatch, string? ipHash = null, string? userAgent = null, CancellationToken ct = default)
    {
        var build = await db.VersionBuilds.FirstOrDefaultAsync(b => b.Id == buildId, ct);
        if (build == null) return;

        if (isPatch)
            build.PatchDownloadCount++;
        else
            build.DownloadCount++;

        db.DownloadLogs.Add(new DownloadLog
        {
            BuildId = buildId,
            IsPatch = isPatch,
            IPHash = ipHash ?? "",
            UserAgent = userAgent
        });

        await db.SaveChangesAsync(ct);
    }

    // Paginated grid operations
    public async Task<PagedResult<PackageGridItem>> GetPackagesPagedAsync(
        PackageQueryParams query,
        CancellationToken ct = default)
    {
        query.Normalize();

        var baseQuery = db.Packages
            .Include(p => p.Versions)
            .ThenInclude(v => v.Builds)
            .AsQueryable();

        if (!string.IsNullOrEmpty(query.SearchTerm))
        {
            baseQuery = baseQuery.Where(p => p.DisplayName.Contains(query.SearchTerm));
        }

        var totalCount = await baseQuery.CountAsync(ct);
        var packages = await baseQuery.ToListAsync(ct);

        IEnumerable<Package> sortedPackages = query.SortColumn switch
        {
            "Released" => query.SortAscending
                ? packages.OrderBy(p => p.Versions.Any() ? p.Versions.Max(v => v.ReleasedAt) : DateTime.MinValue)
                : packages.OrderByDescending(p => p.Versions.Any() ? p.Versions.Max(v => v.ReleasedAt) : DateTime.MinValue),
            _ => query.SortAscending
                ? packages.OrderBy(p => p.DisplayName)
                : packages.OrderByDescending(p => p.DisplayName)
        };

        var pagedPackages = sortedPackages
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToList();

        var items = pagedPackages.Select(MapToGridItem).ToList();

        return new PagedResult<PackageGridItem>
        {
            Items = items,
            TotalCount = totalCount,
            Page = query.Page,
            PageSize = query.PageSize
        };
    }

    public async Task<PackageVersion?> GetVersionByIdAsync(long id, CancellationToken ct = default)
    {
        return await db.PackageVersions
            .Include(v => v.Package)
            .Include(v => v.Builds)
            .ThenInclude(b => b.Files)
            .FirstOrDefaultAsync(v => v.Id == id, ct);
    }

    private static PackageGridItem MapToGridItem(Package package)
    {
        return new PackageGridItem
        {
            Id = package.Id,
            PackageId = package.PackageId,
            DisplayName = package.DisplayName,
            VersionCount = package.Versions.Count,
            BuildCount = package.Versions.Sum(v => v.Builds.Count),
            TotalDownloads = package.Versions.SelectMany(v => v.Builds).Sum(b => b.DownloadCount),
            TotalSize = package.Versions.SelectMany(v => v.Builds).Sum(b => b.TotalSize),
            LatestReleaseDate = package.Versions.Any()
                ? package.Versions.Max(v => v.ReleasedAt)
                : null,
            Versions = package.Versions
                .OrderByDescending(v => v.VersionKey, StringComparer.Ordinal)
                .Select(v => MapToVersionGridItem(v, package.PackageId))
                .ToList()
        };
    }

    private static VersionGridItem MapToVersionGridItem(PackageVersion version, string packageId)
    {
        return new VersionGridItem
        {
            Id = version.Id,
            PackageId = version.PackageId,
            VersionString = version.VersionString,
            Channel = version.Channel,
            Downloads = version.Builds.Sum(b => b.DownloadCount),
            TotalSize = version.Builds.Sum(b => b.TotalSize),
            ReleasedAt = version.ReleasedAt,
            IsDeprecated = version.IsDeprecated,
            Builds = version.Builds
                .OrderBy(b => b.OS)
                .ThenBy(b => b.Architecture)
                .Select(b => MapToBuildGridItem(b, packageId, version.VersionString))
                .ToList()
        };
    }

    private static BuildGridItem MapToBuildGridItem(VersionBuild build, string packageId, string versionString)
    {
        return new BuildGridItem
        {
            Id = build.Id,
            VersionId = build.VersionId,
            OS = build.OS,
            Architecture = build.Architecture,
            FileSize = build.TotalSize,
            Downloads = build.DownloadCount,
            FileCount = build.Files.Count,
            // Relative to the site root; the admin UI navigates to it directly.
            DownloadPath = Api.SiteLinks.DownloadBuild(packageId, versionString, build.OS, build.Architecture)
        };
    }
}
