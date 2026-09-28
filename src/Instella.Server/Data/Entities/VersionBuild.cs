using Instella.Server.Models;

namespace Instella.Server.Data.Entities;

/// <summary>Whether clients see a build.</summary>
public enum BuildState
{
    /// <summary>Visible to installers and apps.</summary>
    Published = 0,

    /// <summary>
    /// Uploaded with <c>instella upload --draft</c>: <see cref="VersionBuild.ReleaseManifestBytes"/>
    /// holds the unsigned manifest until <c>instella publish</c> signs it.
    /// </summary>
    Draft = 1,

    /// <summary>
    /// Signed, and held back by the package's <see cref="ReleaseApproval"/> until someone approves it
    /// or, under <see cref="ReleaseApproval.Delayed"/>, its <see cref="VersionBuild.PublishAfter"/> passes.
    /// </summary>
    Pending = 2,
}

/// <summary>
/// Represents a compiled build for a specific OS/Architecture combination.
/// Uses content-addressed storage - files are stored separately in StoredFile.
/// </summary>
public class VersionBuild
{
    public long Id { get; set; }

    public long VersionId { get; set; }

    public PackageVersion Version { get; set; } = null!;

    /// <summary>
    /// Target operating system.
    /// </summary>
    public TargetOS OS { get; set; }

    /// <summary>
    /// Target CPU architecture.
    /// </summary>
    public Architecture Architecture { get; set; }

    /// <summary>
    /// Sum of all file sizes in this build.
    /// </summary>
    public long TotalSize { get; set; }

    /// <summary>
    /// Hash of the file list for integrity verification.
    /// </summary>
    public required string ManifestHash { get; set; }

    /// <summary>
    /// The exact publisher-signed release manifest bytes (UTF-8 JSON), stored verbatim and
    /// relayed unmodified. Null for builds uploaded with <c>--unsigned</c>.
    /// </summary>
    public byte[]? ReleaseManifestBytes { get; set; }

    /// <summary>Base64 IEEE P1363 signature over <see cref="ReleaseManifestBytes"/>.</summary>
    public string? ReleaseSignature { get; set; }

    /// <summary>Id of the key that signed <see cref="ReleaseManifestBytes"/>.</summary>
    public string? ReleaseKeyId { get; set; }

    /// <summary>
    /// Whether clients see the build. Only <see cref="BuildState.Published"/> builds are offered as an
    /// update, listed, released, downloaded or installed; see <see cref="BuildState"/>.
    /// </summary>
    public BuildState State { get; set; }

    /// <summary>
    /// For a <see cref="BuildState.Pending"/> build under <see cref="ReleaseApproval.Delayed"/>: when
    /// the server publishes it unless someone rejects it first (UTC). Null otherwise.
    /// </summary>
    public DateTime? PublishAfter { get; set; }

    /// <summary>The API key that uploaded the build (no foreign key: keys go with their package).</summary>
    public long? UploadedByApiKeyId { get; set; }

    /// <summary>Name of the uploading API key, for the admin UI and the audit log.</summary>
    public string? UploadedByKeyName { get; set; }

    /// <summary>
    /// When this build was uploaded.
    /// </summary>
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Full build download count.
    /// </summary>
    public long DownloadCount { get; set; }

    /// <summary>
    /// Patch download count.
    /// </summary>
    public long PatchDownloadCount { get; set; }

    /// <summary>
    /// All files in this build.
    /// </summary>
    public ICollection<BuildFile> Files { get; set; } = [];

    /// <summary>Installers published with this build (at most one per kind).</summary>
    public ICollection<BuildInstaller> Installers { get; set; } = [];

    /// <summary>
    /// Patches where this build is the source (older version).
    /// </summary>
    public ICollection<BuildPatch> PatchesFrom { get; set; } = [];

    /// <summary>
    /// Patches where this build is the target (newer version).
    /// </summary>
    public ICollection<BuildPatch> PatchesTo { get; set; } = [];

    /// <summary>
    /// Download logs for this build.
    /// </summary>
    public ICollection<DownloadLog> DownloadLogs { get; set; } = [];
}
