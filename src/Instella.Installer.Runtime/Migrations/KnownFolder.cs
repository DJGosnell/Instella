namespace Instella.Installer.Runtime.Migrations;

/// <summary>
/// The roots every path a migration touches starts from. Each resolves for the scope being
/// installed: a per-user folder is unavailable to a machine-wide install, which runs elevated and
/// perhaps as another account, so a condition on it is false and an action on it fails.
/// </summary>
public enum KnownFolder
{
    /// <summary><c>%LOCALAPPDATA%</c>. Per-user installs only.</summary>
    LocalAppData,

    /// <summary><c>%APPDATA%</c> (Roaming). Per-user installs only.</summary>
    RoamingAppData,

    /// <summary>The Start menu's Programs folder: the user's for a per-user install, all users' for a machine-wide one.</summary>
    StartMenuPrograms,

    /// <summary><c>%ProgramFiles%</c>.</summary>
    ProgramFiles,

    /// <summary><c>%ProgramFiles(x86)%</c>.</summary>
    ProgramFilesX86,

    /// <summary><c>%ProgramData%</c>.</summary>
    ProgramData,

    /// <summary>
    /// The folder being installed. For conditions only: every action refuses it, because the
    /// install itself owns what is in it.
    /// </summary>
    InstallFolder,
}
