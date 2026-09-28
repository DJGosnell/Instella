using System.Text.Json.Serialization;
using Instella.Core.Trust;
using Instella.Core.Update;

namespace Instella.Core.Wire;

/// <summary>Response of <see cref="ApiRoutes.CheckUpdate"/>.</summary>
public sealed record CheckUpdateResponse
{
    /// <summary>Whether a newer version is available for the caller's platform and channel.</summary>
    public required bool UpdateAvailable { get; init; }

    /// <summary>The newer version, when one is available.</summary>
    public string? Version { get; init; }

    /// <summary>Changelog of the newer version.</summary>
    public string? Changelog { get; init; }

    /// <summary>Total size in bytes of the newer build's files.</summary>
    public long? FullSize { get; init; }

    /// <summary>Whether a patch archive from the caller's version exists.</summary>
    public bool PatchAvailable { get; init; }

    /// <summary>Size of the patch archive in bytes.</summary>
    public long? PatchSize { get; init; }

    /// <summary>SHA-256 of the patch archive blob (corruption check only; not a trust anchor).</summary>
    public string? PatchSha256 { get; init; }

    /// <summary>
    /// Reserved: always <see langword="false"/> in 1.x (docs/compatibility.md). Clients ignore it
    /// until a later version defines it.
    /// </summary>
    public bool Mandatory { get; init; }

    /// <summary>
    /// The signed release manifest of <see cref="Version"/>, saving clients a round trip.
    /// Null for builds uploaded without a signature.
    /// </summary>
    public SignedRelease? Release { get; init; }
}

/// <summary>Body of <see cref="ApiRoutes.UploadStart"/>.</summary>
public sealed record StartUploadRequest
{
    /// <summary>Package id.</summary>
    public required string PackageId { get; init; }

    /// <summary>Version being uploaded.</summary>
    public required string Version { get; init; }

    /// <summary>Canonical OS name (<see cref="PlatformStrings.Os"/>).</summary>
    public string? Os { get; init; }

    /// <summary>Canonical architecture name (<see cref="PlatformStrings.Arch"/>).</summary>
    public string? Arch { get; init; }

    /// <summary>Release channel; defaults to <c>stable</c>.</summary>
    public string? Channel { get; init; }
}

/// <summary>Response of <see cref="ApiRoutes.UploadStart"/>.</summary>
public sealed record StartUploadResponse
{
    /// <summary>The new session id.</summary>
    public required Guid SessionId { get; init; }

    /// <summary>Package id.</summary>
    public string? PackageId { get; init; }

    /// <summary>Version.</summary>
    public string? Version { get; init; }

    /// <summary>Canonical OS name.</summary>
    public string? Os { get; init; }

    /// <summary>Canonical architecture name.</summary>
    public string? Arch { get; init; }
}

/// <summary>Response of <see cref="ApiRoutes.UploadFile"/>.</summary>
public sealed record UploadFileResponse
{
    /// <summary>Whether the file was stored.</summary>
    public bool Stored { get; init; }

    /// <summary>Whether identical content already existed.</summary>
    public bool Deduplicated { get; init; }

    /// <summary>Normalised relative path the server recorded.</summary>
    public string? Path { get; init; }

    /// <summary>Size in bytes.</summary>
    public long Size { get; init; }

    /// <summary>SHA-256 of the content.</summary>
    public string? Hash { get; init; }
}

/// <summary>Body of <see cref="ApiRoutes.UploadComplete"/>.</summary>
public sealed record CompleteUploadRequest
{
    /// <summary>Changelog for the version.</summary>
    public string? Changelog { get; init; }

    /// <summary>The publisher-signed release manifest for the uploaded files; null for unsigned uploads.</summary>
    public SignedRelease? Release { get; init; }

    /// <summary>
    /// For a draft upload (<c>instella upload --draft</c>): base64 of the exact, still unsigned
    /// release manifest bytes. The build stays hidden until <c>instella publish</c> signs them.
    /// </summary>
    public string? DraftManifest { get; init; }
}

/// <summary>Response of GET <see cref="ApiRoutes.Draft"/>.</summary>
public sealed record DraftResponse
{
    /// <summary>Base64 of the exact unsigned release manifest bytes to sign.</summary>
    public required string Manifest { get; init; }

    /// <summary>When the draft was uploaded (UTC).</summary>
    public DateTime UploadedAt { get; init; }

    /// <summary>Changelog of the version.</summary>
    public string? Changelog { get; init; }
}

/// <summary>Response of <see cref="ApiRoutes.UploadComplete"/>.</summary>
public sealed record CompleteUploadResponse
{
    /// <summary>Whether the session completed.</summary>
    public bool Success { get; init; }

    /// <summary>Id of the created build.</summary>
    public long BuildId { get; init; }

    /// <summary>Id of the version the build belongs to.</summary>
    public long VersionId { get; init; }

    /// <summary>Number of files in the build.</summary>
    public int FileCount { get; init; }

    /// <summary>Total size in bytes.</summary>
    public long TotalSize { get; init; }

    /// <summary>Number of files whose content already existed on the server.</summary>
    public int DeduplicatedCount { get; init; }

    /// <summary>
    /// A <see cref="ReleaseStates"/> value: whether clients see the build now, or it is a draft or held by
    /// the package's release approval. Null from an older server.
    /// </summary>
    public string? State { get; init; }

    /// <summary>When a delayed release goes live unless rejected (UTC).</summary>
    public DateTime? PublishAfter { get; init; }
}

/// <summary>Body of PUT <see cref="ApiRoutes.PackageVersion"/>.</summary>
public sealed record UpdateVersionRequest
{
    /// <summary>New changelog, or null to keep.</summary>
    public string? Changelog { get; init; }

    /// <summary>New deprecation flag, or null to keep.</summary>
    public bool? IsDeprecated { get; init; }
}

/// <summary>One entry of <see cref="ApiRoutes.Packages"/>.</summary>
public sealed record PackageSummary
{
    /// <summary>Package id.</summary>
    public required string PackageId { get; init; }

    /// <summary>Display name.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Description.</summary>
    public string? Description { get; init; }

    /// <summary>Number of versions.</summary>
    public int VersionCount { get; init; }

    /// <summary>Most recently released version.</summary>
    public string? LatestVersion { get; init; }
}

/// <summary>One entry of <see cref="ApiRoutes.PackageVersions"/>.</summary>
public sealed record VersionSummary
{
    /// <summary>Version string.</summary>
    public required string VersionString { get; init; }

    /// <summary>Channel name (lower case).</summary>
    public string? Channel { get; init; }

    /// <summary>Changelog.</summary>
    public string? Changelog { get; init; }

    /// <summary>Release time (UTC).</summary>
    public DateTime ReleasedAt { get; init; }

    /// <summary>Whether the version is deprecated.</summary>
    public bool IsDeprecated { get; init; }

    /// <summary>Total downloads across builds.</summary>
    public long DownloadCount { get; init; }

    /// <summary>Builds of this version.</summary>
    public IReadOnlyList<BuildSummary> Builds { get; init; } = [];
}

/// <summary>One build inside a <see cref="VersionSummary"/>.</summary>
public sealed record BuildSummary
{
    /// <summary>Canonical OS name.</summary>
    public required string Os { get; init; }

    /// <summary>Canonical architecture name.</summary>
    public required string Arch { get; init; }

    /// <summary>Total size in bytes.</summary>
    public long FileSize { get; init; }

    /// <summary>Number of files.</summary>
    public int FileCount { get; init; }

    /// <summary>Installers published with this build.</summary>
    public IReadOnlyList<InstallerSummary> Installers { get; init; } = [];
}

/// <summary>One installer inside a <see cref="BuildSummary"/>.</summary>
public sealed record InstallerSummary
{
    /// <summary><see cref="InstallerKinds.Online"/> or <see cref="InstallerKinds.Offline"/>.</summary>
    public required string Kind { get; init; }

    /// <summary>File name users download.</summary>
    public required string FileName { get; init; }

    /// <summary>Size in bytes.</summary>
    public long Size { get; init; }
}

/// <summary>Error body returned by every API endpoint on failure.</summary>
public sealed record ApiError
{
    /// <summary>Human-readable message.</summary>
    public string? Error { get; init; }
}

/// <summary>Plain acknowledgement body.</summary>
public sealed record MessageResponse
{
    /// <summary>Human-readable message.</summary>
    public string? Message { get; init; }
}

/// <summary>
/// The state of a build on the server, as <see cref="CompleteUploadResponse.State"/> and
/// <see cref="PublishDraftResponse.State"/> report it. Only a published build is visible to clients.
/// </summary>
public static class ReleaseStates
{
    /// <summary>Visible to installers and apps.</summary>
    public const string Published = "published";

    /// <summary>Signed, and held back by the package's release approval until approved or its delay ends.</summary>
    public const string Pending = "pending";

    /// <summary>Unsigned, waiting for <c>instella publish</c>.</summary>
    public const string Draft = "draft";
}

/// <summary>Response of POST <see cref="ApiRoutes.PublishDraft"/>.</summary>
public sealed record PublishDraftResponse
{
    /// <summary>Human-readable message.</summary>
    public string? Message { get; init; }

    /// <summary>A <see cref="ReleaseStates"/> value: published, or pending under the package's release approval.</summary>
    public string? State { get; init; }

    /// <summary>When a delayed release goes live unless rejected (UTC).</summary>
    public DateTime? PublishAfter { get; init; }
}

/// <summary>One entry of GET <see cref="ApiRoutes.Approvals"/>: a build clients cannot see yet.</summary>
public sealed record UnpublishedReleaseSummary
{
    /// <summary>Version.</summary>
    public required string Version { get; init; }

    /// <summary>Channel name.</summary>
    public string? Channel { get; init; }

    /// <summary>Canonical OS name.</summary>
    public required string Os { get; init; }

    /// <summary>Canonical architecture name.</summary>
    public required string Arch { get; init; }

    /// <summary><see cref="ReleaseStates.Pending"/> or <see cref="ReleaseStates.Draft"/>.</summary>
    public required string State { get; init; }

    /// <summary>When the build was uploaded (UTC).</summary>
    public DateTime UploadedAt { get; init; }

    /// <summary>When a delayed release goes live unless rejected (UTC); null when it waits for approval.</summary>
    public DateTime? PublishAfter { get; init; }

    /// <summary>Id of the publisher key that signed it; null for a draft.</summary>
    public string? KeyId { get; init; }

    /// <summary>The label the key is registered with on the server.</summary>
    public string? KeyLabel { get; init; }

    /// <summary>Name of the API key that uploaded it.</summary>
    public string? UploadedBy { get; init; }

    /// <summary>Lowercase hex SHA-256 of the release manifest bytes: what an approval confirms.</summary>
    public string? ManifestSha256 { get; init; }

    /// <summary>Number of files.</summary>
    public int FileCount { get; init; }

    /// <summary>Total size in bytes.</summary>
    public long TotalSize { get; init; }
}

/// <summary>Response of GET <see cref="ApiRoutes.Approval"/>.</summary>
public sealed record UnpublishedReleaseResponse
{
    /// <summary>The build.</summary>
    public required UnpublishedReleaseSummary Summary { get; init; }

    /// <summary>Base64 of the exact release manifest bytes (signed, or unsigned for a draft).</summary>
    public required string Manifest { get; init; }

    /// <summary>Changelog of the version.</summary>
    public string? Changelog { get; init; }
}

/// <summary>Body of POST <see cref="ApiRoutes.ApproveRelease"/>.</summary>
public sealed record ApproveReleaseRequest
{
    /// <summary>
    /// SHA-256 of the manifest bytes the approver reviewed; the server refuses (409) when the build's
    /// manifest is different.
    /// </summary>
    public required string ManifestSha256 { get; init; }
}

/// <summary>Body of POST <see cref="ApiRoutes.RejectRelease"/>.</summary>
public sealed record RejectReleaseRequest
{
    /// <summary>SHA-256 of the manifest bytes the rejecter reviewed; optional.</summary>
    public string? ManifestSha256 { get; init; }

    /// <summary>Why, for the audit log.</summary>
    public string? Reason { get; init; }
}

/// <summary>Source-generated JSON context for every wire DTO. Used by clients and server alike.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CheckUpdateResponse))]
[JsonSerializable(typeof(StartUploadRequest))]
[JsonSerializable(typeof(StartUploadResponse))]
[JsonSerializable(typeof(UploadFileResponse))]
[JsonSerializable(typeof(CompleteUploadRequest))]
[JsonSerializable(typeof(CompleteUploadResponse))]
[JsonSerializable(typeof(UpdateVersionRequest))]
[JsonSerializable(typeof(PackageSummary[]))]
[JsonSerializable(typeof(VersionSummary[]))]
[JsonSerializable(typeof(ApiError))]
[JsonSerializable(typeof(MessageResponse))]
[JsonSerializable(typeof(PatchManifest))]
[JsonSerializable(typeof(SignedRelease))]
[JsonSerializable(typeof(DraftResponse))]
[JsonSerializable(typeof(PublishDraftResponse))]
[JsonSerializable(typeof(UnpublishedReleaseSummary[]))]
[JsonSerializable(typeof(UnpublishedReleaseResponse))]
[JsonSerializable(typeof(ApproveReleaseRequest))]
[JsonSerializable(typeof(RejectReleaseRequest))]
internal sealed partial class WireJsonContext : JsonSerializerContext;
