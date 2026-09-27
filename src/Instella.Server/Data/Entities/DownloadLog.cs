namespace Instella.Server.Data.Entities;

/// <summary>
/// Download log entry for statistics tracking.
/// </summary>
public class DownloadLog
{
    public long Id { get; set; }

    public long BuildId { get; set; }

    public VersionBuild Build { get; set; } = null!;

    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// True if this was a patch download.
    /// </summary>
    public bool IsPatch { get; set; }

    /// <summary>
    /// Hashed IP address for privacy.
    /// </summary>
    public string IPHash { get; set; } = string.Empty;

    /// <summary>
    /// User agent string.
    /// </summary>
    public string? UserAgent { get; set; }
}
