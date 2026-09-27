using Instella.Core.FileSystem;

namespace Instella.Installer.Testing;

/// <summary>
/// Test-side extension of <see cref="IFileSystem"/>. Adds seeding helpers and
/// snapshot inspection so harness tests can set up preconditions without
/// touching the real disk, and verify step side-effects after execution.
/// </summary>
public interface IFakeFileSystem : IFileSystem
{
    /// <summary>
    /// Seed a file into the fake store. Creates any implied parent directories.
    /// Overwrites any existing file at the same path.
    /// </summary>
    void AddFile(string path, byte[] contents);

    /// <summary>
    /// Seed an empty directory into the fake store. Useful when a step probes
    /// <see cref="IFileSystem.DirectoryExists"/> before creating its own
    /// contents.
    /// </summary>
    void AddDirectory(string path);

    /// <summary>
    /// Immutable snapshot of every file present in the fake store at the
    /// moment of the call. Keyed by the normalized (full-path,
    /// trimmed-separator) path the fake uses internally.
    /// </summary>
    IReadOnlyDictionary<string, byte[]> Snapshot();
}
