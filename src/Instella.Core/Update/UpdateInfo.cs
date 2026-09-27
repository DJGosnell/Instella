namespace Instella.Core.Update;

/// <summary>
/// Information about an available update.
/// </summary>
public sealed record UpdateInfo
{
    /// <summary>The version of the available update.</summary>
    public required Version Version { get; init; }

    /// <summary>Markdown-formatted changelog for this version.</summary>
    public required string Changelog { get; init; }

    /// <summary>Total size of the full update download in bytes.</summary>
    public required long FullSize { get; init; }

    /// <summary>Whether a patch update is available (smaller download).</summary>
    public bool PatchAvailable { get; init; }

    /// <summary>Size of the patch download in bytes (if available).</summary>
    public long? PatchSize { get; init; }

    /// <summary>SHA256 hash of the patch archive (if available).</summary>
    public string? PatchSha256 { get; init; }

    /// <summary>Whether this update is mandatory (cannot be skipped).</summary>
    public bool Mandatory { get; init; }

    /// <summary>The release channel of this update.</summary>
    public string Channel { get; init; } = "stable";
}
