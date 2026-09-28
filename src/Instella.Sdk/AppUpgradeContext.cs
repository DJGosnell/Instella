using Instella.Core.Installation;

namespace Instella.Sdk;

/// <summary>The operation the app's upgrade program was started for (the contract's <c>--mode</c>).</summary>
public enum AppUpgradeMode
{
    /// <summary>A first install: nothing was installed before (<see cref="AppUpgradeContext.FromVersion"/> is null).</summary>
    FirstInstall,
    /// <summary>An installer replaced an older version.</summary>
    Upgrade,
    /// <summary>The same version was reinstalled, by an installer or the updater (<c>--from</c> equals <c>--to</c>).</summary>
    Repair,
    /// <summary>An installer replaced a newer version (<c>--allow-downgrade</c>): adapt, or refuse with <see cref="AppUpgradeRefusedException"/>.</summary>
    Downgrade,
    /// <summary>The in-app updater applied a newer release.</summary>
    Update,
    /// <summary>The app is being uninstalled (<see cref="AppUpgradeContext.ToVersion"/> is null).</summary>
    Uninstall,
}

/// <summary>Whether the installation is for the current user or for every user of the machine.</summary>
public enum AppUpgradeScope
{
    /// <summary>Installed for one user; the program runs with that user's rights.</summary>
    PerUser,
    /// <summary>Installed for every user; the program runs elevated.</summary>
    Machine,
}

/// <summary>
/// What Instella told the app's upgrade program: the operation, the versions before and after, and
/// where the app is installed. Parsed by <see cref="InstellaUpgrade.TryParse"/>; tests can create one
/// directly and turn it into arguments with <see cref="ToArguments"/>.
/// </summary>
public sealed class AppUpgradeContext
{
    /// <summary>The operation.</summary>
    public required AppUpgradeMode Mode { get; init; }

    /// <summary>The version installed before the operation; null on a first install.</summary>
    public Version? FromVersion { get; init; }

    /// <summary>The version after the operation; null on uninstall.</summary>
    public Version? ToVersion { get; init; }

    /// <summary>Per-user or machine-wide.</summary>
    public required AppUpgradeScope Scope { get; init; }

    /// <summary>The install folder (also the working directory).</summary>
    public required string InstallPath { get; init; }

    /// <summary>The application id.</summary>
    public required string AppId { get; init; }

    /// <summary>The launch contract version the arguments follow.</summary>
    public int ContractVersion { get; init; } = AppUpgradeContract.CurrentVersion;

    /// <summary>The <c>arguments</c> of <c>instella-upgrade.json</c> (<c>InstellaUpgradeArguments</c>), after the contract's own.</summary>
    public IReadOnlyList<string> ExtraArguments { get; init; } = [];

    /// <summary>Whether this is a downgrade: <see cref="FromVersion"/> is newer than <see cref="ToVersion"/>.</summary>
    public bool IsDowngrade => Mode == AppUpgradeMode.Downgrade;

    /// <summary>Whether this is a repair: the same version was reinstalled.</summary>
    public bool IsRepair => Mode == AppUpgradeMode.Repair;

    /// <summary>
    /// The exact command-line arguments Instella passes for this context (launch contract version
    /// <see cref="ContractVersion"/>), for tests: <c>InstellaUpgrade.RunAsync(context.ToArguments().ToArray(), …)</c>.
    /// </summary>
    public IReadOnlyList<string> ToArguments() =>
        AppUpgradeContract.BuildArguments(new AppUpgradeLaunch(
            (AppUpgradeLaunchMode)Mode, FromVersion, ToVersion,
            Scope == AppUpgradeScope.Machine ? InstallationScope.SystemWide : InstallationScope.PerUser,
            InstallPath, AppId, ContractVersion), ExtraArguments);
}

/// <summary>Reports progress to the installer or updater window (<c>##instella progress</c> lines on stdout).</summary>
public interface IAppUpgradeProgress
{
    /// <summary>Moves the progress bar to <paramref name="percent"/> (0-100) and shows <paramref name="text"/> when given.</summary>
    void Report(int percent, string? text = null);
}

/// <summary>
/// Thrown by an upgrade handler to refuse the change (exit code 2), for example a downgrade the app's
/// data cannot follow. The installer or updater rolls back and logs <see cref="Exception.Message"/>.
/// Leave the data usable by the version that was installed before.
/// </summary>
public sealed class AppUpgradeRefusedException : Exception
{
    /// <summary>Refuses with <paramref name="message"/>.</summary>
    public AppUpgradeRefusedException(string message) : base(message) { }

    /// <summary>Refuses with <paramref name="message"/>, caused by <paramref name="innerException"/>.</summary>
    public AppUpgradeRefusedException(string message, Exception? innerException) : base(message, innerException) { }
}
