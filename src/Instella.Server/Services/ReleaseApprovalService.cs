using System.Security.Cryptography;
using Instella.Core.Trust;
using Instella.Server.Api;
using Instella.Server.Data;
using Instella.Server.Data.Entities;
using Instella.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace Instella.Server.Services;

/// <summary>Who made a release decision, for the audit log and the self-approval rule.</summary>
/// <param name="IpAddress">The client address, <c>admin UI</c>, or <c>server</c>.</param>
/// <param name="Username">The admin user, when the decision came from the admin UI.</param>
/// <param name="ApiKeyId">The API key, when the decision came from the API.</param>
/// <param name="ApiKeyName">The API key's name.</param>
public sealed record ReleaseActor(string IpAddress, string? Username, long? ApiKeyId, string? ApiKeyName)
{
    /// <summary>An admin UI user.</summary>
    public static ReleaseActor Admin(string? username) => new("admin UI", username, null, null);

    /// <summary>An API key calling from <paramref name="ipAddress"/>.</summary>
    public static ReleaseActor Key(ApiKey key, string ipAddress) => new(ipAddress, null, key.Id, key.Name);

    /// <summary>The server itself (the delayed-release worker).</summary>
    public static readonly ReleaseActor Server = new("server", null, null, null);
}

/// <summary>The outcome of an approve or reject.</summary>
public enum ReleaseDecisionStatus
{
    /// <summary>The build was published (approve) or deleted (reject).</summary>
    Done,

    /// <summary>No such build.</summary>
    NotFound,

    /// <summary>The build is not awaiting this decision (already published, rejected, or a draft to approve).</summary>
    NotAwaiting,

    /// <summary>The build's manifest is not the one the approver reviewed.</summary>
    ManifestChanged,

    /// <summary>An API key tried to approve its own upload.</summary>
    SelfApproval,

    /// <summary>The signature no longer verifies against the package's registered publisher keys.</summary>
    SignatureRejected,
}

/// <summary>The outcome of an approve or reject, with a message for the caller.</summary>
public sealed record ReleaseDecision(ReleaseDecisionStatus Status, string Message);

/// <summary>A build clients cannot see yet: a draft or a release awaiting approval.</summary>
public sealed record UnpublishedBuild(
    long BuildId,
    string PackageId,
    string Version,
    string Channel,
    TargetOS OS,
    Architecture Architecture,
    BuildState State,
    DateTime UploadedAt,
    DateTime? PublishAfter,
    string? KeyId,
    string? KeyLabel,
    string? UploadedByKeyName,
    byte[]? Manifest,
    string? ManifestSha256,
    int FileCount,
    long TotalSize,
    string? Changelog);

/// <summary>
/// Release approval: the single place where a build becomes pending, is published by a decision or a
/// timer, or is rejected. Every decision is a conditional UPDATE on <see cref="VersionBuild.State"/>,
/// so an approver, a rejecter and the delayed-release worker racing on one build cannot both win, and
/// its audit entry is written in the same transaction. The server can only hold a release back; it
/// never changes what clients verify.
/// </summary>
public class ReleaseApprovalService(AppDbContext db, ContentStorageService contentStorage, ILogger<ReleaseApprovalService> logger)
{
    /// <summary>The shortest delay <see cref="ReleaseApproval.Delayed"/> accepts, in minutes.</summary>
    public const int MinDelayMinutes = 10;

    /// <summary>The longest delay (30 days), in minutes.</summary>
    public const int MaxDelayMinutes = 43200;

    /// <summary>The default delay (24 hours), in minutes.</summary>
    public const int DefaultDelayMinutes = 1440;

    /// <summary>
    /// The state of a new build, or of a draft being signed (<paramref name="isDraft"/> false): a draft
    /// stays a draft; otherwise the package's release approval decides.
    /// </summary>
    public static (BuildState State, DateTime? PublishAfter) InitialState(Package package, bool isDraft, DateTime now) =>
        isDraft ? (BuildState.Draft, null) : package.ReleaseApproval switch
        {
            ReleaseApproval.Delayed => (BuildState.Pending, now.AddMinutes(package.ReleaseDelayMinutes)),
            ReleaseApproval.Required => (BuildState.Pending, null),
            _ => (BuildState.Published, null),
        };

    /// <summary>Lowercase hex SHA-256 of the manifest bytes: what an approver confirms they reviewed.</summary>
    public static string ManifestSha256(byte[] manifestBytes) => Convert.ToHexStringLower(SHA256.HashData(manifestBytes));

    /// <summary>A delay for people: "24 h", "7 days", "90 min".</summary>
    public static string FormatDelay(int minutes) =>
        minutes >= 2880 && minutes % 1440 == 0 ? $"{minutes / 1440} days"
        : minutes % 60 == 0 ? $"{minutes / 60} h"
        : $"{minutes} min";

    /// <summary>The package's drafts and pending builds, oldest first.</summary>
    public async Task<List<UnpublishedBuild>> ListUnpublishedAsync(long packageDbId, CancellationToken ct = default)
    {
        var builds = await Unpublished().Where(b => b.Version.PackageId == packageDbId).OrderBy(b => b.UploadedAt).ToListAsync(ct);
        return await ToUnpublishedAsync(builds, ct);
    }

    /// <summary>One draft or pending build, or null.</summary>
    public async Task<UnpublishedBuild?> GetUnpublishedAsync(string packageId, string version, TargetOS os, Architecture arch,
        CancellationToken ct = default)
    {
        if (Instella.Core.Utilities.AppVersions.TryParse(version, out var parsed))
            version = Instella.Core.Utilities.AppVersions.ToCanonicalString(parsed);
        var build = await Unpublished().FirstOrDefaultAsync(b => b.Version.Package.PackageId == packageId
            && b.Version.VersionString == version && b.OS == os && b.Architecture == arch, ct);
        return build is null ? null : (await ToUnpublishedAsync([build], ct))[0];
    }

    /// <summary>One draft or pending build by id, or null.</summary>
    public async Task<UnpublishedBuild?> GetUnpublishedAsync(long buildId, CancellationToken ct = default)
    {
        var build = await Unpublished().FirstOrDefaultAsync(b => b.Id == buildId, ct);
        return build is null ? null : (await ToUnpublishedAsync([build], ct))[0];
    }

    /// <summary>How many builds await approval, in one package or in all.</summary>
    public Task<int> CountPendingAsync(long? packageDbId = null, CancellationToken ct = default) =>
        db.VersionBuilds.CountAsync(b => b.State == BuildState.Pending && (packageDbId == null || b.Version.PackageId == packageDbId), ct);

    /// <summary>
    /// Publishes a pending build. Refused when it is not pending, its manifest is not the one the
    /// approver reviewed (<paramref name="expectedManifestSha256"/>), an API key approves its own upload,
    /// or its signature no longer verifies against the package's registered publisher keys.
    /// </summary>
    public async Task<ReleaseDecision> ApproveAsync(long buildId, string expectedManifestSha256, ReleaseActor actor,
        CancellationToken ct = default)
    {
        var build = await LoadAsync(buildId, ct);
        if (build is null)
            return new(ReleaseDecisionStatus.NotFound, "No such build");
        var name = Describe(build);
        if (build.State != BuildState.Pending)
            return new(ReleaseDecisionStatus.NotAwaiting, $"{name} is not awaiting approval (it is {StateName(build.State)})");
        if (!ManifestMatches(build, expectedManifestSha256))
            return new(ReleaseDecisionStatus.ManifestChanged, $"{name} is not the release that was reviewed; review it again");
        if (actor.ApiKeyId is { } keyId && keyId == build.UploadedByApiKeyId)
            return new(ReleaseDecisionStatus.SelfApproval, "An API key cannot approve its own upload");
        if (await VerifyAsync(build, ct) is { } problem)
            return new(ReleaseDecisionStatus.SignatureRejected, $"{name}: {problem}; reject it");

        var now = DateTime.UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (await ClaimPublishAsync(buildId, due: null, ct) == 0)
            return new(ReleaseDecisionStatus.NotAwaiting, $"{name} is no longer awaiting approval");
        await SetReleasedAtAsync(build, now, ct);
        Audit(SecurityEventType.ReleaseApproved, actor, build,
            $"{name}, manifest {ShortHash(build)}, key {build.ReleaseKeyId}");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();

        logger.LogInformation("Approved build {BuildId} ({Name})", buildId, name);
        return new(ReleaseDecisionStatus.Done, $"Approved {name}");
    }

    /// <summary>
    /// Rejects a draft or pending build: deletes it (and its version when no build is left), so the
    /// version number can be uploaded again. When <paramref name="expectedManifestSha256"/> is given it
    /// must match. A published build is refused: deprecate or delete it instead.
    /// </summary>
    public async Task<ReleaseDecision> RejectAsync(long buildId, string? expectedManifestSha256, string? reason, ReleaseActor actor,
        CancellationToken ct = default)
    {
        var build = await LoadAsync(buildId, ct);
        if (build is null)
            return new(ReleaseDecisionStatus.NotFound, "No such build");
        var name = Describe(build);
        if (build.State == BuildState.Published)
            return new(ReleaseDecisionStatus.NotAwaiting, $"{name} is already published; deprecate or delete it instead");
        if (!string.IsNullOrEmpty(expectedManifestSha256) && !ManifestMatches(build, expectedManifestSha256))
            return new(ReleaseDecisionStatus.ManifestChanged, $"{name} is not the release that was reviewed; review it again");

        var purger = new BuildPurger(db, contentStorage);
        List<string> blobs;
        await using (await purger.LockContentAsync(db.VersionBuilds.Where(b => b.Id == buildId), ct))
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            // The claim is a write, so it holds the database's write lock until the commit: an approval
            // or the worker racing with it finds the build gone.
            var claimed = await db.VersionBuilds.Where(b => b.Id == buildId && b.State != BuildState.Published)
                .ExecuteUpdateAsync(u => u.SetProperty(b => b.PublishAfter, (DateTime?)null), ct);
            if (claimed == 0)
                return new(ReleaseDecisionStatus.NotAwaiting, $"{name} is no longer awaiting a decision");

            blobs = await purger.PurgeBuildsAsync([buildId], ct);
            await db.PackageVersions.Where(v => v.Id == build.VersionId && !v.Builds.Any()).ExecuteDeleteAsync(ct);
            var why = string.IsNullOrWhiteSpace(reason) ? "" : $"; reason: {reason.Trim()}";
            Audit(SecurityEventType.ReleaseRejected, actor, build,
                $"{name} ({StateName(build.State)}), manifest {ShortHash(build)}, key {build.ReleaseKeyId ?? "unsigned"}{why}");
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            db.ChangeTracker.Clear();
            await purger.DeleteBlobsAsync(blobs, ct);
        }

        logger.LogInformation("Rejected build {BuildId} ({Name})", buildId, name);
        return new(ReleaseDecisionStatus.Done, $"Rejected {name}; the build was deleted");
    }

    /// <summary>
    /// Publishes a pending build whose delay has ended by <paramref name="now"/>. When its signature no
    /// longer verifies (the key was removed), cancels its timer instead, so it waits for a person.
    /// Returns whether this call published it.
    /// </summary>
    public async Task<bool> AutoPublishAsync(long buildId, DateTime now, CancellationToken ct = default)
    {
        var build = await LoadAsync(buildId, ct);
        if (build is not { State: BuildState.Pending, PublishAfter: { } due } || due > now)
            return false;
        var name = Describe(build);

        if (await VerifyAsync(build, ct) is { } problem)
        {
            var held = await db.VersionBuilds
                .Where(b => b.Id == buildId && b.State == BuildState.Pending && b.PublishAfter != null)
                .ExecuteUpdateAsync(u => u.SetProperty(b => b.PublishAfter, (DateTime?)null), ct);
            if (held > 0)
            {
                Audit(SecurityEventType.ReleaseAutoPublishBlocked, ReleaseActor.Server, build, $"{name}: {problem}; waits for approval");
                await db.SaveChangesAsync(ct);
                logger.LogWarning("Did not publish delayed build {BuildId} ({Name}): {Problem}", buildId, name, problem);
            }
            return false;
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (await ClaimPublishAsync(buildId, now, ct) == 0)
            return false;
        await SetReleasedAtAsync(build, now, ct);
        Audit(SecurityEventType.ReleaseAutoPublished, ReleaseActor.Server, build,
            $"{name}, key {build.ReleaseKeyId}, delay ended {due:u}");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();

        logger.LogInformation("Published delayed build {BuildId} ({Name})", buildId, name);
        return true;
    }

    /// <summary>
    /// Changes the package's release approval. <see cref="ReleaseApproval.Delayed"/> and
    /// <see cref="ReleaseApproval.Required"/> need a registered publisher key. The change never publishes
    /// anything: loosening applies to later uploads only, and switching to Required cancels the package's
    /// running delay timers, so those builds wait for approval. Logged as
    /// <see cref="SecurityEventType.ReleaseApprovalChanged"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">No publisher key, the delay is out of range, or no such package.</exception>
    public async Task SetReleaseApprovalAsync(long packageDbId, ReleaseApproval approval, int delayMinutes, string? actor,
        CancellationToken ct = default)
    {
        if (delayMinutes is < MinDelayMinutes or > MaxDelayMinutes)
            throw new InvalidOperationException($"The delay must be between {FormatDelay(MinDelayMinutes)} and {FormatDelay(MaxDelayMinutes)}");
        var package = await db.Packages.FirstOrDefaultAsync(p => p.Id == packageDbId, ct)
            ?? throw new InvalidOperationException("No such package");
        if (approval != ReleaseApproval.Automatic && !await db.PackagePublisherKeys.AnyAsync(k => k.PackageId == packageDbId, ct))
            throw new InvalidOperationException(
                $"Release approval {approval} needs a registered publisher key, so every held release has a verified signature. Add the key first.");

        var before = Setting(package.ReleaseApproval, package.ReleaseDelayMinutes);
        var after = Setting(approval, delayMinutes);
        if (package.ReleaseApproval == approval && package.ReleaseDelayMinutes == delayMinutes)
            return;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var cancelled = 0;
        if (approval == ReleaseApproval.Required)
            cancelled = await db.VersionBuilds
                .Where(b => b.Version.PackageId == packageDbId && b.State == BuildState.Pending && b.PublishAfter != null)
                .ExecuteUpdateAsync(u => u.SetProperty(b => b.PublishAfter, (DateTime?)null), ct);
        package.ReleaseApproval = approval;
        package.ReleaseDelayMinutes = delayMinutes;
        db.SecurityEvents.Add(new SecurityEvent
        {
            EventType = SecurityEventType.ReleaseApprovalChanged,
            IpAddress = "admin UI",
            Username = actor,
            PackageId = package.PackageId,
            Details = cancelled > 0 ? $"{before} → {after}; {cancelled} pending timer(s) cancelled" : $"{before} → {after}",
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        logger.LogInformation("Release approval of {PackageId}: {Before} -> {After}", package.PackageId, before, after);
    }

    private static string Setting(ReleaseApproval approval, int delayMinutes) =>
        approval == ReleaseApproval.Delayed ? $"Delayed ({FormatDelay(delayMinutes)})" : approval.ToString();

    private IQueryable<VersionBuild> Unpublished() =>
        db.VersionBuilds.AsNoTracking()
            .Include(b => b.Version).ThenInclude(v => v.Package)
            .Include(b => b.Files)
            .Where(b => b.State != BuildState.Published);

    private async Task<List<UnpublishedBuild>> ToUnpublishedAsync(List<VersionBuild> builds, CancellationToken ct)
    {
        var packageIds = builds.Select(b => b.Version.PackageId).Distinct().ToList();
        var labels = await db.PackagePublisherKeys.AsNoTracking()
            .Where(k => packageIds.Contains(k.PackageId))
            .Select(k => new { k.PackageId, k.KeyId, k.Label })
            .ToListAsync(ct);
        return builds.Select(b => new UnpublishedBuild(
            b.Id, b.Version.Package.PackageId, b.Version.VersionString, b.Version.Channel, b.OS, b.Architecture, b.State,
            b.UploadedAt, b.PublishAfter, b.ReleaseKeyId,
            labels.FirstOrDefault(l => l.PackageId == b.Version.PackageId && l.KeyId == b.ReleaseKeyId)?.Label,
            b.UploadedByKeyName, b.ReleaseManifestBytes,
            b.ReleaseManifestBytes is { } bytes ? ManifestSha256(bytes) : null,
            b.Files.Count, b.TotalSize, b.Version.Changelog)).ToList();
    }

    private Task<VersionBuild?> LoadAsync(long buildId, CancellationToken ct) =>
        db.VersionBuilds.AsNoTracking()
            .Include(b => b.Version).ThenInclude(v => v.Package)
            .FirstOrDefaultAsync(b => b.Id == buildId, ct);

    /// <summary>
    /// Pending to published, if still pending (and, for the worker, due): 1 when this caller won.
    /// Inside the caller's transaction.
    /// </summary>
    private Task<int> ClaimPublishAsync(long buildId, DateTime? due, CancellationToken ct) =>
        db.VersionBuilds
            .Where(b => b.Id == buildId && b.State == BuildState.Pending
                        && (due == null || (b.PublishAfter != null && b.PublishAfter <= due)))
            .ExecuteUpdateAsync(u => u
                .SetProperty(b => b.State, BuildState.Published)
                .SetProperty(b => b.PublishAfter, (DateTime?)null), ct);

    /// <summary>A version is "released" when its first build is published, so "latest" and its date follow publishing.</summary>
    private async Task SetReleasedAtAsync(VersionBuild build, DateTime now, CancellationToken ct)
    {
        if (!await db.VersionBuilds.AnyAsync(b => b.VersionId == build.VersionId && b.Id != build.Id && b.State == BuildState.Published, ct))
            await db.PackageVersions.Where(v => v.Id == build.VersionId)
                .ExecuteUpdateAsync(u => u.SetProperty(v => v.ReleasedAt, now), ct);
    }

    /// <summary>Null when the stored signature verifies against a key registered now; otherwise why not.</summary>
    private async Task<string?> VerifyAsync(VersionBuild build, CancellationToken ct)
    {
        var release = DownloadController.SignedReleaseOf(build);
        if (release is null)
            return "the build is not signed";
        var keys = await db.PackagePublisherKeys.AsNoTracking()
            .Where(k => k.PackageId == build.Version.PackageId)
            .Select(k => new PublisherKey(k.KeyId, k.PublicKey))
            .ToListAsync(ct);
        if (keys.Count == 0)
            return "the package has no registered publisher key to verify it with";
        if (keys.All(k => k.KeyId != build.ReleaseKeyId))
            return $"it is signed by key {build.ReleaseKeyId}, which is no longer registered for this package";
        try
        {
            ReleaseVerifier.Verify(release, new TrustPolicy(keys, build.Version.Package.PackageId,
                PlatformMapping.ToWire(build.OS), PlatformMapping.ToWire(build.Architecture), MustBeNewerThan: null,
                MustEqual: Version.TryParse(build.Version.VersionString, out var pinned) ? pinned : null));
            return null;
        }
        catch (UpdateTrustException ex)
        {
            return $"its signature does not verify: {ex.Message}";
        }
    }

    private static bool ManifestMatches(VersionBuild build, string? expected) =>
        build.ReleaseManifestBytes is { } bytes && expected is not null
        && string.Equals(ManifestSha256(bytes), expected.Trim(), StringComparison.OrdinalIgnoreCase);

    private void Audit(SecurityEventType type, ReleaseActor actor, VersionBuild build, string details) =>
        db.SecurityEvents.Add(new SecurityEvent
        {
            EventType = type,
            IpAddress = actor.IpAddress,
            Username = actor.Username,
            ApiKeyName = actor.ApiKeyName,
            PackageId = build.Version.Package.PackageId,
            Details = details.Length > 1000 ? details[..1000] : details,
        });

    private static string Describe(VersionBuild build) =>
        $"{build.Version.VersionString} {PlatformMapping.ToWire(build.OS)}/{PlatformMapping.ToWire(build.Architecture)}";

    private static string ShortHash(VersionBuild build) =>
        build.ReleaseManifestBytes is { } bytes ? ManifestSha256(bytes)[..16] : "none";

    private static string StateName(BuildState state) => state switch
    {
        BuildState.Draft => "a draft",
        BuildState.Pending => "pending",
        _ => "published",
    };
}
