namespace Instella.Core.Platform;

/// <summary>
/// Platform-neutral mirror of <c>Microsoft.Win32.RegistryValueKind</c>. Lives
/// in <c>Instella.Core</c> so the fluent builder (<c>Runtime</c>) and manifest
/// (<c>Core</c>) can reference it without pulling a Windows-only type into
/// cross-platform call sites. Windows-side <see cref="IPlatformServices.WriteRegistryValueAsync"/>
/// maps these values to the real registry kinds.
/// </summary>
public enum InstellaRegistryValueKind
{
    /// <summary>A string (<c>REG_SZ</c>).</summary>
    String = 1,
    /// <summary>A string whose environment variables expand when read (<c>REG_EXPAND_SZ</c>).</summary>
    ExpandString = 2,
    /// <summary>Raw bytes (<c>REG_BINARY</c>).</summary>
    Binary = 3,
    /// <summary>A 32-bit number (<c>REG_DWORD</c>).</summary>
    DWord = 4,
    /// <summary>A list of strings (<c>REG_MULTI_SZ</c>).</summary>
    MultiString = 7,
    /// <summary>A 64-bit number (<c>REG_QWORD</c>).</summary>
    QWord = 11,
}
