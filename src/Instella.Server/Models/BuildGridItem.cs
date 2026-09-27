namespace Instella.Server.Models;

/// <summary>
/// View model for a build row in the tree-grid.
/// </summary>
public class BuildGridItem
{
    public long Id { get; set; }
    public long VersionId { get; set; }

    /// <summary>Target operating system.</summary>
    public TargetOS OS { get; set; }

    /// <summary>Target CPU architecture.</summary>
    public Architecture Architecture { get; set; }

    /// <summary>Combined "OS Architecture" display string.</summary>
    public string Platform => $"{OS} {Architecture}";

    /// <summary>Total size in bytes.</summary>
    public long FileSize { get; set; }

    /// <summary>Download count for this specific build.</summary>
    public long Downloads { get; set; }

    /// <summary>Number of files in this build.</summary>
    public int FileCount { get; set; }

    /// <summary>Site-relative URL of the full-build download.</summary>
    public string DownloadPath { get; set; } = "";
}
