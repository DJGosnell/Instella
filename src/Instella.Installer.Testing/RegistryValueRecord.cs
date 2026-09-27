using Instella.Core.Platform;

namespace Instella.Installer.Testing;

/// <summary>
/// Resolved snapshot entry from <see cref="IFakeRegistry.Snapshot"/>. Mirrors
/// the shape of the runtime's internal <c>RegistryWriteSpec</c> but with the
/// value already materialized (no builder-time <c>Func&lt;InstallContext, object&gt;</c>)
/// so tests assert against concrete data.
/// </summary>
/// <param name="Hive">Hive that the value lives under.</param>
/// <param name="KeyPath">Key path within the hive (e.g. <c>Software\Example</c>).</param>
/// <param name="ValueName">Named value under the key. Empty string for the default value.</param>
/// <param name="Kind">Registry value kind, matching what a step passed to
/// <c>IPlatformServices.WriteRegistryValueAsync</c>.</param>
/// <param name="Value">The stored value, in whatever CLR type the producing
/// step handed in (typically <see cref="string"/>, <see cref="int"/>,
/// <see cref="long"/>, <see cref="byte"/>[], or <see cref="string"/>[]).</param>
public readonly record struct RegistryValueRecord(
    RegistryHive Hive,
    string KeyPath,
    string ValueName,
    InstellaRegistryValueKind Kind,
    object Value);
