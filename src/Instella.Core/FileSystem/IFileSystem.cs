namespace Instella.Core.FileSystem;

/// <summary>
/// Abstraction for file system operations.
/// All operations return result types instead of throwing exceptions for common errors.
/// </summary>
public interface IFileSystem
{
    /// <summary>Copies a file from source to destination.</summary>
    /// <param name="source">Source file path.</param>
    /// <param name="dest">Destination file path.</param>
    /// <param name="overwrite">Whether to overwrite if destination exists.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<FileSystemResult> CopyFileAsync(string source, string dest, bool overwrite, CancellationToken ct);

    /// <summary>Moves a file from source to destination.</summary>
    /// <param name="source">Source file path.</param>
    /// <param name="dest">Destination file path.</param>
    /// <param name="overwrite">Whether to overwrite if destination exists.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<FileSystemResult> MoveFileAsync(string source, string dest, bool overwrite, CancellationToken ct);

    /// <summary>Deletes a file.</summary>
    /// <param name="path">Path to the file.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<FileSystemResult> DeleteFileAsync(string path, CancellationToken ct);

    /// <summary>Creates a directory and all parent directories.</summary>
    /// <param name="path">Path to the directory.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<FileSystemResult> CreateDirectoryAsync(string path, CancellationToken ct);

    /// <summary>Deletes a directory.</summary>
    /// <param name="path">Path to the directory.</param>
    /// <param name="recursive">Whether to delete contents recursively.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<FileSystemResult> DeleteDirectoryAsync(string path, bool recursive, CancellationToken ct);

    /// <summary>Reads all bytes from a file.</summary>
    /// <param name="path">Path to the file.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<FileSystemResult<byte[]>> ReadAllBytesAsync(string path, CancellationToken ct);

    /// <summary>Writes all bytes to a file.</summary>
    /// <param name="path">Path to the file.</param>
    /// <param name="data">Bytes to write.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<FileSystemResult> WriteAllBytesAsync(string path, byte[] data, CancellationToken ct);

    /// <summary>Opens a file for reading.</summary>
    /// <param name="path">Path to the file.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<FileSystemResult<Stream>> OpenReadAsync(string path, CancellationToken ct);

    /// <summary>Opens a file for writing (creates or overwrites).</summary>
    /// <param name="path">Path to the file.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<FileSystemResult<Stream>> OpenWriteAsync(string path, CancellationToken ct);

    /// <summary>
    /// Sets POSIX permission bits (for example <c>0755</c> on executables). A no-op on
    /// Windows. The default implementation does nothing, so existing implementations
    /// keep compiling; real and in-memory file systems override it.
    /// </summary>
    /// <param name="path">Path to the file.</param>
    /// <param name="mode">Permission bits to set.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<FileSystemResult> SetUnixFileModeAsync(string path, UnixFileMode mode, CancellationToken ct) =>
        Task.FromResult(FileSystemResult.Ok());

    /// <summary>Checks if a file exists.</summary>
    /// <param name="path">Path to the file.</param>
    bool Exists(string path);

    /// <summary>Checks if a directory exists.</summary>
    /// <param name="path">Path to the directory.</param>
    bool DirectoryExists(string path);

    /// <summary>Computes the SHA256 hash of a file.</summary>
    /// <param name="path">Path to the file.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>SHA256 hash as a lowercase hex string.</returns>
    Task<string> ComputeSha256Async(string path, CancellationToken ct);

    /// <summary>Gets all file paths in a directory.</summary>
    /// <param name="path">Path to the directory.</param>
    /// <param name="searchPattern">Search pattern (e.g., "*.dll").</param>
    /// <param name="recursive">Whether to search recursively.</param>
    /// <returns>Enumerable of file paths.</returns>
    IEnumerable<string> EnumerateFiles(string path, string searchPattern = "*", bool recursive = false);

    /// <summary>Gets the size of a file in bytes.</summary>
    /// <param name="path">Path to the file.</param>
    /// <returns>File size, or -1 if file doesn't exist.</returns>
    long GetFileSize(string path);
}
