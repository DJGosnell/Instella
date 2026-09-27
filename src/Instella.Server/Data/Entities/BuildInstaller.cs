namespace Instella.Server.Data.Entities;

/// <summary>
/// An installer published with a build (one per kind): the online installer, which downloads
/// the build, and the offline installer, which carries it. Its content lives in content-addressed
/// storage like a build file and counts as one reference.
/// </summary>
public class BuildInstaller
{
    public long Id { get; set; }

    public long BuildId { get; set; }

    public VersionBuild Build { get; set; } = null!;

    /// <summary><c>online</c> or <c>offline</c> (<see cref="Instella.Core.Wire.InstallerKinds"/>).</summary>
    public required string Kind { get; set; }

    /// <summary>File name users download.</summary>
    public required string FileName { get; set; }

    /// <summary>SHA-256 of the content, FK to <see cref="StoredFile"/>.</summary>
    public required string ContentHash { get; set; }

    public StoredFile StoredFile { get; set; } = null!;

    /// <summary>Size in bytes.</summary>
    public long Size { get; set; }

    /// <summary>How often it was downloaded.</summary>
    public long DownloadCount { get; set; }
}
