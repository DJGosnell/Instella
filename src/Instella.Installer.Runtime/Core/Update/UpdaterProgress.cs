namespace Instella.Installer.Runtime.Core.Update;

/// <summary>
/// Progress information during the update process.
/// </summary>
internal sealed record UpdaterProgress
{
    /// <summary>Current state of the updater.</summary>
    public required UpdaterState State { get; init; }

    /// <summary>Overall progress percentage (0-100).</summary>
    public double Percentage { get; init; }

    /// <summary>Human-readable status message.</summary>
    public string? StatusText { get; init; }

    /// <summary>Bytes downloaded so far (for downloading state).</summary>
    public long? BytesDownloaded { get; init; }

    /// <summary>Total bytes to download (for downloading state).</summary>
    public long? TotalBytes { get; init; }

    /// <summary>Current file being processed.</summary>
    public string? CurrentFile { get; init; }

    /// <summary>Files processed so far (for patch/extract states).</summary>
    public int? FilesProcessed { get; init; }

    /// <summary>Total files to process (for patch/extract states).</summary>
    public int? TotalFiles { get; init; }

    /// <summary>
    /// Creates a progress instance for a state with just a status message.
    /// </summary>
    public static UpdaterProgress ForState(UpdaterState state, string? statusText = null)
        => new() { State = state, StatusText = statusText };
}

/// <summary>
/// Information about a detected running process.
/// </summary>
internal sealed record ProcessInfo
{
    /// <summary>Process ID.</summary>
    public required int ProcessId { get; init; }

    /// <summary>Process name.</summary>
    public required string ProcessName { get; init; }

    /// <summary>Main window title, if available.</summary>
    public string? WindowTitle { get; init; }
}

/// <summary>Why an update failed; decides its exit code.</summary>
internal enum UpdateFailure
{
    /// <summary>Failed before commit; nothing live was touched (exit 20).</summary>
    BeforeCommit,

    /// <summary>The server could not be reached (exit 21).</summary>
    Unreachable,

    /// <summary>Commit failed and was rolled back; the previous version is intact (exit 22).</summary>
    RolledBack,

    /// <summary>Commit failed and so did rollback; the journal is left for <c>--recover</c> (exit 23).</summary>
    RollbackFailed,

    /// <summary>The application would not close (exit 24).</summary>
    AppWouldNotClose,
}

/// <summary>
/// Result of an update attempt.
/// </summary>
internal sealed record UpdateResult
{
    /// <summary>Whether the update completed successfully.</summary>
    public required bool Success { get; init; }

    /// <summary>Error message if failed.</summary>
    public string? Error { get; init; }

    /// <summary>Why it failed, when it did and was not cancelled.</summary>
    public UpdateFailure? Failure { get; init; }

    /// <summary>Whether the update was cancelled before commit.</summary>
    public bool WasCancelled { get; init; }

    /// <summary>The app's executable, after a successful update: the caller restarts it.</summary>
    public string? ExecutablePath { get; init; }

    /// <summary>Whether a rollback was performed.</summary>
    public bool RolledBack => Failure == UpdateFailure.RolledBack;

    /// <summary>The installer exit code for this outcome.</summary>
    public Instella.Core.Installation.InstellaExitCode ExitCode => this switch
    {
        { Success: true } => Instella.Core.Installation.InstellaExitCode.Success,
        { WasCancelled: true } => Instella.Core.Installation.InstellaExitCode.UserCancelled,
        { Failure: UpdateFailure.Unreachable } => Instella.Core.Installation.InstellaExitCode.UpdateServerUnreachable,
        { Failure: UpdateFailure.RolledBack } => Instella.Core.Installation.InstellaExitCode.UpdateRolledBack,
        { Failure: UpdateFailure.RollbackFailed } => Instella.Core.Installation.InstellaExitCode.UpdateRollbackFailed,
        { Failure: UpdateFailure.AppWouldNotClose } => Instella.Core.Installation.InstellaExitCode.UpdateAppCouldNotClose,
        _ => Instella.Core.Installation.InstellaExitCode.UpdateGeneralFailure,
    };

    /// <summary>Creates a successful result.</summary>
    public static UpdateResult Succeeded() => new() { Success = true };

    /// <summary>Creates a failed result with the given error.</summary>
    public static UpdateResult Failed(string error, UpdateFailure failure = UpdateFailure.BeforeCommit)
        => new() { Success = false, Error = error, Failure = failure };

    /// <summary>Creates a cancelled result.</summary>
    public static UpdateResult Cancelled() => new() { Success = false, Error = "Update was cancelled", WasCancelled = true };
}

/// <summary>
/// Progress for patch application.
/// </summary>
internal readonly record struct PatchProgress(int FilesProcessed, int TotalFiles, string CurrentFile);
