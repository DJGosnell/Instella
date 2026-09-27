namespace Instella.Core.Platform;

/// <summary>A registry value's kind and data, as read back for restore-on-rollback.</summary>
/// <param name="Kind">The value kind.</param>
/// <param name="Value">The data, typed as the registry returns it for <paramref name="Kind"/>.</param>
public sealed record RegistryValueData(InstellaRegistryValueKind Kind, object Value);
