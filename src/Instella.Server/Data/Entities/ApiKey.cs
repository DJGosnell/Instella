namespace Instella.Server.Data.Entities;

/// <summary>
/// API key scope.
/// </summary>
public enum ApiKeyScope
{
    /// <summary>
    /// Full admin access.
    /// </summary>
    Admin,

    /// <summary>
    /// Access to a specific package only.
    /// </summary>
    Package
}

/// <summary>
/// Represents an API key for CLI/API authentication.
/// </summary>
public class ApiKey
{
    public long Id { get; set; }

    /// <summary>
    /// SHA256 hash of the key (never store plaintext).
    /// </summary>
    public required string KeyHash { get; set; }

    /// <summary>
    /// Description for admin reference.
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// Key scope.
    /// </summary>
    public ApiKeyScope Scope { get; set; }

    /// <summary>
    /// For package-scoped keys, the associated package.
    /// </summary>
    public long? PackageId { get; set; }

    public Package? Package { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? LastUsedAt { get; set; }

    public bool IsRevoked { get; set; }

    /// <summary>
    /// Whether this key can be used for uploads.
    /// </summary>
    public bool CanUpload { get; set; } = true;

    /// <summary>
    /// Whether this key can be used for downloads.
    /// </summary>
    public bool CanDownload { get; set; } = false;

    /// <summary>
    /// Whether this key can edit, deprecate and delete versions. Off by default: an upload key
    /// in a CI pipeline should not be able to delete releases.
    /// </summary>
    public bool CanManageVersions { get; set; }
}
