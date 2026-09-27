namespace Instella.Installer.Runtime.Migrations;

/// <summary>When an <see cref="InstallMigration"/> runs.</summary>
public enum MigrationTiming
{
    /// <summary>
    /// During an install, upgrade or repair, before the new files are moved into place. A failure
    /// fails the install and rolls it back (<see cref="InstallMigration"/>'s <c>RollbackAsync</c> is called).
    /// </summary>
    BeforeCommit,

    /// <summary>
    /// The default: at the very end of an install, upgrade or repair, once everything else has
    /// succeeded. Best effort: a failure is logged as a warning and the install is kept.
    /// </summary>
    AfterCommit,

    /// <summary>During an uninstall, before Instella removes anything. A failure is logged and the uninstall continues.</summary>
    Uninstall,
}
