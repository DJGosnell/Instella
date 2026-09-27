using System;
using System.Collections.Generic;
using Instella.Core.Platform;

namespace Instella.Installer.Runtime.Builders;

/// <summary>
/// Windows-only configuration, obtained through <see cref="InstallerBuilder.OnWindows"/>.
/// Its configure delegate runs only when the installer runs on Windows.
/// </summary>
public sealed class WindowsBuilder
{
    private readonly List<RegistryWriteSpec> _writes = new();

    internal WindowsBuilder()
    {
    }

    internal IReadOnlyList<RegistryWriteSpec> Writes => _writes;

    /// <summary>
    /// Declare a registry key whose values are written at install time and removed at
    /// uninstall time (every write is tracked).
    /// </summary>
    public WindowsBuilder AddRegistryKey(RegistryHive hive, string keyPath, Action<RegistryKeyBuilder> configure)
    {
        ArgumentException.ThrowIfNullOrEmpty(keyPath);
        ArgumentNullException.ThrowIfNull(configure);
        var kb = new RegistryKeyBuilder(hive, keyPath);
        configure(kb);
        _writes.AddRange(kb.Writes);
        return this;
    }
}

/// <summary>
/// Linux-only configuration, obtained through <see cref="InstallerBuilder.OnLinux"/>. It has
/// no members yet; Linux extension points are added here without breaking callers.
/// </summary>
public sealed class LinuxBuilder
{
    internal LinuxBuilder()
    {
    }
}

/// <summary>
/// macOS-only configuration, obtained through <see cref="InstallerBuilder.OnMacOS"/>. It has
/// no members yet; macOS extension points are added here without breaking callers.
/// </summary>
public sealed class MacOSBuilder
{
    internal MacOSBuilder()
    {
    }
}
