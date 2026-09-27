namespace Instella.Server.Data.Entities;

/// <summary>
/// Content-addressed storage: one row per unique file content.
/// Multiple BuildFiles can reference the same StoredFile for deduplication.
/// </summary>
public class StoredFile
{
    /// <summary>
    /// Primary key - SHA256 hex string of file content.
    /// </summary>
    public required string ContentHash { get; set; }

    /// <summary>
    /// File size in bytes.
    /// </summary>
    public long Size { get; set; }

    /// <summary>
    /// Path in the storage provider.
    /// </summary>
    public required string StoragePath { get; set; }

    /// <summary>
    /// Number of BuildFiles referencing this content, maintained in bulk SQL when a session
    /// completes or a version is deleted.
    /// </summary>
    public int ReferenceCount { get; set; }

    /// <summary>
    /// Set while the content is referenced only by an open upload session (reference count 0).
    /// Completion clears it; the orphan sweeper deletes content left pending for 24 hours.
    /// </summary>
    public DateTime? PendingSince { get; set; }

    /// <summary>
    /// When this content was first uploaded.
    /// </summary>
    public DateTime FirstUploadedAt { get; set; } = DateTime.UtcNow;
}
