using Instella.Core.Platform;

namespace Instella.Installer.Testing;

/// <summary>
/// In-memory <see cref="IFakeRegistry"/>. Keyed as (hive, keyPath, name) with
/// <see cref="StringComparer.OrdinalIgnoreCase"/> matching Windows registry
/// semantics on every host so tests behave identically cross-platform.
/// </summary>
public sealed class InMemoryRegistry : IFakeRegistry
{
    private readonly Dictionary<(RegistryHive, string), Dictionary<string, (InstellaRegistryValueKind Kind, object Value)>> _keys
        = new(KeyComparer.Instance);
    private readonly object _lock = new();

    /// <inheritdoc />
    public void Set(RegistryHive hive, string keyPath, string name, InstellaRegistryValueKind kind, object value)
    {
        ArgumentException.ThrowIfNullOrEmpty(keyPath);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);

        lock (_lock)
        {
            if (!_keys.TryGetValue((hive, keyPath), out var values))
            {
                values = new Dictionary<string, (InstellaRegistryValueKind, object)>(StringComparer.OrdinalIgnoreCase);
                _keys[(hive, keyPath)] = values;
            }
            values[name] = (kind, value);
        }
    }

    /// <inheritdoc />
    public object? Get(RegistryHive hive, string keyPath, string name)
    {
        lock (_lock)
        {
            if (!_keys.TryGetValue((hive, keyPath), out var values)) return null;
            return values.TryGetValue(name, out var entry) ? entry.Value : null;
        }
    }

    /// <inheritdoc />
    public bool Contains(RegistryHive hive, string keyPath)
    {
        lock (_lock) return _keys.ContainsKey((hive, keyPath));
    }

    /// <inheritdoc />
    public bool Delete(RegistryHive hive, string keyPath, string name)
    {
        lock (_lock)
        {
            if (!_keys.TryGetValue((hive, keyPath), out var values)) return false;
            var removed = values.Remove(name);
            if (values.Count == 0) _keys.Remove((hive, keyPath));
            return removed;
        }
    }

    /// <inheritdoc />
    public bool DeleteKey(RegistryHive hive, string keyPath)
    {
        lock (_lock) return _keys.Remove((hive, keyPath));
    }

    /// <inheritdoc />
    public IReadOnlyList<RegistryValueRecord> Snapshot()
    {
        lock (_lock)
        {
            var snapshot = new List<RegistryValueRecord>();
            foreach (var ((hive, keyPath), values) in _keys)
            {
                foreach (var (name, (kind, value)) in values)
                    snapshot.Add(new RegistryValueRecord(hive, keyPath, name, kind, value));
            }
            return snapshot;
        }
    }

    private sealed class KeyComparer : IEqualityComparer<(RegistryHive, string)>
    {
        public static readonly KeyComparer Instance = new();

        public bool Equals((RegistryHive, string) x, (RegistryHive, string) y)
            => x.Item1 == y.Item1 && StringComparer.OrdinalIgnoreCase.Equals(x.Item2, y.Item2);

        public int GetHashCode((RegistryHive, string) obj)
            => HashCode.Combine(obj.Item1, StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Item2));
    }
}
