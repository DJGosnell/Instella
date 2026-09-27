using System.Collections.Concurrent;
using Instella.Server.Storage;

namespace Instella.Server.Tests.Infrastructure;

/// <summary>
/// In-memory storage provider for testing.
/// Tracks all operations for assertions.
/// </summary>
public class TestStorageProvider : IStorageProvider
{
    private readonly ConcurrentDictionary<string, StoredContent> _files = new();
    private readonly List<StorageOperation> _operations = new();
    private readonly object _lock = new();

    public IReadOnlyList<StorageOperation> Operations
    {
        get
        {
            lock (_lock)
            {
                return _operations.ToList();
            }
        }
    }

    public IReadOnlyDictionary<string, StoredContent> Files => _files;

    /// <summary>Puts <paramref name="key"/> in storage as if written at <paramref name="createdAt"/>.</summary>
    public void Seed(string key, byte[] data, DateTime createdAt) => _files[key] = new StoredContent(data, createdAt);

    public bool ShouldFail { get; set; }
    public string? FailureMessage { get; set; }

    public Task<StorageResult> UploadAsync(string key, Stream content, CancellationToken ct = default)
    {
        RecordOperation(new StorageOperation(nameof(UploadAsync), key));

        if (ShouldFail)
        {
            return Task.FromResult(new StorageResult
            {
                Success = false,
                Error = FailureMessage ?? "Test failure"
            });
        }

        using var ms = new MemoryStream();
        content.CopyTo(ms);
        var data = ms.ToArray();

        _files[key] = new StoredContent(data, DateTime.UtcNow);

        return Task.FromResult(new StorageResult { Success = true });
    }

    public Task<Stream?> DownloadAsync(string key, CancellationToken ct = default)
    {
        RecordOperation(new StorageOperation(nameof(DownloadAsync), key));

        if (ShouldFail)
            return Task.FromResult<Stream?>(null);

        if (!_files.TryGetValue(key, out var content))
            return Task.FromResult<Stream?>(null);

        return Task.FromResult<Stream?>(new MemoryStream(content.Data));
    }

    /// <summary>Runs before each delete, for tests that interleave work with a purge.</summary>
    public Func<string, Task>? BeforeDelete { get; set; }

    public async Task<bool> DeleteAsync(string key, CancellationToken ct = default)
    {
        RecordOperation(new StorageOperation(nameof(DeleteAsync), key));
        if (BeforeDelete is { } hook) await hook(key);

        if (ShouldFail)
            return false;

        return _files.TryRemove(key, out _);
    }

    /// <summary>When set, <see cref="ExistsAsync"/> throws it: storage that cannot answer.</summary>
    public Exception? ExistsFailure { get; set; }

    public Task<bool> ExistsAsync(string key, CancellationToken ct = default)
    {
        RecordOperation(new StorageOperation(nameof(ExistsAsync), key));
        if (ExistsFailure is { } failure) return Task.FromException<bool>(failure);
        return Task.FromResult(_files.ContainsKey(key));
    }

    public Task<StorageInfo?> GetInfoAsync(string key, CancellationToken ct = default)
    {
        RecordOperation(new StorageOperation(nameof(GetInfoAsync), key));

        if (!_files.TryGetValue(key, out var content))
            return Task.FromResult<StorageInfo?>(null);

        return Task.FromResult<StorageInfo?>(new StorageInfo
        {
            Key = key,
            Size = content.Data.Length,
            LastModified = content.CreatedAt
        });
    }

    public Task<string?> GetPresignedUrlAsync(string key, TimeSpan expiry, string? fileName = null, CancellationToken ct = default)
    {
        RecordOperation(new StorageOperation(nameof(GetPresignedUrlAsync), key));

        if (!_files.ContainsKey(key))
            return Task.FromResult<string?>(null);

        return Task.FromResult<string?>($"https://test-presigned/{key}?expiry={expiry.TotalSeconds}&fileName={fileName}");
    }

    public Task<Stream?> DownloadRangeAsync(string key, long from, long to, CancellationToken ct = default)
    {
        RecordOperation(new StorageOperation($"{nameof(DownloadRangeAsync)}({from}-{to})", key));

        if (!_files.TryGetValue(key, out var content))
            return Task.FromResult<Stream?>(null);

        var length = (int)(to - from + 1);
        var rangeData = new byte[length];
        Array.Copy(content.Data, from, rangeData, 0, length);

        return Task.FromResult<Stream?>(new MemoryStream(rangeData));
    }

    public void Clear()
    {
        _files.Clear();
        lock (_lock)
        {
            _operations.Clear();
        }
    }

    private void RecordOperation(StorageOperation operation)
    {
        lock (_lock)
        {
            _operations.Add(operation);
        }
    }

    public async IAsyncEnumerable<StorageInfo> ListAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        foreach (var (key, content) in _files.ToArray())
            yield return new StorageInfo { Key = key, Size = content.Data.Length, LastModified = content.CreatedAt };
    }
}

public record StoredContent(byte[] Data, DateTime CreatedAt);

public record StorageOperation(string Method, string Key);
