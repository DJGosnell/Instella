namespace Instella.Server.Models;

/// <summary>
/// Flattened view model for displaying a package in the tree-grid.
/// Contains pre-aggregated statistics to avoid repeated calculations during rendering.
/// </summary>
public class PackageGridItem
{
    /// <summary>Database primary key.</summary>
    public long Id { get; set; }

    /// <summary>Unique package identifier (e.g., "com.example.myapp").</summary>
    public string PackageId { get; set; } = "";

    /// <summary>Human-readable display name.</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>Number of versions for this package.</summary>
    public int VersionCount { get; set; }

    /// <summary>Total number of builds across all versions.</summary>
    public int BuildCount { get; set; }

    /// <summary>Sum of DownloadCount across all versions.</summary>
    public long TotalDownloads { get; set; }

    /// <summary>Sum of FileSize across all builds (bytes).</summary>
    public long TotalSize { get; set; }

    /// <summary>Most recent ReleasedAt date across all versions.</summary>
    public DateTime? LatestReleaseDate { get; set; }

    /// <summary>Child versions, pre-sorted by ReleasedAt descending.</summary>
    public List<VersionGridItem> Versions { get; set; } = [];

    /// <summary>Builds pending approval, across versions.</summary>
    public int PendingCount => Versions.Sum(v => v.PendingCount);
}
