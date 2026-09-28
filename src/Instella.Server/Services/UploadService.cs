using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Instella.Server.Models;
using Instella.Core.FileSystem;
using Instella.Core.Trust;
using Instella.Core.Wire;
using Instella.Server.Data;
using Instella.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Instella.Server.Services;

/// <summary>
/// Session-based uploads into content-addressed storage. Sessions and their files are durable
/// rows; uploaded content is counted only when the session completes, in the same
/// transaction that creates the build, so a crash or cancel never leaks reference counts.
/// </summary>
public class UploadService(
    AppDbContext db,
    ContentStorageService contentStorage,
    ILogger<UploadService> logger,
    IOptions<UploadLimits>? limits = null)
{
    /// <summary>How long an idle session stays open.</summary>
    public static readonly TimeSpan SessionTimeout = TimeSpan.FromHours(2);

    private UploadLimits Limits => limits?.Value ?? new UploadLimits();

    /// <summary>Starts a new upload session (not tied to a key; used by tests and tools).</summary>
    public Task<UploadSession> StartSessionAsync(
        string packageId, string version, string channel, TargetOS os, Architecture arch, CancellationToken ct = default) =>
        StartSessionAsync(packageId, version, channel, os, arch, apiKeyId: null, ct);

    /// <summary>Starts a new upload session owned by <paramref name="apiKeyId"/>.</summary>
    public async Task<UploadSession> StartSessionAsync(
        string packageId, string version, string channel, TargetOS os, Architecture arch, long? apiKeyId,
        CancellationToken ct = default)
    {
        // Validated before anything else. Versions are stored in one canonical form: "1.2" is
        // "1.2.0", and "latest" or "1.2-beta" is not a version.
        if (!Instella.Core.Utilities.AppVersions.TryParse(version, out var parsedVersion))
            throw new InvalidOperationException($"invalid version '{version}': use numbers like 1.2.3 or 1.2.3.4");
        version = Instella.Core.Utilities.AppVersions.ToCanonicalString(parsedVersion);
        // Channels are free names with one rule; "Beta" is stored as "beta".
        if (!ChannelNames.TryNormalize(channel, out var normalizedChannel))
            throw new InvalidOperationException($"Invalid channel '{channel}': {ChannelNames.Rule}");
        channel = normalizedChannel;

        var package = await db.Packages.FirstOrDefaultAsync(p => p.PackageId == packageId, ct)
            ?? throw new InvalidOperationException($"Package '{packageId}' not found");

        var existingChannel = await db.PackageVersions
            .Where(v => v.PackageId == package.Id && v.VersionString == version)
            .Select(v => v.Channel).FirstOrDefaultAsync(ct);
        if (existingChannel is not null && existingChannel != channel)
            throw ChannelConflict(version, existingChannel);

        var exists = await db.VersionBuilds.AnyAsync(b =>
            b.Version.Package.PackageId == packageId && b.Version.VersionString == version &&
            b.OS == os && b.Architecture == arch, ct);
        if (exists)
            throw new InvalidOperationException($"Build already exists for {packageId} {version} {os}/{arch}");

        var now = DateTime.UtcNow;
        var record = new UploadSessionRecord
        {
            Id = Guid.NewGuid(),
            ApiKeyId = apiKeyId,
            PackageDbId = package.Id,
            PackageId = packageId,
            Version = version,
            Channel = channel,
            OS = os,
            Architecture = arch,
            CreatedAt = now,
            ExpiresAt = now + SessionTimeout,
        };
        db.UploadSessions.Add(record);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Started upload session {SessionId} for {PackageId} {Version} {OS}/{Arch}",
            record.Id, packageId, version, os, arch);
        return ToSession(record, [], []);
    }

    /// <summary>
    /// Uploads a file to an open session. Validates the path and hash before touching storage
    ///; re-uploading the same path replaces the earlier file.
    /// </summary>
    public async Task<FileUploadResult> UploadFileAsync(
        Guid sessionId,
        string relativePath,
        string expectedHash,
        Stream content,
        CancellationToken ct = default)
    {
        var record = await OpenSessionAsync(sessionId, ct);
        var limits = Limits;

        if (expectedHash.Length != 64 || !expectedHash.All(char.IsAsciiHexDigitLower))
            throw new InvalidOperationException("sha256 must be 64 lowercase hex characters");

        // The path becomes a file name on every machine that installs this build, so it is
        // validated here, at the trust boundary, and only the canonical form is stored.
        if (!SafePath.TryNormalizeRelative(relativePath, out var normalizedPath, out var why))
            throw new InvalidOperationException($"Invalid file path '{relativePath}': {why}");
        if (normalizedPath.Length > limits.MaxPathLength)
            throw new InvalidOperationException($"File path is longer than {limits.MaxPathLength} characters");
        if (normalizedPath.Count(c => c == '/') + 1 > limits.MaxPathSegments)
            throw new InvalidOperationException($"File path is deeper than {limits.MaxPathSegments} segments");
        relativePath = normalizedPath;

        var replacing = await db.UploadSessionFiles.AnyAsync(f => f.SessionId == sessionId && f.RelativePath == relativePath, ct);
        if (!replacing && await db.UploadSessionFiles.CountAsync(f => f.SessionId == sessionId, ct) >= limits.MaxFilesPerSession)
            throw new InvalidOperationException($"A session may contain at most {limits.MaxFilesPerSession} files");

        // From the dedup lookup to the session row, no purge may delete this content.
        long size;
        string actualHash;
        bool deduplicated;
        await using (await ContentLocks.Shared.AcquireAsync(expectedHash, ct))
        {
            (size, actualHash, deduplicated) = await StoreContentAsync(expectedHash, content, relativePath, ct);

            await db.UploadSessionFiles.Where(f => f.SessionId == sessionId && f.RelativePath == relativePath).ExecuteDeleteAsync(ct);
            db.UploadSessionFiles.Add(new UploadSessionFile
            {
                SessionId = sessionId,
                RelativePath = relativePath,
                ContentHash = actualHash,
                Size = size,
                Deduplicated = deduplicated,
            });
            record.ExpiresAt = DateTime.UtcNow + SessionTimeout;   // activity keeps a session open
            await db.SaveChangesAsync(ct);
        }

        logger.LogDebug("Uploaded file {Path} ({Size} bytes, hash: {Hash}, deduplicated: {Dedup})",
            relativePath, size, actualHash, deduplicated);
        return new FileUploadResult { RelativePath = relativePath, ContentHash = actualHash, Size = size, Deduplicated = deduplicated };
    }

    /// <summary>
    /// Uploads an installer (<see cref="InstallerKinds"/>) to an open session. It is stored like
    /// a build file but is not part of the build's file list; re-uploading a kind replaces it.
    /// </summary>
    public async Task<FileUploadResult> UploadInstallerAsync(
        Guid sessionId,
        string kind,
        string fileName,
        string expectedHash,
        Stream content,
        CancellationToken ct = default)
    {
        var record = await OpenSessionAsync(sessionId, ct);
        if (!InstallerKinds.IsValid(kind))
            throw new InvalidOperationException($"Unknown installer kind '{kind}' (expected '{InstallerKinds.Online}' or '{InstallerKinds.Offline}')");
        if (!InstallerKinds.IsValidFileName(fileName))
            throw new InvalidOperationException(
                $"Invalid installer file name '{fileName}': use letters, digits, '. _ - + ( )' and spaces, at most {InstallerKinds.MaxFileNameLength} characters");
        if (expectedHash.Length != 64 || !expectedHash.All(char.IsAsciiHexDigitLower))
            throw new InvalidOperationException("sha256 must be 64 lowercase hex characters");

        long size;
        string actualHash;
        bool deduplicated;
        await using (await ContentLocks.Shared.AcquireAsync(expectedHash, ct))
        {
            (size, actualHash, deduplicated) = await StoreContentAsync(expectedHash, content, fileName, ct);

            await db.UploadSessionInstallers.Where(i => i.SessionId == sessionId && i.Kind == kind).ExecuteDeleteAsync(ct);
            db.UploadSessionInstallers.Add(new UploadSessionInstaller
            {
                SessionId = sessionId,
                Kind = kind,
                FileName = fileName,
                ContentHash = actualHash,
                Size = size,
                Deduplicated = deduplicated,
            });
            record.ExpiresAt = DateTime.UtcNow + SessionTimeout;
            await db.SaveChangesAsync(ct);
        }

        logger.LogDebug("Uploaded {Kind} installer {FileName} ({Size} bytes, hash: {Hash})", kind, fileName, size, actualHash);
        return new FileUploadResult { RelativePath = fileName, ContentHash = actualHash, Size = size, Deduplicated = deduplicated };
    }

    /// <summary>
    /// Streams <paramref name="content"/> through SHA-256 into a temp file, counting bytes
    /// against the size limit, checks the hash, and stores it as pending content.
    /// </summary>
    private async Task<(long Size, string Hash, bool Deduplicated)> StoreContentAsync(
        string expectedHash, Stream content, string label, CancellationToken ct)
    {
        var limits = Limits;
        var tempPath = Path.Combine(Path.GetTempPath(), $"instella-upload-{Guid.NewGuid()}");
        try
        {
            string actualHash;
            long size = 0;
            using (var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            await using (var tempStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                int bytesRead;
                while ((bytesRead = await content.ReadAsync(buffer, ct)) > 0)
                {
                    size += bytesRead;
                    if (size > limits.MaxFileBytes)
                        throw new InvalidOperationException($"File is larger than {limits.MaxFileBytes} bytes");
                    hasher.AppendData(buffer, 0, bytesRead);
                    await tempStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                }
                actualHash = Convert.ToHexStringLower(hasher.GetHashAndReset());
            }

            if (!string.Equals(actualHash, expectedHash, StringComparison.Ordinal))
                throw new InvalidOperationException($"Hash mismatch for {label}: expected {expectedHash}, got {actualHash}");

            bool deduplicated;
            await using (var storeStream = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
                (_, deduplicated) = await contentStorage.StorePendingAsync(actualHash, storeStream, size, ct);
            return (size, actualHash, deduplicated);
        }
        finally
        {
            if (File.Exists(tempPath))
                try { File.Delete(tempPath); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Completes an upload session, creating the version and build records.
    /// </summary>
    public async Task<UploadCompleteResult> CompleteSessionAsync(
        Guid sessionId,
        string? changelog,
        CancellationToken ct = default) =>
        await CompleteSessionAsync(sessionId, changelog, release: null, ct);

    /// <summary>
    /// Completes an upload session in one transaction: version and build rows, file rows, and
    /// the reference-count increments (grouped by hash, in bulk SQL), then the session rows are
    /// deleted. A signed <paramref name="release"/> must describe exactly the session's files.
    /// </summary>
    public async Task<UploadCompleteResult> CompleteSessionAsync(
        Guid sessionId,
        string? changelog,
        SignedRelease? release,
        CancellationToken ct = default)
    {
        var session = await GetSessionAsync(sessionId, ct)
            ?? throw new InvalidOperationException($"Session '{sessionId}' not found or expired");
        if (session.Files.Count == 0)
            throw new InvalidOperationException("Cannot complete session with no files");

        // Validated before anything is written, so a rejected release can be fixed and resent.
        var releaseBytes = await ValidateReleaseAsync(session, release, ct);
        return await CompleteCoreAsync(session, changelog, releaseBytes, release?.Signature, release?.KeyId, isDraft: false, ct);
    }

    /// <summary>
    /// Completes a draft upload: the build is stored with the exact unsigned manifest bytes
    /// (checked against the session like a signed release) and stays hidden from clients until
    /// <see cref="PublishDraftAsync"/> receives the publisher's signature over those bytes.
    /// </summary>
    public async Task<UploadCompleteResult> CompleteDraftSessionAsync(
        Guid sessionId,
        string? changelog,
        byte[] draftManifest,
        CancellationToken ct = default)
    {
        var session = await GetSessionAsync(sessionId, ct)
            ?? throw new InvalidOperationException($"Session '{sessionId}' not found or expired");
        if (session.Files.Count == 0)
            throw new InvalidOperationException("Cannot complete session with no files");

        CheckMatchesSession(session, ParseManifest(draftManifest));
        return await CompleteCoreAsync(session, changelog, draftManifest, signature: null, keyId: null, isDraft: true, ct);
    }

    /// <summary>
    /// Publishes a draft build: <paramref name="release"/> must sign exactly the draft's manifest
    /// bytes and, when the package has registered publisher keys, verify against one of them.
    /// Returns false when there is no such draft.
    /// </summary>
    public async Task<bool> PublishDraftAsync(
        string packageId, string version, TargetOS os, Architecture arch, SignedRelease release, CancellationToken ct = default)
    {
        version = Canonical(version);
        var build = await db.VersionBuilds
            .Include(b => b.Version).ThenInclude(v => v.Package)
            .FirstOrDefaultAsync(b => b.State == BuildState.Draft && b.Version.Package.PackageId == packageId
                                      && b.Version.VersionString == version && b.OS == os && b.Architecture == arch, ct);
        if (build is null) return false;

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(release.Manifest);
            Convert.FromBase64String(release.Signature);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"Release is malformed: {ex.Message}");
        }
        if (build.ReleaseManifestBytes is null || !bytes.AsSpan().SequenceEqual(build.ReleaseManifestBytes))
            throw new InvalidOperationException("The signed manifest is not this draft's manifest; sign the bytes 'instella publish' downloads.");

        var registeredKeys = await db.PackagePublisherKeys
            .Where(k => k.PackageId == build.Version.PackageId)
            .Select(k => new PublisherKey(k.KeyId, k.PublicKey))
            .ToListAsync(ct);
        if (registeredKeys.Count > 0)
        {
            try
            {
                ReleaseVerifier.Verify(release, new TrustPolicy(registeredKeys, packageId, PlatformMapping.ToWire(os),
                    PlatformMapping.ToWire(arch), MustBeNewerThan: null,
                    MustEqual: Version.TryParse(version, out var pinned) ? pinned : null));
            }
            catch (UpdateTrustException ex)
            {
                throw new InvalidOperationException($"Release rejected: {ex.Message}");
            }
        }

        // The version is "released" when its first build is published, so "latest" follows publishing.
        if (!await db.VersionBuilds.AnyAsync(b => b.VersionId == build.VersionId && b.Id != build.Id && b.State == BuildState.Published, ct))
            build.Version.ReleasedAt = DateTime.UtcNow;
        build.State = BuildState.Published;
        build.ReleaseSignature = release.Signature;
        build.ReleaseKeyId = release.KeyId;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Published draft build {BuildId} of {PackageId} {Version} {OS}/{Arch} (key {KeyId})",
            build.Id, packageId, version, os, arch, release.KeyId);
        return true;
    }

    /// <summary>The draft build of one platform, or null.</summary>
    public Task<VersionBuild?> GetDraftAsync(string packageId, string version, TargetOS os, Architecture arch, CancellationToken ct = default)
    {
        version = Canonical(version);
        return db.VersionBuilds.AsNoTracking()
            .Include(b => b.Version)
            .FirstOrDefaultAsync(b => b.State == BuildState.Draft && b.Version.Package.PackageId == packageId
                                      && b.Version.VersionString == version && b.OS == os && b.Architecture == arch, ct);
    }

    /// <summary>The canonical form of <paramref name="version"/> when it is one; else unchanged (it then matches nothing).</summary>
    private static string Canonical(string version) =>
        Instella.Core.Utilities.AppVersions.TryParse(version, out var parsed)
            ? Instella.Core.Utilities.AppVersions.ToCanonicalString(parsed)
            : version;

    private static UploadConflictException ChannelConflict(string version, string channel) =>
        new($"version {version} is on channel '{channel}'; a version belongs to one channel");

    private async Task<UploadCompleteResult> CompleteCoreAsync(
        UploadSession session, string? changelog, byte[]? releaseBytes, string? signature, string? keyId, bool isDraft,
        CancellationToken ct)
    {
        var sessionId = session.Id;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.UploadSessions.AnyAsync(s => s.Id == sessionId, ct))
            throw new InvalidOperationException($"Session '{sessionId}' not found or expired");

        if (!ChannelNames.TryNormalize(session.Channel, out var channelName))
            throw new InvalidOperationException($"Invalid channel '{session.Channel}': {ChannelNames.Rule}");

        var versionEntity = await db.PackageVersions.FirstOrDefaultAsync(v =>
            v.PackageId == session.PackageDbId && v.VersionString == session.Version, ct);
        if (versionEntity == null)
        {
            versionEntity = new PackageVersion
            {
                PackageId = session.PackageDbId,
                VersionString = session.Version,
                Channel = channelName,
                Changelog = changelog ?? "",
                ReleasedAt = DateTime.UtcNow,
            };
            db.PackageVersions.Add(versionEntity);
            // The first version on a channel creates it.
            if (!await db.PackageChannels.AnyAsync(c => c.PackageId == session.PackageDbId && c.Name == channelName, ct))
                db.PackageChannels.Add(new PackageChannel { PackageId = session.PackageDbId, Name = channelName });
        }
        else if (versionEntity.Channel != channelName)
        {
            // A concurrent session created the version on another channel after this one started.
            throw ChannelConflict(versionEntity.VersionString, versionEntity.Channel);
        }
        else if (!string.IsNullOrEmpty(changelog))
        {
            versionEntity.Changelog = changelog;
        }
        await db.SaveChangesAsync(ct);

        var files = session.Files.Values.ToList();
        var totalSize = files.Sum(f => f.Size);
        var build = new VersionBuild
        {
            VersionId = versionEntity.Id,
            OS = session.OS,
            Architecture = session.Architecture,
            TotalSize = totalSize,
            ManifestHash = ComputeManifestHash(files),
            UploadedAt = DateTime.UtcNow,
            ReleaseManifestBytes = releaseBytes,
            ReleaseSignature = signature,
            ReleaseKeyId = keyId,
            State = isDraft ? BuildState.Draft : BuildState.Published,
        };
        db.VersionBuilds.Add(build);
        await db.SaveChangesAsync(ct);

        foreach (var file in files)
        {
            db.BuildFiles.Add(new BuildFile
            {
                BuildId = build.Id,
                RelativePath = file.RelativePath,
                ContentHash = file.ContentHash,
                Size = file.Size,
            });
        }
        foreach (var installer in session.Installers.Values)
        {
            db.BuildInstallers.Add(new BuildInstaller
            {
                BuildId = build.Id,
                Kind = installer.Kind,
                FileName = installer.FileName,
                ContentHash = installer.ContentHash,
                Size = installer.Size,
            });
        }
        await db.SaveChangesAsync(ct);

        // References are counted here and only here: one per file or installer row, grouped by hash.
        var hashes = files.Select(f => f.ContentHash).Concat(session.Installers.Values.Select(i => i.ContentHash));
        foreach (var group in hashes.GroupBy(h => h))
        {
            var count = group.Count();
            await db.StoredFiles.Where(f => f.ContentHash == group.Key)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(f => f.ReferenceCount, f => f.ReferenceCount + count)
                    .SetProperty(f => f.PendingSince, (DateTime?)null), ct);
        }

        await db.UploadSessions.Where(s => s.Id == sessionId).ExecuteDeleteAsync(ct);   // files cascade
        // The patch job is queued in the upload's transaction, so a crash right after the commit cannot skip it.
        db.PendingPatchJobs.Add(new PendingPatchJob { ToBuildId = build.Id });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();

        logger.LogInformation("Completed upload session {SessionId}: created build {BuildId} with {FileCount} files ({TotalSize} bytes)",
            sessionId, build.Id, files.Count, totalSize);

        return new UploadCompleteResult
        {
            Success = true,
            BuildId = build.Id,
            VersionId = versionEntity.Id,
            FileCount = files.Count,
            TotalSize = totalSize,
            DeduplicatedCount = files.Count(f => f.Deduplicated),
        };
    }

    /// <summary>
    /// Checks a signed release against the session: identity (app, version, platform,
    /// channel) and the exact <c>(path, sha256, size)</c> set. Returns the signed bytes to
    /// store, or null for an accepted unsigned upload.
    /// </summary>
    private async Task<byte[]?> ValidateReleaseAsync(UploadSession session, SignedRelease? release, CancellationToken ct)
    {
        var registeredKeys = await db.PackagePublisherKeys
            .Where(k => k.PackageId == session.PackageDbId)
            .Select(k => new PublisherKey(k.KeyId, k.PublicKey))
            .ToListAsync(ct);

        if (release is null)
        {
            if (registeredKeys.Count > 0)
                throw new InvalidOperationException(
                    $"Package '{session.PackageId}' requires releases signed by a registered publisher key; this upload is unsigned.");
            return null;
        }

        byte[] bytes;
        ReleaseManifest manifest;
        if (registeredKeys.Count > 0)
        {
            // Full verification (signature + identity) against the registered keys.
            try
            {
                manifest = ReleaseVerifier.Verify(release, new TrustPolicy(
                    registeredKeys, session.PackageId, PlatformMapping.ToWire(session.OS),
                    PlatformMapping.ToWire(session.Architecture), MustBeNewerThan: null,
                    MustEqual: Version.TryParse(session.Version, out var pinned) ? pinned : null));
            }
            catch (UpdateTrustException ex)
            {
                throw new InvalidOperationException($"Release rejected: {ex.Message}");
            }
            bytes = Convert.FromBase64String(release.Manifest);
        }
        else
        {
            try
            {
                bytes = Convert.FromBase64String(release.Manifest);
                Convert.FromBase64String(release.Signature);
                manifest = JsonSerializer.Deserialize(bytes, TrustJsonContext.Default.ReleaseManifest)
                    ?? throw new InvalidOperationException("Release manifest is empty");
            }
            catch (Exception ex) when (ex is FormatException or JsonException)
            {
                throw new InvalidOperationException($"Release manifest is malformed: {ex.Message}");
            }
        }

        CheckMatchesSession(session, manifest);
        return bytes;
    }

    private static ReleaseManifest ParseManifest(byte[] bytes)
    {
        try
        {
            return JsonSerializer.Deserialize(bytes, TrustJsonContext.Default.ReleaseManifest)
                ?? throw new InvalidOperationException("Release manifest is empty");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Release manifest is malformed: {ex.Message}");
        }
    }

    /// <summary>
    /// A release manifest (signed or draft) must describe exactly the session: identity (app,
    /// version, platform, channel), the <c>(path, sha256, size)</c> set and the installers.
    /// </summary>
    private static void CheckMatchesSession(UploadSession session, ReleaseManifest manifest)
    {
        if (manifest.FormatVersion != ReleaseManifest.CurrentFormatVersion)
            throw new InvalidOperationException($"Unsupported release format {manifest.FormatVersion}");
        if (manifest.AppId != session.PackageId)
            throw new InvalidOperationException($"Release is for '{manifest.AppId}', not '{session.PackageId}'");
        if (!Version.TryParse(session.Version, out var sessionVersion) || manifest.Version != sessionVersion)
            throw new InvalidOperationException($"Release version {manifest.Version} does not match the session version {session.Version}");
        if (manifest.Os != PlatformMapping.ToWire(session.OS) || manifest.Arch != PlatformMapping.ToWire(session.Architecture))
            throw new InvalidOperationException($"Release platform {manifest.Os}/{manifest.Arch} does not match the session");
        if (!string.Equals(manifest.Channel, session.Channel, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Release channel '{manifest.Channel}' does not match the session channel '{session.Channel}'");

        var uploaded = session.Files.Values
            .Select(f => (f.RelativePath, Sha256: f.ContentHash.ToLowerInvariant(), f.Size))
            .ToHashSet();
        var declared = manifest.Files.Select(f => (f.Path, Sha256: f.Sha256.ToLowerInvariant(), f.Size)).ToHashSet();
        if (!uploaded.SetEquals(declared))
        {
            var missing = declared.Except(uploaded).Select(f => f.Item1).Take(3);
            var extra = uploaded.Except(declared).Select(f => f.Item1).Take(3);
            throw new InvalidOperationException(
                $"Release file list does not match the uploaded files (not uploaded: [{string.Join(", ", missing)}]; not in release: [{string.Join(", ", extra)}])");
        }

        // An installer is offered to users (and handed off to by older installers) only when
        // the publisher signed it, so the signed list must be exactly what was uploaded.
        var uploadedInstallers = session.Installers.Values
            .Select(i => (i.Kind, i.FileName, Sha256: i.ContentHash.ToLowerInvariant(), i.Size))
            .ToHashSet();
        var declaredInstallers = (manifest.Installers ?? [])
            .Select(i => (i.Kind, i.FileName, Sha256: i.Sha256.ToLowerInvariant(), i.Size))
            .ToHashSet();
        if (!uploadedInstallers.SetEquals(declaredInstallers))
            throw new InvalidOperationException(
                "Release installer list does not match the uploaded installers " +
                $"(signed: [{string.Join(", ", declaredInstallers.Select(i => $"{i.Kind} {i.FileName}"))}]; " +
                $"uploaded: [{string.Join(", ", uploadedInstallers.Select(i => $"{i.Kind} {i.FileName}"))}])");

    }

    /// <summary>
    /// Cancels an upload session. Its uploaded content was never counted; content nothing else
    /// references is removed by the orphan sweeper once its grace period has passed.
    /// </summary>
    public async Task CancelSessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        await db.UploadSessions.Where(s => s.Id == sessionId).ExecuteDeleteAsync(ct);
        logger.LogInformation("Cancelled upload session {SessionId}", sessionId);
    }

    /// <summary>An open session with its files, or null when it does not exist or has expired.</summary>
    public UploadSession? GetSession(Guid sessionId) => GetSessionAsync(sessionId).GetAwaiter().GetResult();

    /// <summary>An open session with its files, or null when it does not exist or has expired.</summary>
    public async Task<UploadSession?> GetSessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var record = await db.UploadSessions.AsNoTracking()
            .Include(s => s.Files)
            .Include(s => s.Installers)
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.ExpiresAt > now, ct);
        return record is null ? null : ToSession(record, record.Files, record.Installers);
    }

    private async Task<UploadSessionRecord> OpenSessionAsync(Guid sessionId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        return await db.UploadSessions.FirstOrDefaultAsync(s => s.Id == sessionId && s.ExpiresAt > now, ct)
            ?? throw new InvalidOperationException($"Session '{sessionId}' not found or expired");
    }

    private static UploadSession ToSession(UploadSessionRecord r, IEnumerable<UploadSessionFile> files,
        IEnumerable<UploadSessionInstaller> installers) => new()
    {
        Id = r.Id,
        ApiKeyId = r.ApiKeyId,
        PackageId = r.PackageId,
        PackageDbId = r.PackageDbId,
        Version = r.Version,
        Channel = r.Channel,
        OS = r.OS,
        Architecture = r.Architecture,
        StartedAt = r.CreatedAt,
        Files = files.ToDictionary(
            f => f.RelativePath,
            f => new UploadedFile { RelativePath = f.RelativePath, ContentHash = f.ContentHash, Size = f.Size, Deduplicated = f.Deduplicated },
            StringComparer.Ordinal),
        Installers = installers.ToDictionary(
            i => i.Kind,
            i => new UploadedInstaller { Kind = i.Kind, FileName = i.FileName, ContentHash = i.ContentHash, Size = i.Size },
            StringComparer.Ordinal),
    };

    private static string ComputeManifestHash(IEnumerable<UploadedFile> files)
    {
        var manifestBuilder = new StringBuilder();
        foreach (var file in files.OrderBy(f => f.RelativePath, StringComparer.Ordinal))
            manifestBuilder.AppendLine($"{file.RelativePath}:{file.ContentHash}");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(manifestBuilder.ToString())));
    }
}

/// <summary>
/// An open upload session, as read from the database.
/// </summary>
/// <summary>The upload conflicts with what the server holds (HTTP 409).</summary>
public sealed class UploadConflictException(string message) : InvalidOperationException(message);

public class UploadSession
{
    public Guid Id { get; init; }
    public long? ApiKeyId { get; init; }
    public required string PackageId { get; init; }
    public long PackageDbId { get; init; }
    public required string Version { get; init; }
    public required string Channel { get; init; }
    public TargetOS OS { get; init; }
    public Architecture Architecture { get; init; }
    public DateTime StartedAt { get; init; }
    public required IReadOnlyDictionary<string, UploadedFile> Files { get; init; }

    /// <summary>Installers uploaded into the session, by kind.</summary>
    public IReadOnlyDictionary<string, UploadedInstaller> Installers { get; init; } = new Dictionary<string, UploadedInstaller>();
}

/// <summary>An installer uploaded within a session.</summary>
public class UploadedInstaller
{
    public required string Kind { get; init; }
    public required string FileName { get; init; }
    public required string ContentHash { get; init; }
    public long Size { get; init; }
}

/// <summary>
/// Represents a file uploaded within a session.
/// </summary>
public class UploadedFile
{
    public required string RelativePath { get; init; }
    public required string ContentHash { get; init; }
    public long Size { get; init; }
    public bool Deduplicated { get; init; }
}

/// <summary>
/// Result of uploading a single file.
/// </summary>
public record FileUploadResult
{
    public required string RelativePath { get; init; }
    public required string ContentHash { get; init; }
    public long Size { get; init; }
    public bool Deduplicated { get; init; }
}

/// <summary>
/// Result of completing an upload session.
/// </summary>
public record UploadCompleteResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public long BuildId { get; init; }
    public long VersionId { get; init; }
    public int FileCount { get; init; }
    public long TotalSize { get; init; }
    public int DeduplicatedCount { get; init; }
}
