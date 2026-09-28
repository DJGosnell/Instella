namespace Instella.Server.Data.Entities;

/// <summary>
/// Specifies the download access mode for a package.
/// </summary>
public enum DownloadAccessMode
{
    /// <summary>
    /// No authentication required.
    /// </summary>
    Open,

    /// <summary>
    /// Requires an Admin-scoped API key.
    /// </summary>
    MasterKeyRequired,

    /// <summary>
    /// Requires an Admin-scoped API key or a package-specific API key.
    /// </summary>
    PackageKeyRequired
}

/// <summary>
/// Whether a signed upload goes live at once. The server can only hold a release back; clients still
/// verify every release against the keys compiled into their installer.
/// </summary>
public enum ReleaseApproval
{
    /// <summary>A signed upload is published immediately.</summary>
    Automatic = 0,

    /// <summary>A signed upload is published after <see cref="Package.ReleaseDelayMinutes"/> unless rejected.</summary>
    Delayed = 1,

    /// <summary>A signed upload waits until an admin or an approve key approves it.</summary>
    Required = 2,
}

/// <summary>
/// Represents a distributable software package.
/// </summary>
public class Package
{
    public long Id { get; set; }

    /// <summary>
    /// Unique identifier matching Instella AppId.
    /// </summary>
    public required string PackageId { get; set; }

    /// <summary>
    /// Human-readable display name.
    /// </summary>
    public required string DisplayName { get; set; }

    /// <summary>
    /// Package description.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Optional path to package icon.
    /// </summary>
    public string? IconPath { get; set; }

    /// <summary>
    /// Specifies the download access mode for this package.
    /// </summary>
    public DownloadAccessMode DownloadAccessMode { get; set; } = DownloadAccessMode.Open;

    /// <summary>Whether signed uploads go live at once, after a delay, or after approval.</summary>
    public ReleaseApproval ReleaseApproval { get; set; } = ReleaseApproval.Automatic;

    /// <summary>The hold of <see cref="ReleaseApproval.Delayed"/>, in minutes.</summary>
    public int ReleaseDelayMinutes { get; set; } = 1440;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<PackageVersion> Versions { get; set; } = [];

    /// <summary>The channels versions have been uploaded to.</summary>
    public ICollection<PackageChannel> Channels { get; set; } = [];

    public ICollection<ApiKey> ApiKeys { get; set; } = [];

    /// <summary>Publisher keys uploads must be signed with (optional, see <see cref="PackagePublisherKey"/>).</summary>
    public ICollection<PackagePublisherKey> PublisherKeys { get; set; } = [];
}
