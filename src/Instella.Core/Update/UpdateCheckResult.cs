namespace Instella.Core.Update;

/// <summary>Outcome of an update check.</summary>
public enum UpdateCheckStatus
{
    /// <summary>The installed version is the newest on the channel.</summary>
    UpToDate,

    /// <summary>A newer, verified release is available.</summary>
    UpdateAvailable,

    /// <summary>The app was not installed by Instella (for example started from the IDE).</summary>
    NotInstalled,

    /// <summary>The check failed (network, server error, or a release that did not verify).</summary>
    Failed,
}

/// <summary>
/// Result of checking for updates.
/// </summary>
/// <param name="Status">What the check found.</param>
/// <param name="Update">Information about the update when <see cref="UpdateCheckStatus.UpdateAvailable"/>.</param>
/// <param name="Error">Why the check failed or could not run.</param>
public sealed record UpdateCheckResult(UpdateCheckStatus Status, UpdateInfo? Update, string? Error)
{
    /// <summary>Whether an update is available.</summary>
    public bool UpdateAvailable => Status == UpdateCheckStatus.UpdateAvailable;

    /// <summary>Whether the check completed (regardless of update availability).</summary>
    public bool Success => Status is UpdateCheckStatus.UpToDate or UpdateCheckStatus.UpdateAvailable;

    /// <summary>A verified update is available.</summary>
    public static UpdateCheckResult Available(UpdateInfo update) => new(UpdateCheckStatus.UpdateAvailable, update, null);

    /// <summary>The installation is up to date.</summary>
    public static UpdateCheckResult NoUpdate() => new(UpdateCheckStatus.UpToDate, null, null);

    /// <summary>The check failed.</summary>
    public static UpdateCheckResult Failed(string error) => new(UpdateCheckStatus.Failed, null, error);

    /// <summary>The app is not an Instella installation.</summary>
    public static UpdateCheckResult NotInstalled(string reason) => new(UpdateCheckStatus.NotInstalled, null, reason);
}
