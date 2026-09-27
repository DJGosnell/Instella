namespace Instella.Sdk.Internal;

/// <summary>
/// Represents progress during a download operation.
/// </summary>
internal readonly record struct DownloadProgress
{
    /// <summary>
    /// Gets the number of bytes downloaded so far.
    /// </summary>
    public long BytesDownloaded { get; init; }

    /// <summary>
    /// Gets the total number of bytes to download, or -1 if unknown.
    /// </summary>
    public long TotalBytes { get; init; }

    /// <summary>
    /// Gets the current download speed in bytes per second, or null if not calculated.
    /// </summary>
    public double? BytesPerSecond { get; init; }

    /// <summary>
    /// Gets the download progress as a percentage (0-100), or null if total is unknown.
    /// </summary>
    public double? Percentage => TotalBytes > 0 ? (double)BytesDownloaded / TotalBytes * 100 : null;

    /// <summary>
    /// Gets the estimated time remaining, or null if speed is unknown.
    /// </summary>
    public TimeSpan? EstimatedTimeRemaining
    {
        get
        {
            if (BytesPerSecond is not { } speed || speed <= 0 || TotalBytes <= 0)
                return null;

            var remaining = TotalBytes - BytesDownloaded;
            return TimeSpan.FromSeconds(remaining / speed);
        }
    }

    public DownloadProgress(long bytesDownloaded, long totalBytes, double? bytesPerSecond = null)
    {
        BytesDownloaded = bytesDownloaded;
        TotalBytes = totalBytes;
        BytesPerSecond = bytesPerSecond;
    }
}
