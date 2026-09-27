using System.Security.Cryptography;

namespace Instella.Core.FileSystem;

/// <summary>
/// Production implementation of IFileSystem that wraps the real file system.
/// </summary>
internal sealed class RealFileSystem : IFileSystem
{
    /// <summary>Singleton instance.</summary>
    public static RealFileSystem Instance { get; } = new();

    private RealFileSystem() { }

    private static readonly int[] RetryDelaysMs = [100, 250, 1000];

    public async Task<FileSystemResult> CopyFileAsync(string source, string dest, bool overwrite, CancellationToken ct)
    {
        var destDir = Path.GetDirectoryName(dest);
        if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
            Directory.CreateDirectory(destDir);

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                await using var sourceStream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                await using var destStream = new FileStream(dest, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                await sourceStream.CopyToAsync(destStream, ct);
                return FileSystemResult.Ok();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (IOException ex) when (attempt < RetryDelaysMs.Length && IsSharingViolation(ex))
            {
                await Task.Delay(RetryDelaysMs[attempt], ct);
            }
            catch (Exception ex)
            {
                return FileSystemResult.FromException(ex);
            }
        }
    }

    public async Task<FileSystemResult> MoveFileAsync(string source, string dest, bool overwrite, CancellationToken ct)
    {
        try
        {
            var destDir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                Directory.CreateDirectory(destDir);

            // A same-volume move is a real rename (MoveFileEx / rename(2)): atomic, and it
            // keeps the file's permission bits. Across devices .NET falls back to copy +
            // delete, and its Unix copy preserves the source mode.
            File.Move(source, dest, overwrite);
            await Task.CompletedTask;
            return FileSystemResult.Ok();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return FileSystemResult.FromException(ex);
        }
    }

    public Task<FileSystemResult> DeleteFileAsync(string path, CancellationToken ct)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
            return Task.FromResult(FileSystemResult.Ok());
        }
        catch (Exception ex)
        {
            return Task.FromResult(FileSystemResult.FromException(ex));
        }
    }

    public Task<FileSystemResult> CreateDirectoryAsync(string path, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(path);
            return Task.FromResult(FileSystemResult.Ok());
        }
        catch (Exception ex)
        {
            return Task.FromResult(FileSystemResult.FromException(ex));
        }
    }

    public Task<FileSystemResult> DeleteDirectoryAsync(string path, bool recursive, CancellationToken ct)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive);
            return Task.FromResult(FileSystemResult.Ok());
        }
        catch (Exception ex)
        {
            return Task.FromResult(FileSystemResult.FromException(ex));
        }
    }

    public async Task<FileSystemResult<byte[]>> ReadAllBytesAsync(string path, CancellationToken ct)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(path, ct);
            return FileSystemResult<byte[]>.Ok(bytes);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return FileSystemResult<byte[]>.FromException(ex);
        }
    }

    public async Task<FileSystemResult> WriteAllBytesAsync(string path, byte[] data, CancellationToken ct)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            await File.WriteAllBytesAsync(path, data, ct);
            return FileSystemResult.Ok();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return FileSystemResult.FromException(ex);
        }
    }

    public Task<FileSystemResult<Stream>> OpenReadAsync(string path, CancellationToken ct)
    {
        try
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            return Task.FromResult(FileSystemResult<Stream>.Ok(stream));
        }
        catch (Exception ex)
        {
            return Task.FromResult(FileSystemResult<Stream>.FromException(ex));
        }
    }

    public Task<FileSystemResult<Stream>> OpenWriteAsync(string path, CancellationToken ct)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
            return Task.FromResult(FileSystemResult<Stream>.Ok(stream));
        }
        catch (Exception ex)
        {
            return Task.FromResult(FileSystemResult<Stream>.FromException(ex));
        }
    }

    public Task<FileSystemResult> SetUnixFileModeAsync(string path, UnixFileMode mode, CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
            return Task.FromResult(FileSystemResult.Ok());
        try
        {
            File.SetUnixFileMode(path, mode);
            return Task.FromResult(FileSystemResult.Ok());
        }
        catch (Exception ex)
        {
            return Task.FromResult(FileSystemResult.FromException(ex));
        }
    }

    public bool Exists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexStringLower(hash);
    }

    public IEnumerable<string> EnumerateFiles(string path, string searchPattern = "*", bool recursive = false)
    {
        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        return Directory.EnumerateFiles(path, searchPattern, option);
    }

    public long GetFileSize(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? info.Length : -1;
    }

    private static bool IsSharingViolation(IOException ex)
    {
        // ERROR_SHARING_VIOLATION = 0x20, ERROR_LOCK_VIOLATION = 0x21
        const int sharingViolation = unchecked((int)0x80070020);
        const int lockViolation = unchecked((int)0x80070021);
        return ex.HResult == sharingViolation || ex.HResult == lockViolation;
    }
}
