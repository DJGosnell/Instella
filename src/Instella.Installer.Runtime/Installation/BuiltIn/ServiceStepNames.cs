using System.Collections.Generic;

namespace Instella.Installer.Runtime.Installation.BuiltIn;

/// <summary>
/// The names of the built-in Windows service steps. They are reserved: a user step may not take
/// one, as with <see cref="AppUpgradeStep.StepName"/>.
/// </summary>
internal static class ServiceStepNames
{
    /// <summary>Creates or reconfigures the declared services (Register stage).</summary>
    public const string RegisterServices = "register-services";

    /// <summary>Stops and deletes the services an upgrade no longer declares (Register stage).</summary>
    public const string RemoveDroppedServices = "remove-dropped-services";

    /// <summary>Stops the installation's services right before <c>commit-transaction</c>.</summary>
    public const string StopServices = "stop-services";

    /// <summary>Starts the services right after <c>app-upgrade</c>.</summary>
    public const string StartServices = "start-services";

    /// <summary>Every reserved service step name.</summary>
    public static IReadOnlySet<string> All { get; } = new HashSet<string>
    {
        RegisterServices, RemoveDroppedServices, StopServices, StartServices,
    };
}
