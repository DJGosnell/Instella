using System;
using Instella.Core.FileSystem;
using Instella.Core.Installation;
using Instella.Core.Logging;
using Instella.Core.Platform;
using Instella.Installer.Runtime.Installation;

namespace Instella.Installer.Runtime.Migrations;

/// <summary>
/// Read-only facts about the run a migration is part of, for custom conditions
/// (<c>Condition.From</c>) and migration bodies.
/// </summary>
public sealed class MigrationContext
{
    internal MigrationContext(InstallContext install, MigrationRuntime runtime, IInstellaLogger log)
    {
        Install = install;
        Runtime = runtime;
        Log = log;
    }

    /// <summary>The application id.</summary>
    public string AppId => Install.AppId;

    /// <summary>The application name.</summary>
    public string AppName => Install.AppName;

    /// <summary>The version this installer installs (for an uninstall: the installer's own version).</summary>
    public Version AppVersion => Install.AppVersion;

    /// <summary><see cref="InstallerMode.FirstInstall"/>, <see cref="InstallerMode.Upgrade"/>, <see cref="InstallerMode.Repair"/> or <see cref="InstallerMode.Uninstall"/>.</summary>
    public InstallerMode Mode => Install.Mode;

    /// <summary>
    /// The version installed before this run: the one being upgraded, repaired or uninstalled. Null
    /// for a first install. After in-app updates it is the updated version, not the one first installed.
    /// </summary>
    public Version? PreviousVersion => Install.ExistingInstallation?.Version;

    /// <summary>Per-user or machine-wide.</summary>
    public InstallationScope Scope => Install.Scope;

    /// <summary>The folder being installed (or uninstalled).</summary>
    public string InstallPath => Install.InstallPath;

    /// <summary>True in preview: actions report what they would do and change nothing.</summary>
    public bool IsPreview => Runtime.IsPreview;

    /// <summary>The operating system.</summary>
    public TargetPlatform Platform => Install.Platform.Platform;

    /// <summary>
    /// The file system. Custom code using it directly bypasses every migration safety rule: prefer
    /// the actions, which refuse protected folders, the install folder and other installations.
    /// </summary>
    public IFileSystem FileSystem => Install.FileSystem;

    /// <summary>
    /// The platform services (registry, shortcuts, processes). Custom code using them directly
    /// bypasses every migration safety rule.
    /// </summary>
    public IPlatformServices PlatformServices => Install.Platform;

    /// <summary>The path of <paramref name="folder"/>, or null when it is unavailable in this scope or on this platform.</summary>
    public string? GetFolderPath(KnownFolder folder) => Runtime.Folders.Resolve(folder, Scope, InstallPath);

    internal InstallContext Install { get; }

    internal MigrationRuntime Runtime { get; }

    /// <summary>The <c>migration[&lt;id&gt;]</c> logger.</summary>
    internal IInstellaLogger Log { get; }

    /// <summary>Whether this install writes the current user's registry (HKCU) rather than the machine's.</summary>
    internal bool PerUser => Scope == InstallationScope.PerUser;
}
