namespace Instella.Server.Storage;

/// <summary>
/// Abstraction for file storage operations.
/// </summary>
public interface IStorageProvider
{
    /// <summary>
    /// Uploads a file to storage.
    /// </summary>
    /// <param name="key">Storage key (path)</param>
    /// <param name="content">File content stream</param>
    /// <param name="ct">Cancellation token</param>
    Task<StorageResult> UploadAsync(string key, Stream content, CancellationToken ct = default);

    /// <summary>
    /// Downloads a file from storage.
    /// </summary>
    /// <param name="key">Storage key (path)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>File stream or null if not found</returns>
    Task<Stream?> DownloadAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// Deletes a file from storage.
    /// </summary>
    /// <param name="key">Storage key (path)</param>
    /// <param name="ct">Cancellation token</param>
    Task<bool> DeleteAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// Checks if a file exists in storage.
    /// </summary>
    /// <param name="key">Storage key (path)</param>
    /// <param name="ct">Cancellation token</param>
    Task<bool> ExistsAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// Gets information about a stored file.
    /// </summary>
    /// <param name="key">Storage key (path)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>File info or null if not found</returns>
    Task<StorageInfo?> GetInfoAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// Gets a presigned URL for direct download (if supported).
    /// </summary>
    /// <param name="key">Storage key (path)</param>
    /// <param name="expiry">URL expiration time</param>
    /// <param name="fileName">Optional download filename</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Presigned URL or null if not supported</returns>
    Task<string?> GetPresignedUrlAsync(string key, TimeSpan expiry, string? fileName = null, CancellationToken ct = default);

    /// <summary>
    /// Downloads a range of bytes from a file.
    /// </summary>
    /// <param name="key">Storage key (path)</param>
    /// <param name="from">Start byte offset</param>
    /// <param name="to">End byte offset (inclusive)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>File stream for the range or null if not found</returns>
    Task<Stream?> DownloadRangeAsync(string key, long from, long to, CancellationToken ct = default);

    /// <summary>
    /// Every stored object (key, size, last write). The orphan sweeper uses it to find blobs no
    /// database row refers to. The default lists nothing.
    /// </summary>
    async IAsyncEnumerable<StorageInfo> ListAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }
}
