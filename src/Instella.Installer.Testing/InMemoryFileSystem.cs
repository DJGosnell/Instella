using System.Security.Cryptography;
using Instella.Core.FileSystem;

namespace Instella.Installer.Testing;

/// <summary>
/// In-memory <see cref="IFakeFileSystem"/> implementation. Backed by two
/// dictionaries — one for files (path → bytes), one for explicitly-created
/// directories — keyed by the platform's native path comparer
/// (<see cref="StringComparer.OrdinalIgnoreCase"/> on Windows, ordinal
/// elsewhere). Parent directories are implicit: any file under <c>C:\a\b</c>
/// makes <c>C:\a</c> and <c>C:\a\b</c> "exist" for
/// <see cref="IFileSystem.DirectoryExists"/> purposes.
/// </summary>
/// <remarks>
/// All operations complete synchronously — the async shape is a concession to
/// the <see cref="IFileSystem"/> contract. No I/O, no locks, no retries.
/// </remarks>
public sealed class InMemoryFileSystem : IFakeFileSystem
{
    private static readonly StringComparer s_pathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly Dictionary<string, byte[]> _files = new(s_pathComparer);
    private readonly HashSet<string> _explicitDirs = new(s_pathComparer);
    private readonly Dictionary<string, UnixFileMode> _modes = new(s_pathComparer);
    private readonly object _lock = new();

    /// <summary>
    /// The mode last set on <paramref name="path"/> through
    /// <see cref="SetUnixFileModeAsync"/>, or null when none was set. Lets tests assert
    /// that executables were marked executable.
    /// </summary>
    public UnixFileMode? GetUnixFileMode(string path)
    {
        lock (_lock) return _modes.TryGetValue(Normalize(path), out var mode) ? mode : null;
    }

    /// <inheritdoc />
    public Task<FileSystemResult> SetUnixFileModeAsync(string path, UnixFileMode mode, CancellationToken ct)
    {
        var normalized = Normalize(path);
        lock (_lock)
        {
            if (!_files.ContainsKey(normalized))
                return Task.FromResult(FileSystemResult.Fail(new FileSystemError(
                    FileSystemErrorType.NotFound, $"file not found: {path}")));
            _modes[normalized] = mode;
        }
        return Task.FromResult(FileSystemResult.Ok());
    }

    /// <inheritdoc />
    public void AddFile(string path, byte[] contents)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(contents);
        var normalized = Normalize(path);
        lock (_lock)
        {
            _files[normalized] = (byte[])contents.Clone();
            AddImpliedDirectories(normalized);
        }
    }

    /// <inheritdoc />
    public void AddDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var normalized = Normalize(path);
        lock (_lock)
        {
            _explicitDirs.Add(normalized);
            AddImpliedDirectories(normalized);
        }
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, byte[]> Snapshot()
    {
        lock (_lock)
        {
            var copy = new Dictionary<string, byte[]>(_files.Count, s_pathComparer);
            foreach (var (k, v) in _files) copy[k] = (byte[])v.Clone();
            return copy;
        }
    }

    /// <inheritdoc />
    public Task<FileSystemResult> CopyFileAsync(string source, string dest, bool overwrite, CancellationToken ct)
    {
        var src = Normalize(source);
        var dst = Normalize(dest);
        lock (_lock)
        {
            if (!_files.TryGetValue(src, out var bytes))
                return Task.FromResult(FileSystemResult.Fail(new FileSystemError(
                    FileSystemErrorType.NotFound, $"source not found: {source}")));
            if (!overwrite && _files.ContainsKey(dst))
                return Task.FromResult(FileSystemResult.Fail(new FileSystemError(
                    FileSystemErrorType.Unknown, $"destination exists: {dest}")));
            _files[dst] = (byte[])bytes.Clone();
            AddImpliedDirectories(dst);
        }
        return Task.FromResult(FileSystemResult.Ok());
    }

    /// <inheritdoc />
    public Task<FileSystemResult> MoveFileAsync(string source, string dest, bool overwrite, CancellationToken ct)
    {
        var src = Normalize(source);
        var dst = Normalize(dest);
        lock (_lock)
        {
            if (!_files.TryGetValue(src, out var bytes))
                return Task.FromResult(FileSystemResult.Fail(new FileSystemError(
                    FileSystemErrorType.NotFound, $"source not found: {source}")));
            if (!overwrite && _files.ContainsKey(dst))
                return Task.FromResult(FileSystemResult.Fail(new FileSystemError(
                    FileSystemErrorType.Unknown, $"destination exists: {dest}")));
            _files.Remove(src);
            _files[dst] = bytes;
            // A rename keeps the file's mode, as on a real file system.
            if (_modes.Remove(src, out var mode)) _modes[dst] = mode;
            else _modes.Remove(dst);
            AddImpliedDirectories(dst);
        }
        return Task.FromResult(FileSystemResult.Ok());
    }

    /// <inheritdoc />
    public Task<FileSystemResult> DeleteFileAsync(string path, CancellationToken ct)
    {
        var normalized = Normalize(path);
        lock (_lock)
        {
            _files.Remove(normalized);
            _modes.Remove(normalized);
        }
        return Task.FromResult(FileSystemResult.Ok());
    }

    /// <inheritdoc />
    public Task<FileSystemResult> CreateDirectoryAsync(string path, CancellationToken ct)
    {
        var normalized = Normalize(path);
        lock (_lock)
        {
            _explicitDirs.Add(normalized);
            AddImpliedDirectories(normalized);
        }
        return Task.FromResult(FileSystemResult.Ok());
    }

    /// <inheritdoc />
    public Task<FileSystemResult> DeleteDirectoryAsync(string path, bool recursive, CancellationToken ct)
    {
        var prefix = Normalize(path);
        var prefixWithSep = prefix + Path.DirectorySeparatorChar;
        lock (_lock)
        {
            _explicitDirs.Remove(prefix);
            _explicitDirs.RemoveWhere(d =>
                d.StartsWith(prefixWithSep, s_pathComparer == StringComparer.OrdinalIgnoreCase
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal));

            if (recursive)
            {
                var toRemove = new List<string>();
                foreach (var key in _files.Keys)
                {
                    if (s_pathComparer.Equals(key, prefix) ||
                        key.StartsWith(prefixWithSep, s_pathComparer == StringComparer.OrdinalIgnoreCase
                            ? StringComparison.OrdinalIgnoreCase
                            : StringComparison.Ordinal))
                    {
                        toRemove.Add(key);
                    }
                }
                foreach (var k in toRemove) _files.Remove(k);
            }
        }
        return Task.FromResult(FileSystemResult.Ok());
    }

    /// <inheritdoc />
    public Task<FileSystemResult<byte[]>> ReadAllBytesAsync(string path, CancellationToken ct)
    {
        var normalized = Normalize(path);
        lock (_lock)
        {
            if (!_files.TryGetValue(normalized, out var bytes))
                return Task.FromResult(FileSystemResult<byte[]>.Fail(new FileSystemError(
                    FileSystemErrorType.NotFound, $"file not found: {path}")));
            return Task.FromResult(FileSystemResult<byte[]>.Ok((byte[])bytes.Clone()));
        }
    }

    /// <inheritdoc />
    public Task<FileSystemResult> WriteAllBytesAsync(string path, byte[] data, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(data);
        var normalized = Normalize(path);
        lock (_lock)
        {
            _files[normalized] = (byte[])data.Clone();
            AddImpliedDirectories(normalized);
        }
        return Task.FromResult(FileSystemResult.Ok());
    }

    /// <inheritdoc />
    public Task<FileSystemResult<Stream>> OpenReadAsync(string path, CancellationToken ct)
    {
        var normalized = Normalize(path);
        lock (_lock)
        {
            if (!_files.TryGetValue(normalized, out var bytes))
                return Task.FromResult(FileSystemResult<Stream>.Fail(new FileSystemError(
                    FileSystemErrorType.NotFound, $"file not found: {path}")));
            Stream stream = new MemoryStream((byte[])bytes.Clone(), writable: false);
            return Task.FromResult(FileSystemResult<Stream>.Ok(stream));
        }
    }

    /// <inheritdoc />
    public Task<FileSystemResult<Stream>> OpenWriteAsync(string path, CancellationToken ct)
    {
        var normalized = Normalize(path);
        Stream stream = new WritableFileStream(this, normalized);
        lock (_lock) AddImpliedDirectories(normalized);
        return Task.FromResult(FileSystemResult<Stream>.Ok(stream));
    }

    /// <inheritdoc />
    public bool Exists(string path)
    {
        var normalized = Normalize(path);
        lock (_lock) return _files.ContainsKey(normalized);
    }

    /// <inheritdoc />
    public bool DirectoryExists(string path)
    {
        var normalized = Normalize(path);
        lock (_lock)
        {
            if (_explicitDirs.Contains(normalized)) return true;
            var prefix = normalized + Path.DirectorySeparatorChar;
            foreach (var file in _files.Keys)
            {
                if (file.StartsWith(prefix, s_pathComparer == StringComparer.OrdinalIgnoreCase
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
                    return true;
            }
            foreach (var dir in _explicitDirs)
            {
                if (dir.StartsWith(prefix, s_pathComparer == StringComparer.OrdinalIgnoreCase
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
                    return true;
            }
            return false;
        }
    }

    /// <inheritdoc />
    public Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        var normalized = Normalize(path);
        byte[] bytes;
        lock (_lock)
        {
            if (!_files.TryGetValue(normalized, out var found))
                throw new FileNotFoundException("file not found in fake filesystem", path);
            bytes = found;
        }
        var hash = SHA256.HashData(bytes);
        return Task.FromResult(Convert.ToHexStringLower(hash));
    }

    /// <inheritdoc />
    public IEnumerable<string> EnumerateFiles(string path, string searchPattern = "*", bool recursive = false)
    {
        var normalized = Normalize(path);
        var prefixWithSep = normalized + Path.DirectorySeparatorChar;
        var comparison = s_pathComparer == StringComparer.OrdinalIgnoreCase
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        List<string> snapshot;
        lock (_lock) snapshot = new List<string>(_files.Keys);

        foreach (var file in snapshot)
        {
            var isUnderDir = file.StartsWith(prefixWithSep, comparison);
            if (!isUnderDir) continue;

            var relative = file.Substring(prefixWithSep.Length);
            if (!recursive && relative.Contains(Path.DirectorySeparatorChar)) continue;

            var fileName = Path.GetFileName(file);
            if (MatchesPattern(fileName, searchPattern))
                yield return file;
        }
    }

    /// <inheritdoc />
    public long GetFileSize(string path)
    {
        var normalized = Normalize(path);
        lock (_lock) return _files.TryGetValue(normalized, out var bytes) ? bytes.Length : -1;
    }

    internal void WriteBytesInternal(string normalizedPath, byte[] bytes)
    {
        lock (_lock)
        {
            _files[normalizedPath] = bytes;
            AddImpliedDirectories(normalizedPath);
        }
    }

    private void AddImpliedDirectories(string normalizedPath)
    {
        var dir = Path.GetDirectoryName(normalizedPath);
        while (!string.IsNullOrEmpty(dir))
        {
            _explicitDirs.Add(dir);
            dir = Path.GetDirectoryName(dir);
        }
    }

    private static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        return Path.TrimEndingDirectorySeparator(full);
    }

    private static bool MatchesPattern(string name, string pattern)
    {
        if (pattern == "*" || string.IsNullOrEmpty(pattern)) return true;
        if (!pattern.Contains('*') && !pattern.Contains('?'))
            return s_pathComparer.Equals(name, pattern);

        // Minimal glob: '*' matches any sequence, '?' matches one char.
        return MatchGlob(name, pattern, 0, 0);
    }

    private static bool MatchGlob(string s, string p, int si, int pi)
    {
        while (pi < p.Length)
        {
            if (p[pi] == '*')
            {
                while (pi + 1 < p.Length && p[pi + 1] == '*') pi++;
                if (pi == p.Length - 1) return true;
                for (var i = si; i <= s.Length; i++)
                    if (MatchGlob(s, p, i, pi + 1)) return true;
                return false;
            }
            if (si >= s.Length) return false;
            if (p[pi] != '?')
            {
                var a = s_pathComparer == StringComparer.OrdinalIgnoreCase ? char.ToLowerInvariant(s[si]) : s[si];
                var b = s_pathComparer == StringComparer.OrdinalIgnoreCase ? char.ToLowerInvariant(p[pi]) : p[pi];
                if (a != b) return false;
            }
            si++;
            pi++;
        }
        return si == s.Length;
    }

    private sealed class WritableFileStream : MemoryStream
    {
        private readonly InMemoryFileSystem _owner;
        private readonly string _normalizedPath;
        private bool _committed;

        public WritableFileStream(InMemoryFileSystem owner, string normalizedPath)
        {
            _owner = owner;
            _normalizedPath = normalizedPath;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_committed)
            {
                _owner.WriteBytesInternal(_normalizedPath, ToArray());
                _committed = true;
            }
            base.Dispose(disposing);
        }
    }
}
