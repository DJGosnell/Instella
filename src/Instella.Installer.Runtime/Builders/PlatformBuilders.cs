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
    private readonly List<ServiceSpec> _services = new();

    internal WindowsBuilder()
    {
    }

    internal IReadOnlyList<RegistryWriteSpec> Writes => _writes;

    internal IReadOnlyList<ServiceSpec> Services => _services;

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

    /// <summary>
    /// Declare a Windows service that install, upgrade and repair register (or reconfigure), stop
    /// before the files are replaced and start again afterwards; uninstall removes it. May be called
    /// more than once. Needs <c>WithElevation(ElevationMode.SystemWide)</c>; the name (1–80 letters,
    /// digits, '.', '_' or '-') and the rest are checked by <see cref="InstallerBuilder.Build"/>.
    /// </summary>
    public WindowsBuilder AddService(string name, Action<ServiceBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(configure);
        var sb = new ServiceBuilder(name);
        configure(sb);
        _services.Add(sb.Build());
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
