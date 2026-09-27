using System.Diagnostics;
using Instella.Core.Platform;

namespace Instella.Installer.Testing;

/// <summary>
/// <see cref="IPlatformServices"/> implementation composed of an
/// <see cref="IFakeFileSystem"/> (reachable via the harness — this type is
/// registry-focused) and an <see cref="IFakeRegistry"/>. Routes
/// <see cref="WriteRegistryValueAsync"/>, <see cref="DeleteRegistryValueAsync"/>,
/// and <see cref="DeleteRegistryKeyAsync"/> through the fake on every
/// platform, unlike the real implementations where Linux/macOS return
/// <c>true</c> (no-op). All other surface methods record the call on a
/// per-kind list (exposed via <see cref="Shortcuts"/>, <see cref="PathEntries"/>,
/// etc.) and succeed — no attempt is made to mutate the host OS.
/// </summary>
/// <remarks>
/// <see cref="Platform"/> defaults to <see cref="TargetPlatform.Windows"/>
/// because the built-in registry step only runs when a Windows builder has
/// attached registry specs. Test authors needing macOS/Linux semantics pass
/// the platform into the constructor.
/// </remarks>
public sealed class FakePlatformServices : IPlatformServices
{
    /// <summary>The in-memory registry every registry call reads and writes.</summary>
    public IFakeRegistry Registry { get; }

    /// <summary>A Windows fake with an empty registry.</summary>
    public FakePlatformServices() : this(new InMemoryRegistry(), TargetPlatform.Windows) { }

    /// <summary>A fake for <paramref name="platform"/> with an empty registry.</summary>
    public FakePlatformServices(TargetPlatform platform) : this(new InMemoryRegistry(), platform) { }

    /// <summary>A fake for <paramref name="platform"/> over <paramref name="registry"/>.</summary>
    public FakePlatformServices(IFakeRegistry registry, TargetPlatform platform = TargetPlatform.Windows)
    {
        ArgumentNullException.ThrowIfNull(registry);
        Registry = registry;
        Platform = platform;
    }

    /// <inheritdoc />
    public TargetPlatform Platform { get; }

    /// <summary>Shortcuts created, in order.</summary>
    public List<ShortcutInfo> Shortcuts { get; } = new();
    /// <summary>Shortcuts removed, in order.</summary>
    public List<ShortcutInfo> ShortcutsRemoved { get; } = new();
    /// <summary>File associations registered, in order.</summary>
    public List<FileAssociationInfo> FileAssociations { get; } = new();
    /// <summary>File associations unregistered, in order.</summary>
    public List<(string Extension, string AppId)> FileAssociationsRemoved { get; } = new();
    /// <summary>PATH entries added, in order.</summary>
    public List<(string Directory, bool PerUser)> PathEntries { get; } = new();
    /// <summary>PATH entries removed, in order.</summary>
    public List<(string Directory, bool PerUser)> PathEntriesRemoved { get; } = new();
    /// <summary>Auto-start registrations, in order.</summary>
    public List<AutoStartInfo> AutoStarts { get; } = new();
    /// <summary>Auto-start registrations removed (by app id), in order.</summary>
    public List<string> AutoStartsRemoved { get; } = new();
    /// <summary>Installed Apps entries registered, in order.</summary>
    public List<UninstallEntryInfo> UninstallEntries { get; } = new();
    /// <summary>Installed Apps entries removed, in order.</summary>
    public List<(string AppId, bool PerUser)> UninstallEntriesRemoved { get; } = new();

    // Current state (what is registered right now), alongside the call logs above.
    private readonly HashSet<string> _shortcuts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _associations = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _path = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _autoStarts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<(string AppId, bool PerUser)> _uninstallEntries = new();

    /// <summary>
    /// What is registered right now: shortcuts, associations, PATH entries, auto-starts,
    /// Installed-Apps entries and registry values, each as a sorted list. Compare a snapshot
    /// taken before install with one taken after uninstall to prove nothing was left behind.
    /// </summary>
    public PlatformSnapshot Snapshot() => new(
        _shortcuts.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
        _associations.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
        _path.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
        _autoStarts.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
        _uninstallEntries.Select(e => e.PerUser ? e.AppId : e.AppId + " (machine)").Order(StringComparer.OrdinalIgnoreCase).ToArray(),
        Registry.Snapshot().Select(r => $"{r.Hive}\\{r.KeyPath}\\{r.ValueName}={r.Value}")
            .Order(StringComparer.OrdinalIgnoreCase).ToArray());

    private static string ShortcutKey(ShortcutInfo info) => $"{info.Location}:{info.Name}";
    private static string PathKey(string directory, bool perUser) => $"{(perUser ? "user" : "machine")}:{directory}";

    /// <inheritdoc />
    public string GetDefaultInstallPath(string appName, bool perUser)
    {
        var baseDir = perUser ? "C:\\Users\\Fake\\Local" : "C:\\Fake\\Install";
        return Path.Combine(baseDir, appName);
    }

    /// <inheritdoc />
    public Task<PlatformResult> CreateShortcutAsync(ShortcutInfo info, CancellationToken ct)
    {
        Shortcuts.Add(info);
        _shortcuts.Add(ShortcutKey(info));
        return Task.FromResult(PlatformResult.Ok);
    }

    /// <inheritdoc />
    public Task<PlatformResult> RemoveShortcutAsync(ShortcutInfo info, CancellationToken ct)
    {
        ShortcutsRemoved.Add(info);
        _shortcuts.Remove(ShortcutKey(info));
        return Task.FromResult(PlatformResult.Ok);
    }

    /// <inheritdoc />
    public Task<PlatformResult> RegisterFileAssociationAsync(FileAssociationInfo info, CancellationToken ct)
    {
        FileAssociations.Add(info);
        _associations.Add(info.Extension);
        return Task.FromResult(PlatformResult.Ok);
    }

    /// <inheritdoc />
    public Task<PlatformResult> UnregisterFileAssociationAsync(string extension, string appId, CancellationToken ct)
    {
        FileAssociationsRemoved.Add((extension, appId));
        _associations.Remove(extension);
        return Task.FromResult(PlatformResult.Ok);
    }

    /// <inheritdoc />
    public Task<PlatformResult> AddToPathAsync(string directory, bool perUser, CancellationToken ct)
    {
        PathEntries.Add((directory, perUser));
        _path.Add(PathKey(directory, perUser));
        return Task.FromResult(PlatformResult.Ok);
    }

    /// <inheritdoc />
    public Task<PlatformResult> RemoveFromPathAsync(string directory, bool perUser, CancellationToken ct)
    {
        PathEntriesRemoved.Add((directory, perUser));
        _path.Remove(PathKey(directory, perUser));
        return Task.FromResult(PlatformResult.Ok);
    }

    /// <inheritdoc />
    public Task<PlatformResult> ConfigureAutoStartAsync(AutoStartInfo info, CancellationToken ct)
    {
        AutoStarts.Add(info);
        _autoStarts.Add(info.AppId);
        return Task.FromResult(PlatformResult.Ok);
    }

    /// <inheritdoc />
    public Task<PlatformResult> RemoveAutoStartAsync(string appId, CancellationToken ct)
    {
        AutoStartsRemoved.Add(appId);
        _autoStarts.Remove(appId);
        return Task.FromResult(PlatformResult.Ok);
    }

    /// <inheritdoc />
    public IReadOnlyList<Process> GetRunningProcesses(string processName, string? installPath)
        => Array.Empty<Process>();

    /// <inheritdoc />
    public Task<PlatformResult> TerminateProcessAsync(Process process, TimeSpan timeout, CancellationToken ct)
        => Task.FromResult(PlatformResult.Ok);

    /// <inheritdoc />
    public Task<PlatformResult> UpdateShortcutAsync(string oldTarget, string newTarget, string shortcutPath, CancellationToken ct)
        => Task.FromResult(PlatformResult.Ok);

    /// <inheritdoc />
    public Task<PlatformResult> RegisterUninstallEntryAsync(UninstallEntryInfo info, CancellationToken ct)
    {
        UninstallEntries.Add(info);
        _uninstallEntries.Add((info.AppId, info.PerUser));
        // The values the installer probe reads to find an existing installation, in the hive
        // the real platform writes them to.
        var hive = info.PerUser ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;
        var key = UninstallEntryKeys.PathFor(info.AppId);
        Registry.Set(hive, key, "InstallLocation", InstellaRegistryValueKind.String, info.InstallLocation);
        Registry.Set(hive, key, "DisplayVersion", InstellaRegistryValueKind.String, info.DisplayVersion);
        Registry.Set(hive, key, "UninstallString", InstellaRegistryValueKind.String, info.UninstallCommand);
        Registry.Set(hive, key, "QuietUninstallString", InstellaRegistryValueKind.String, info.UninstallCommand + " --silent");
        return Task.FromResult(PlatformResult.Ok);
    }

    /// <inheritdoc />
    public Task<PlatformResult> UnregisterUninstallEntryAsync(string appId, bool perUser, CancellationToken ct)
    {
        UninstallEntriesRemoved.Add((appId, perUser));
        _uninstallEntries.Remove((appId, perUser));
        var hive = perUser ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;
        var key = UninstallEntryKeys.PathFor(appId);
        foreach (var name in new[] { "InstallLocation", "DisplayVersion", "UninstallString", "QuietUninstallString",
                                     "EstimatedSize" })
            Registry.Delete(hive, key, name);
        return Task.FromResult(PlatformResult.Ok);
    }

    /// <inheritdoc />
    public Task<PlatformResult> DeleteRegistryValueAsync(RegistryHive hive, string keyPath, string name, bool perUser, CancellationToken ct)
        => Task.FromResult(Registry.Delete(hive, keyPath, name) ? PlatformResult.Ok : PlatformResult.Fail($"no value '{name}' under '{keyPath}'"));

    /// <inheritdoc />
    public Task<PlatformResult> DeleteRegistryKeyAsync(RegistryHive hive, string keyPath, bool perUser, CancellationToken ct)
        => Task.FromResult(Registry.DeleteKey(hive, keyPath) ? PlatformResult.Ok : PlatformResult.Fail($"no key '{keyPath}'"));

    /// <inheritdoc />
    public Task<PlatformResult> WriteRegistryValueAsync(RegistryHive hive, string keyPath, string name, InstellaRegistryValueKind kind, object value, bool perUser, CancellationToken ct)
    {
        Registry.Set(hive, keyPath, name, kind, value);
        return Task.FromResult(PlatformResult.Ok);
    }

    /// <inheritdoc />
    public Task<RegistryValueData?> ReadRegistryValueAsync(RegistryHive hive, string keyPath, string name, bool perUser, CancellationToken ct)
    {
        foreach (var r in Registry.Snapshot())
        {
            if (r.Hive == hive && string.Equals(r.KeyPath, keyPath, StringComparison.OrdinalIgnoreCase)
                && string.Equals(r.ValueName, name, StringComparison.OrdinalIgnoreCase))
                return Task.FromResult<RegistryValueData?>(new RegistryValueData(r.Kind, r.Value));
        }
        return Task.FromResult<RegistryValueData?>(null);
    }

    /// <inheritdoc />
    public Task<bool> RegistryKeyExistsAsync(RegistryHive hive, string keyPath, bool perUser, CancellationToken ct)
        => Task.FromResult(Registry.Contains(hive, keyPath));

    /// <inheritdoc />
    public Task<PlatformResult> DeleteRegistryKeyIfEmptyAsync(RegistryHive hive, string keyPath, bool perUser, CancellationToken ct)
    {
        // The in-memory registry drops a key with its last value, so a key that still
        // exists here has values and is left alone.
        return Task.FromResult(Registry.Contains(hive, keyPath) ? PlatformResult.Fail($"'{keyPath}' is not empty") : PlatformResult.Ok);
    }
}

/// <summary>What a <see cref="FakePlatformServices"/> has registered at one moment.</summary>
public sealed record PlatformSnapshot(
    IReadOnlyList<string> Shortcuts,
    IReadOnlyList<string> FileAssociations,
    IReadOnlyList<string> PathEntries,
    IReadOnlyList<string> AutoStarts,
    IReadOnlyList<string> UninstallEntries,
    IReadOnlyList<string> RegistryValues)
{
    /// <summary>Structural equality over the lists (records compare list references otherwise).</summary>
    public bool SameAs(PlatformSnapshot other) =>
        Shortcuts.SequenceEqual(other.Shortcuts) && FileAssociations.SequenceEqual(other.FileAssociations)
        && PathEntries.SequenceEqual(other.PathEntries) && AutoStarts.SequenceEqual(other.AutoStarts)
        && UninstallEntries.SequenceEqual(other.UninstallEntries) && RegistryValues.SequenceEqual(other.RegistryValues);

    /// <summary>Every list, for assertion messages.</summary>
    public override string ToString() =>
        $"shortcuts=[{string.Join(", ", Shortcuts)}] assoc=[{string.Join(", ", FileAssociations)}] path=[{string.Join(", ", PathEntries)}] " +
        $"autostart=[{string.Join(", ", AutoStarts)}] arp=[{string.Join(", ", UninstallEntries)}] registry=[{string.Join(", ", RegistryValues)}]";
}
