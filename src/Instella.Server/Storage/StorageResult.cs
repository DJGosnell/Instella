namespace Instella.Server.Storage;

/// <summary>
/// Result of an upload operation.
/// </summary>
public class StorageResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string? Key { get; init; }
    public long Size { get; init; }

    public static StorageResult Ok(string key, long size) => new() { Success = true, Key = key, Size = size };
    public static StorageResult Fail(string error) => new() { Success = false, Error = error };
}

/// <summary>
/// Information about a stored file.
/// </summary>
public class StorageInfo
{
    public required string Key { get; init; }
    public long Size { get; init; }
    public DateTime LastModified { get; init; }
}
