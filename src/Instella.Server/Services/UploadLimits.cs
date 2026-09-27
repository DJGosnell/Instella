namespace Instella.Server.Services;

/// <summary>Upload validation limits, bound from the <c>Upload</c> configuration section.</summary>
public sealed class UploadLimits
{
    /// <summary>Largest single file accepted (default 2 GiB).</summary>
    public long MaxFileBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    /// <summary>Most files one session may contain.</summary>
    public int MaxFilesPerSession { get; set; } = 20_000;

    /// <summary>Longest relative path, in characters.</summary>
    public int MaxPathLength { get; set; } = 260;

    /// <summary>Deepest relative path, in segments.</summary>
    public int MaxPathSegments { get; set; } = 32;
}
