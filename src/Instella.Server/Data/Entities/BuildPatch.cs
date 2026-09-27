namespace Instella.Server.Data.Entities;

/// <summary>
/// Stores a binary diff patch between two builds.
/// </summary>
public class BuildPatch
{
    public long Id { get; set; }

    /// <summary>
    /// Source build (older version).
    /// </summary>
    public long FromBuildId { get; set; }

    public VersionBuild FromBuild { get; set; } = null!;

    /// <summary>
    /// Target build (newer version).
    /// </summary>
    public long ToBuildId { get; set; }

    public VersionBuild ToBuild { get; set; } = null!;

    /// <summary>
    /// Total size of the patch archive in bytes.
    /// </summary>
    public long PatchSize { get; set; }

    /// <summary>
    /// SHA256 hash of the patch archive.
    /// </summary>
    public required string PatchHash { get; set; }

    /// <summary>
    /// Path in the storage provider.
    /// </summary>
    public required string StoragePath { get; set; }

    /// <summary>
    /// Serialized PatchManifest JSON containing:
    /// - PatchedFiles: Files with binary diffs
    /// - NewFiles: Files to download fully
    /// - DeletedFiles: Files to remove
    /// - VerificationList: All files post-update for integrity check
    /// </summary>
    public required string ManifestJson { get; set; }

    /// <summary>
    /// When this patch was generated.
    /// </summary>
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
}
