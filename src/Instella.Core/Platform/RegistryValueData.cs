namespace Instella.Core.Platform;

/// <summary>A registry value's kind and data, as read back for restore-on-rollback.</summary>
/// <param name="Kind">The value kind.</param>
/// <param name="Value">The data, typed as the registry returns it for <paramref name="Kind"/>.</param>
public sealed record RegistryValueData(InstellaRegistryValueKind Kind, object Value);

/// <summary>
/// The outcome of <see cref="IPlatformServices.TryReadRegistryValueAsync"/>: the value (null when it
/// does not exist), or why it could not be read.
/// </summary>
/// <param name="Value">The value, or null when it (or its key) does not exist or could not be read.</param>
/// <param name="Error">Why the value could not be read (for example access denied); null when it was read.</param>
public sealed record RegistryReadResult(RegistryValueData? Value, string? Error)
{
    /// <summary>Whether the value could not be read, so its existence is unknown.</summary>
    public bool Failed => Error is not null;
}
