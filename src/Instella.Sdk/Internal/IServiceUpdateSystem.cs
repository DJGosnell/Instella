namespace Instella.Sdk.Internal;

/// <summary>Whether a process started now could outlive the job object this process runs in.</summary>
internal enum JobBreakaway
{
    /// <summary>This process is in no job: a started process is not tied to it.</summary>
    NotInJob,

    /// <summary>The job allows breakaway (<c>JOB_OBJECT_LIMIT_BREAKAWAY_OK</c> or <c>SILENT_BREAKAWAY_OK</c>).</summary>
    Allowed,

    /// <summary>The job forbids breakaway: a started process dies with the job when the service stops.</summary>
    Forbidden,
}

/// <summary>
/// Everything <see cref="ServiceUpdateLauncher"/> asks the operating system, so the checks
/// before a service self-update can be tested without a service. The real implementation is
/// <see cref="WindowsServiceUpdateSystem"/>.
/// </summary>
internal interface IServiceUpdateSystem
{
    /// <summary>Null when this OS has Windows services, else why a service update cannot run here.</summary>
    string? UnsupportedReason => null;

    /// <summary>This process's id.</summary>
    int CurrentProcessId { get; }

    /// <summary>Whether this process runs elevated (LocalSystem, or an administrator account).</summary>
    bool IsElevated { get; }

    /// <summary>Null when this process can create and delete files in <paramref name="directory"/>, else the error.</summary>
    string? CheckCanWrite(string directory);

    /// <summary>Null when this process may stop and start <paramref name="serviceName"/>, else the error.</summary>
    Task<string?> CheckCanStopAndStartAsync(string serviceName, CancellationToken ct);

    /// <summary>The process id of the running service, or null when it is not running or cannot be read.</summary>
    Task<int?> GetServiceProcessIdAsync(string serviceName, CancellationToken ct);

    /// <summary>The job object this process runs in, if any, and whether it allows breakaway.</summary>
    JobBreakaway GetJobBreakaway();

    /// <summary>
    /// Starts <paramref name="executable"/> without a window and without tying it to this process,
    /// so it outlives the service; returns its process id.
    /// </summary>
    int StartDetached(string executable, IReadOnlyList<string> arguments, string workingDirectory, bool breakawayFromJob);
}
