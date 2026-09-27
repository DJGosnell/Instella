namespace Instella.Server.Storage;

/// <summary>
/// Local filesystem storage provider.
/// </summary>
public class LocalStorageProvider : IStorageProvider
{
    private readonly string _basePath;

    public LocalStorageProvider(string basePath)
    {
        _basePath = basePath;
        Directory.CreateDirectory(_basePath);
    }

    private string GetFullPath(string key) => Path.Combine(_basePath, key.Replace('/', Path.DirectorySeparatorChar));

    public async Task<StorageResult> UploadAsync(string key, Stream content, CancellationToken ct = default)
    {
        try
        {
            var fullPath = GetFullPath(key);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            // Written aside, then renamed into place: readers never see a partial blob, and two
            // concurrent uploads of the same content-addressed key do not collide on one file.
            var temp = $"{fullPath}.{Guid.NewGuid():N}.tmp";
            try
            {
                await using (var fileStream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                    await content.CopyToAsync(fileStream, ct);
                try
                {
                    File.Move(temp, fullPath, overwrite: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && File.Exists(fullPath))
                {
                    // Another upload of the same key won, or a reader holds it open (Windows
                    // reports both as a sharing or access error). Keys are content hashes, so
                    // the bytes already there are the same.
                }
            }
            finally
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }

            var fileInfo = new FileInfo(fullPath);
            return StorageResult.Ok(key, fileInfo.Length);
        }
        catch (Exception ex)
        {
            return StorageResult.Fail(ex.Message);
        }
    }

    public Task<Stream?> DownloadAsync(string key, CancellationToken ct = default)
    {
        var fullPath = GetFullPath(key);
        if (!File.Exists(fullPath))
            return Task.FromResult<Stream?>(null);

        Stream stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        return Task.FromResult<Stream?>(stream);
    }

    public Task<bool> DeleteAsync(string key, CancellationToken ct = default)
    {
        var fullPath = GetFullPath(key);
        if (!File.Exists(fullPath))
            return Task.FromResult(false);

        File.Delete(fullPath);

        // Clean up empty directories
        var directory = Path.GetDirectoryName(fullPath);
        while (!string.IsNullOrEmpty(directory) && directory != _basePath)
        {
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
                directory = Path.GetDirectoryName(directory);
            }
            else
            {
                break;
            }
        }

        return Task.FromResult(true);
    }

    public Task<bool> ExistsAsync(string key, CancellationToken ct = default)
    {
        var fullPath = GetFullPath(key);
        return Task.FromResult(File.Exists(fullPath));
    }

    public Task<StorageInfo?> GetInfoAsync(string key, CancellationToken ct = default)
    {
        var fullPath = GetFullPath(key);
        if (!File.Exists(fullPath))
            return Task.FromResult<StorageInfo?>(null);

        var fileInfo = new FileInfo(fullPath);
        return Task.FromResult<StorageInfo?>(new StorageInfo
        {
            Key = key,
            Size = fileInfo.Length,
            LastModified = fileInfo.LastWriteTimeUtc
        });
    }

    public async IAsyncEnumerable<StorageInfo> ListAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        if (!Directory.Exists(_basePath)) yield break;
        foreach (var file in Directory.EnumerateFiles(_basePath, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var info = new FileInfo(file);
            yield return new StorageInfo
            {
                Key = Path.GetRelativePath(_basePath, file).Replace(Path.DirectorySeparatorChar, '/'),
                Size = info.Length,
                LastModified = info.LastWriteTimeUtc,
            };
        }
    }

    public Task<string?> GetPresignedUrlAsync(string key, TimeSpan expiry, string? fileName = null, CancellationToken ct = default)
    {
        // Local storage doesn't support presigned URLs - server must proxy
        return Task.FromResult<string?>(null);
    }

    public Task<Stream?> DownloadRangeAsync(string key, long from, long to, CancellationToken ct = default)
    {
        var fullPath = GetFullPath(key);
        if (!File.Exists(fullPath))
            return Task.FromResult<Stream?>(null);

        var fileStream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        fileStream.Seek(from, SeekOrigin.Begin);

        // Return a limited stream that only reads the requested range
        var length = to - from + 1;
        return Task.FromResult<Stream?>(new RangeStream(fileStream, length));
    }
}

/// <summary>
/// Stream wrapper that limits reading to a specific length.
/// </summary>
internal class RangeStream : Stream
{
    private readonly Stream _inner;
    private readonly long _length;
    private long _position;

    public RangeStream(Stream inner, long length)
    {
        _inner = inner;
        _length = length;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position { get => _position; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var remaining = _length - _position;
        if (remaining <= 0) return 0;

        var toRead = (int)Math.Min(count, remaining);
        var read = _inner.Read(buffer, offset, toRead);
        _position += read;
        return read;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        var remaining = _length - _position;
        if (remaining <= 0) return 0;

        var toRead = (int)Math.Min(count, remaining);
        var read = await _inner.ReadAsync(buffer.AsMemory(offset, toRead), ct);
        _position += read;
        return read;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}
