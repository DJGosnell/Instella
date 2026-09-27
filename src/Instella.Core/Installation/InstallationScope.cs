namespace Instella.Core.Installation;

/// <summary>
/// Whether an install targets the current user only (HKCU, per-user
/// shortcuts, no admin needed) or the whole machine (HKLM, admin required).
/// Resolved at install time from <see cref="Instella.Core.Manifest.ElevationMode"/>
/// plus the chosen install path.
/// </summary>
public enum InstallationScope
{
    /// <summary>Installed for the current user only; no administrator rights needed.</summary>
    PerUser,
    /// <summary>Installed for every user of the machine; requires administrator rights.</summary>
    SystemWide,
}
