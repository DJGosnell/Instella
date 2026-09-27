namespace Instella.Core.Installation;

/// <summary>
/// The operation the installer binary is currently performing. Determined at
/// startup from CLI args (and presence of a sibling installed manifest).
/// </summary>
public enum InstallerMode
{
    /// <summary>Nothing is installed at the target path yet.</summary>
    FirstInstall,
    /// <summary>An older version is installed; it is replaced.</summary>
    Upgrade,
    /// <summary>The same version is installed; its files are restored.</summary>
    Repair,
    /// <summary>The updater is applying a server release to an installation (<c>--update</c>).</summary>
    Update,
    /// <summary>The installation is being removed (<c>--uninstall</c>).</summary>
    Uninstall,
    /// <summary>The installed copy was launched without arguments: show the manage window.</summary>
    Manage,
    /// <summary>A temporary copy finishes deleting the install directory after uninstall (<c>--cleanup</c>).</summary>
    Cleanup,

    /// <summary>Finish or undo an interrupted install transaction (<c>--recover</c>), then exit.</summary>
    Recover,
}
