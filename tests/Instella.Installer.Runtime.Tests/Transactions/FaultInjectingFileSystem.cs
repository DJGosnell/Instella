using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.FileSystem;

namespace Instella.Installer.Runtime.Tests.Transactions;

/// <summary>
/// Wraps a file system and throws <see cref="IOException"/> on the <see cref="FailAt"/>-th
/// mutating call (0-based), simulating a crash or a hard I/O failure at that point.
/// </summary>
internal sealed class FaultInjectingFileSystem(IFileSystem inner) : IFileSystem
{
    /// <summary>Index of the mutating call that fails; null disables faults.</summary>
    public int? FailAt { get; set; }

    /// <summary>Mutating calls seen since the last <see cref="Arm"/>.</summary>
    public int MutatingCalls { get; private set; }

    public void Arm(int? failAt)
    {
        FailAt = failAt;
        MutatingCalls = 0;
    }

    private void Tick()
    {
        var n = MutatingCalls++;
        if (FailAt == n)
            throw new IOException($"injected fault at mutating call {n}");
    }

    public Task<FileSystemResult> CopyFileAsync(string source, string dest, bool overwrite, CancellationToken ct) { Tick(); return inner.CopyFileAsync(source, dest, overwrite, ct); }
    public Task<FileSystemResult> MoveFileAsync(string source, string dest, bool overwrite, CancellationToken ct) { Tick(); return inner.MoveFileAsync(source, dest, overwrite, ct); }
    public Task<FileSystemResult> DeleteFileAsync(string path, CancellationToken ct) { Tick(); return inner.DeleteFileAsync(path, ct); }
    public Task<FileSystemResult> CreateDirectoryAsync(string path, CancellationToken ct) { Tick(); return inner.CreateDirectoryAsync(path, ct); }
    public Task<FileSystemResult> DeleteDirectoryAsync(string path, bool recursive, CancellationToken ct) { Tick(); return inner.DeleteDirectoryAsync(path, recursive, ct); }
    public Task<FileSystemResult<byte[]>> ReadAllBytesAsync(string path, CancellationToken ct) => inner.ReadAllBytesAsync(path, ct);
    public Task<FileSystemResult> WriteAllBytesAsync(string path, byte[] data, CancellationToken ct) { Tick(); return inner.WriteAllBytesAsync(path, data, ct); }
    public Task<FileSystemResult<Stream>> OpenReadAsync(string path, CancellationToken ct) => inner.OpenReadAsync(path, ct);
    public Task<FileSystemResult<Stream>> OpenWriteAsync(string path, CancellationToken ct) { Tick(); return inner.OpenWriteAsync(path, ct); }
    public Task<FileSystemResult> SetUnixFileModeAsync(string path, UnixFileMode mode, CancellationToken ct) { Tick(); return inner.SetUnixFileModeAsync(path, mode, ct); }
    public bool Exists(string path) => inner.Exists(path);
    public bool DirectoryExists(string path) => inner.DirectoryExists(path);
    public Task<string> ComputeSha256Async(string path, CancellationToken ct) => inner.ComputeSha256Async(path, ct);
    public IEnumerable<string> EnumerateFiles(string path, string searchPattern = "*", bool recursive = false) => inner.EnumerateFiles(path, searchPattern, recursive);
    public long GetFileSize(string path) => inner.GetFileSize(path);
}
