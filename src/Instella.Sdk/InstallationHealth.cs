namespace Instella.Sdk;

/// <summary>Result of <see cref="InstellaClient.GetInstallationHealth"/>.</summary>
public enum InstallationHealth
{
    /// <summary>No update was interrupted; the installed files are consistent.</summary>
    Healthy,

    /// <summary>
    /// An update was interrupted while swapping files. Call
    /// <see cref="InstellaClient.StartRecoveryAsync"/> and exit.
    /// </summary>
    InterruptedUpdate,
}
