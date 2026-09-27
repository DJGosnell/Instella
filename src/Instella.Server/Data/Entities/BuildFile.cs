namespace Instella.Server.Data.Entities;

/// <summary>
/// Links a build to a stored file at a specific path.
/// Enables content-addressed storage where identical files are shared.
/// </summary>
public class BuildFile
{
    public long Id { get; set; }

    public long BuildId { get; set; }

    public VersionBuild Build { get; set; } = null!;

    /// <summary>
    /// Relative path within the package (e.g., "lib/MyApp.dll").
    /// </summary>
    public required string RelativePath { get; set; }

    /// <summary>
    /// SHA256 hash of the file content, FK to StoredFile.
    /// </summary>
    public required string ContentHash { get; set; }

    public StoredFile StoredFile { get; set; } = null!;

    /// <summary>
    /// File size in bytes.
    /// </summary>
    public long Size { get; set; }
}
