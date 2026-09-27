using System;
using Instella.Core.Installation;

namespace Instella.Installer.Runtime.Migrations;

/// <summary>The installer modes a migration condition can be true in; used by the Build() check.</summary>
[Flags]
internal enum ModeSet
{
    None = 0,
    FirstInstall = 1,
    Upgrade = 2,
    Repair = 4,
    Uninstall = 8,
    Install = FirstInstall | Upgrade | Repair,
    All = Install | Uninstall,
}

internal static class ModeSets
{
    /// <summary>The flag for <paramref name="mode"/>; <see cref="ModeSet.None"/> for modes migrations never run in.</summary>
    public static ModeSet Of(InstallerMode mode) => mode switch
    {
        InstallerMode.FirstInstall => ModeSet.FirstInstall,
        InstallerMode.Upgrade => ModeSet.Upgrade,
        InstallerMode.Repair => ModeSet.Repair,
        InstallerMode.Uninstall => ModeSet.Uninstall,
        _ => ModeSet.None,
    };

    /// <summary>The modes migrations of <paramref name="timing"/> run in.</summary>
    public static ModeSet For(MigrationTiming timing) =>
        timing == MigrationTiming.Uninstall ? ModeSet.Uninstall : ModeSet.Install;
}
