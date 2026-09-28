namespace Instella.Server.Models;

/// <summary>
/// View model for a version row in the tree-grid.
/// </summary>
public class VersionGridItem
{
    public long Id { get; set; }
    public long PackageId { get; set; }

    /// <summary>Semantic version string (e.g., "1.2.3").</summary>
    public string VersionString { get; set; } = "";

    /// <summary>Release channel name ("stable", "beta", …).</summary>
    public string Channel { get; set; } = Instella.Core.Wire.ChannelNames.Stable;

    /// <summary>Version-level download count.</summary>
    public long Downloads { get; set; }

    /// <summary>Sum of FileSize across all builds for this version.</summary>
    public long TotalSize { get; set; }

    /// <summary>When this version was released.</summary>
    public DateTime ReleasedAt { get; set; }

    /// <summary>Whether this version is deprecated.</summary>
    public bool IsDeprecated { get; set; }

    /// <summary>Child builds, pre-sorted by OS then Architecture.</summary>
    public List<BuildGridItem> Builds { get; set; } = [];

    /// <summary>Builds pending approval.</summary>
    public int PendingCount => Builds.Count(b => b.State == Instella.Server.Data.Entities.BuildState.Pending);
}
