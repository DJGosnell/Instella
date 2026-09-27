using System;
using System.Collections.Generic;
using Instella.Core.Platform;
using Instella.Installer.Runtime.Installation;
using RegistryHive = Instella.Core.Platform.RegistryHive;

namespace Instella.Installer.Runtime.Builders;

/// <summary>
/// Fluent builder for one Windows registry key's values. Each setter records a typed write;
/// the framework applies them at install time and tracks each one so uninstall can reverse it.
/// </summary>
/// <remarks>
/// Every setter has two overloads: one that takes a <c>Func&lt;InstallContext, T&gt;</c> for
/// values that depend on install-time state (such as the resolved install path), and one for
/// constants.
/// </remarks>
public sealed class RegistryKeyBuilder
{
    private readonly RegistryHive _hive;
    private readonly string _keyPath;
    private readonly List<RegistryWriteSpec> _writes = new();

    internal RegistryKeyBuilder(RegistryHive hive, string keyPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(keyPath);
        _hive = hive;
        _keyPath = keyPath;
    }

    internal IReadOnlyList<RegistryWriteSpec> Writes => _writes;

    /// <summary>Writes a <c>REG_SZ</c> string named <paramref name="name"/>, computed at install time.</summary>
    public RegistryKeyBuilder SetString(string name, Func<InstallContext, string> value) => Add(name, InstellaRegistryValueKind.String, ctx => value(ctx));
    /// <summary>Writes a <c>REG_EXPAND_SZ</c> string (environment variables expand on read) named <paramref name="name"/>, computed at install time.</summary>
    public RegistryKeyBuilder SetExpandString(string name, Func<InstallContext, string> value) => Add(name, InstellaRegistryValueKind.ExpandString, ctx => value(ctx));
    /// <summary>Writes a <c>REG_DWORD</c> 32-bit value named <paramref name="name"/>, computed at install time.</summary>
    public RegistryKeyBuilder SetDWord(string name, Func<InstallContext, int> value) => Add(name, InstellaRegistryValueKind.DWord, ctx => value(ctx));
    /// <summary>Writes a <c>REG_QWORD</c> 64-bit value named <paramref name="name"/>, computed at install time.</summary>
    public RegistryKeyBuilder SetQWord(string name, Func<InstallContext, long> value) => Add(name, InstellaRegistryValueKind.QWord, ctx => value(ctx));
    /// <summary>Writes a <c>REG_MULTI_SZ</c> string list named <paramref name="name"/>, computed at install time.</summary>
    public RegistryKeyBuilder SetMultiString(string name, Func<InstallContext, string[]> value) => Add(name, InstellaRegistryValueKind.MultiString, ctx => value(ctx));
    /// <summary>Writes a <c>REG_BINARY</c> value named <paramref name="name"/>, computed at install time.</summary>
    public RegistryKeyBuilder SetBinary(string name, Func<InstallContext, byte[]> value) => Add(name, InstellaRegistryValueKind.Binary, ctx => value(ctx));

    /// <summary>Writes a <c>REG_SZ</c> string named <paramref name="name"/>.</summary>
    public RegistryKeyBuilder SetString(string name, string value) => Add(name, InstellaRegistryValueKind.String, _ => value);
    /// <summary>Writes a <c>REG_EXPAND_SZ</c> string (environment variables expand on read) named <paramref name="name"/>.</summary>
    public RegistryKeyBuilder SetExpandString(string name, string value) => Add(name, InstellaRegistryValueKind.ExpandString, _ => value);
    /// <summary>Writes a <c>REG_DWORD</c> 32-bit value named <paramref name="name"/>.</summary>
    public RegistryKeyBuilder SetDWord(string name, int value) => Add(name, InstellaRegistryValueKind.DWord, _ => value);
    /// <summary>Writes a <c>REG_QWORD</c> 64-bit value named <paramref name="name"/>.</summary>
    public RegistryKeyBuilder SetQWord(string name, long value) => Add(name, InstellaRegistryValueKind.QWord, _ => value);
    /// <summary>Writes a <c>REG_MULTI_SZ</c> string list named <paramref name="name"/>.</summary>
    public RegistryKeyBuilder SetMultiString(string name, string[] value) => Add(name, InstellaRegistryValueKind.MultiString, _ => value);
    /// <summary>Writes a <c>REG_BINARY</c> value named <paramref name="name"/>.</summary>
    public RegistryKeyBuilder SetBinary(string name, byte[] value) => Add(name, InstellaRegistryValueKind.Binary, _ => value);

    private RegistryKeyBuilder Add(string name, InstellaRegistryValueKind kind, Func<InstallContext, object> factory)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(factory);
        _writes.Add(new RegistryWriteSpec(_hive, _keyPath, name, kind, factory));
        return this;
    }
}
