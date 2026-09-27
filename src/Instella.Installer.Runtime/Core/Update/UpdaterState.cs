namespace Instella.Installer.Runtime.Core.Update;

/// <summary>
/// States the updater can be in during the update process.
/// </summary>
internal enum UpdaterState
{
    /// <summary>Loading configuration, validating arguments.</summary>
    Initializing,

    /// <summary>Waiting for the main application to close.</summary>
    WaitingForProcessClose,

    /// <summary>Creating backup of current installation.</summary>
    BackingUp,

    /// <summary>Downloading patch or full update from server.</summary>
    Downloading,

    /// <summary>Verifying download integrity (SHA256).</summary>
    Verifying,

    /// <summary>Applying BSDiff patches to files.</summary>
    ApplyingPatch,

    /// <summary>Extracting new files from archive.</summary>
    Extracting,

    /// <summary>Cleanup, setting permissions, updating manifest.</summary>
    Finalizing,

    /// <summary>Update completed successfully.</summary>
    Completed,

    /// <summary>Update failed with unrecoverable error.</summary>
    Failed,

    /// <summary>User cancelled the update.</summary>
    Cancelled,

    /// <summary>Restoring backup after failure.</summary>
    RollingBack
}
