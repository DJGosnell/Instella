using Instella.Core.Platform;

namespace Instella.Installer.Testing;

/// <summary>
/// Registry fake surface. Harness tests seed preconditions via
/// <see cref="Set"/>, observe side-effects via <see cref="Get"/> /
/// <see cref="Contains"/>, and take a full snapshot for assertions via
/// <see cref="Snapshot"/>.
/// </summary>
/// <remarks>
/// Unlike the real <c>WindowsPlatformServices</c>, the fake routes on ALL
/// platforms, so tests of registry-touching steps also run on macOS and
/// Linux without special-casing.
/// </remarks>
public interface IFakeRegistry
{
    /// <summary>
    /// Record a registry value write. Called both by the harness consumer (to
    /// seed state) and by <see cref="FakePlatformServices.WriteRegistryValueAsync"/>
    /// on the code path under test. Existing values at the same
    /// hive/key/name are overwritten.
    /// </summary>
    void Set(RegistryHive hive, string keyPath, string name, InstellaRegistryValueKind kind, object value);

    /// <summary>
    /// Read a named value. Returns <see langword="null"/> when the value is
    /// absent. The return type mirrors whatever was originally written.
    /// </summary>
    object? Get(RegistryHive hive, string keyPath, string name);

    /// <summary>
    /// Tests whether a key exists in the fake registry. A key "exists" if any
    /// value has been written into it (the fake has no empty-key concept).
    /// </summary>
    bool Contains(RegistryHive hive, string keyPath);

    /// <summary>
    /// Delete a named value from a key. Returns true when a value was removed;
    /// false when the key / value was absent. Matches the real
    /// <c>IPlatformServices.DeleteRegistryValueAsync</c> contract.
    /// </summary>
    bool Delete(RegistryHive hive, string keyPath, string name);

    /// <summary>
    /// Delete a key (and every value under it). Returns true when any values
    /// were removed; false when the key was absent.
    /// </summary>
    bool DeleteKey(RegistryHive hive, string keyPath);

    /// <summary>
    /// Immutable snapshot of every value present in the fake registry at the
    /// moment of the call. Entries are returned in insertion order per key.
    /// </summary>
    IReadOnlyList<RegistryValueRecord> Snapshot();
}
