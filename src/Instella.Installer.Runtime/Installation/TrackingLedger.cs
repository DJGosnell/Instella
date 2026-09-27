using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.FileSystem;
using Instella.Core.Logging;
using Instella.Core.Platform;
using Instella.Core.Installation;

namespace Instella.Installer.Runtime.Installation;

/// <summary>
/// Records mutations made by install steps so they can be reversed in
/// reverse-registration order on install failure. It covers files,
/// directories, registry values, registry keys, and PATH entries. The
/// ledger stores entries; <see cref="TrackedRollbackStrategy"/> contains the
/// per-kind undo logic.
/// </summary>
internal sealed class TrackingLedger
{
    private readonly List<LedgerEntry> _entries = new();

    internal IReadOnlyList<LedgerEntry> Entries => _entries;

    /// <summary>
    /// Set by <see cref="StepExecutor"/> while a custom (builder-authored) step runs; entries
    /// recorded meanwhile are persisted in the installed manifest for uninstall.
    /// </summary>
    internal bool RecordingUserStep { get; set; }

    /// <summary>Total number of entries in the ledger. Used by <see cref="StepExecutor"/>'s point-of-no-return snapshot.</summary>
    public int Count => _entries.Count;

    public void TrackFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        _entries.Add(LedgerEntry.File(path) with { FromUserStep = RecordingUserStep });
    }

    public void TrackDirectory(string path, bool recursive = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        _entries.Add(LedgerEntry.Directory(path, recursive) with { FromUserStep = RecordingUserStep });
    }

    public void TrackRegistryValue(RegistryHive hive, string keyPath, string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(keyPath);
        ArgumentException.ThrowIfNullOrEmpty(name);
        _entries.Add(LedgerEntry.RegistryValue(hive, keyPath, name) with { FromUserStep = RecordingUserStep });
    }

    public void TrackRegistryKey(RegistryHive hive, string keyPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(keyPath);
        _entries.Add(LedgerEntry.RegistryKey(hive, keyPath) with { FromUserStep = RecordingUserStep });
    }

    /// <summary>
    /// Record that a registry value which already existed is about to be overwritten, so
    /// rollback writes <paramref name="previous"/> back instead of deleting the value.
    /// </summary>
    public void TrackRegistryValueRestore(RegistryHive hive, string keyPath, string name, RegistryValueData previous)
    {
        ArgumentException.ThrowIfNullOrEmpty(keyPath);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(previous);
        _entries.Add(LedgerEntry.RestoreRegistryValue(hive, keyPath, name, previous));
    }

    public void TrackPathEntry(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        _entries.Add(LedgerEntry.PathEntry(directory) with { FromUserStep = RecordingUserStep });
    }

    /// <summary>
    /// Apply reverse operations to entries in <c>[fromIndex, Count)</c> in LIFO
    /// order. Each failure collects into <paramref name="warnings"/>; unwind
    /// never throws. Designed to be called by <see cref="StepExecutor"/>
    /// during failure handling, not directly. Pass <c>0</c> for a full unwind;
    /// pass a snapshot index to skip entries committed before a point-of-no-return.
    /// </summary>
    internal Task UnwindAsync(
        InstallContext context,
        List<string> warnings,
        int fromIndex,
        CancellationToken cancellationToken)
        => TrackedRollbackStrategy.UnwindAsync(this, context, warnings, fromIndex, cancellationToken);
}

internal static class LedgerExtensions
{
    /// <summary>
    /// The entries custom steps recorded, as installed-manifest items for uninstall.
    /// A restored (pre-existing) registry value is not included: uninstall must not delete it.
    /// </summary>
    public static List<ManifestTrackedItem> UserTrackedItems(this TrackingLedger ledger)
    {
        var items = new List<ManifestTrackedItem>();
        foreach (var e in ledger.Entries)
        {
            if (!e.FromUserStep) continue;
            string? kind = e.Kind switch
            {
                LedgerKind.File => "file",
                LedgerKind.Directory => "directory",
                LedgerKind.RegistryValue => "registry-value",
                LedgerKind.RegistryKey => "registry-key",
                LedgerKind.PathEntry => "path-entry",
                _ => null,
            };
            if (kind is not null)
                items.Add(new ManifestTrackedItem(kind, e.Path, e.Recursive, e.Hive, e.ValueName));
        }
        return items;
    }
}

internal enum LedgerKind
{
    File,
    Directory,
    RegistryValue,
    RegistryKey,
    PathEntry,
    RestoreRegistryValue,
}

internal readonly record struct LedgerEntry(
    LedgerKind Kind,
    string Path,
    bool Recursive,
    RegistryHive Hive,
    string? ValueName,
    RegistryValueData? Previous = null,
    bool FromUserStep = false)
{
    public static LedgerEntry File(string path)
        => new(LedgerKind.File, path, Recursive: false, RegistryHive.AutoFromScope, ValueName: null);

    public static LedgerEntry Directory(string path, bool recursive)
        => new(LedgerKind.Directory, path, recursive, RegistryHive.AutoFromScope, ValueName: null);

    public static LedgerEntry RegistryValue(RegistryHive hive, string keyPath, string name)
        => new(LedgerKind.RegistryValue, keyPath, Recursive: false, hive, name);

    public static LedgerEntry RegistryKey(RegistryHive hive, string keyPath)
        => new(LedgerKind.RegistryKey, keyPath, Recursive: false, hive, ValueName: null);

    public static LedgerEntry RestoreRegistryValue(RegistryHive hive, string keyPath, string name, RegistryValueData previous)
        => new(LedgerKind.RestoreRegistryValue, keyPath, Recursive: false, hive, name, previous);

    public static LedgerEntry PathEntry(string directory)
        => new(LedgerKind.PathEntry, directory, Recursive: false, RegistryHive.AutoFromScope, ValueName: null);
}
