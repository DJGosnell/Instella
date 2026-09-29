namespace Instella.Core.Installation;

/// <summary>
/// Exit codes the installer binary returns. Range-banded so scripts and
/// enterprise deployments can distinguish failure categories:
/// <list type="bullet">
/// <item><c>0</c> — success</item>
/// <item><c>1</c> — user cancelled</item>
/// <item><c>10–19</c> — install errors</item>
/// <item><c>20–29</c> — update errors</item>
/// <item><c>30–39</c> — uninstall errors</item>
/// <item><c>40–49</c> — usage / CLI errors</item>
/// <item><c>50–59</c> — platform / environment errors</item>
/// </list>
/// Integer values are load-bearing for scripting; never reorder.
/// </summary>
public enum InstellaExitCode
{
    /// <summary>The operation completed.</summary>
    Success = 0,
    /// <summary>The user cancelled; nothing was changed.</summary>
    UserCancelled = 1,

    /// <summary>The install failed and was rolled back.</summary>
    InstallGeneralFailure = 10,
    /// <summary>A prerequisite could not be installed.</summary>
    InstallPrereqFailed = 11,
    /// <summary>The installer failed its own payload integrity check, or was built by a newer Instella.</summary>
    InstallIntegrityFailed = 12,
    /// <summary>The install failed and rollback left something behind (see the log).</summary>
    InstallRollbackCompletedWithWarnings = 13,
    /// <summary>A silent install needed input no CLI flag supplied (for example the licence acceptance).</summary>
    InstallSilentMissingState = 14,
    /// <summary>
    /// The app's upgrade program failed, timed out or could not start, or its declaration could not be
    /// honoured; the install was rolled back.
    /// </summary>
    InstallAppUpgradeFailed = 15,

    /// <summary>The update failed before anything was changed.</summary>
    UpdateGeneralFailure = 20,
    /// <summary>The update server could not be reached.</summary>
    UpdateServerUnreachable = 21,
    /// <summary>The update failed while applying and was rolled back; the previous version is intact.</summary>
    UpdateRolledBack = 22,
    /// <summary>The update failed and so did rollback, or an interrupted update cannot be recovered by this version; run <c>--recover</c>.</summary>
    UpdateRollbackFailed = 23,
    /// <summary>The application would not close, so the update was not applied.</summary>
    UpdateAppCouldNotClose = 24,
    /// <summary>
    /// The app's upgrade program failed, timed out or could not start, or its declaration could not be
    /// honoured; the update was rolled back and the previous version is intact.
    /// </summary>
    UpdateAppUpgradeFailed = 25,

    /// <summary>The uninstall failed.</summary>
    UninstallGeneralFailure = 30,
    /// <summary>No readable installed manifest was found at the install path.</summary>
    UninstallManifestMissing = 31,
    /// <summary>
    /// Some files were in use and could not be removed, or a silent uninstall found programs using
    /// the app's files and, without <c>--force-close</c>, changed nothing.
    /// </summary>
    UninstallFilesLocked = 32,

    /// <summary>The command line was invalid.</summary>
    UsageInvalidArgs = 40,
    /// <summary>The command line named an unknown mode.</summary>
    UsageUnknownMode = 41,

    /// <summary>The operation is not supported on this platform.</summary>
    UnsupportedPlatform = 50,
    /// <summary>The operation needs administrator rights that were not granted.</summary>
    InsufficientPrivileges = 51,
    /// <summary>
    /// Another Instella process (setup, update, uninstall, recovery) is working on the same
    /// installation; wait for it to finish and try again.
    /// </summary>
    InstallationBusy = 52,
}
