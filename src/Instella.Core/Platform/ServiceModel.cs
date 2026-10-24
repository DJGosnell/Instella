namespace Instella.Core.Platform;

/// <summary>Why an <see cref="IServiceManager"/> call failed, so callers can tell the cases apart.</summary>
internal enum ServiceFailure
{
    /// <summary>The call succeeded.</summary>
    None,

    /// <summary>No service has that name (<c>ERROR_SERVICE_DOES_NOT_EXIST</c>).</summary>
    NotFound,

    /// <summary>The caller lacks the rights (<c>ERROR_ACCESS_DENIED</c>); usually not elevated.</summary>
    AccessDenied,

    /// <summary>The service is deleted but still running or open (<c>ERROR_SERVICE_MARKED_FOR_DELETE</c>).</summary>
    MarkedForDeletion,

    /// <summary>A service with that name already exists (<c>ERROR_SERVICE_EXISTS</c>).</summary>
    AlreadyExists,

    /// <summary>The service did not reach the wanted state in time.</summary>
    Timeout,

    /// <summary>The platform has no service manager this seam can drive.</summary>
    NotSupported,

    /// <summary>Any other failure; the message and <c>NativeError</c> say which.</summary>
    Failed,
}

/// <summary>The outcome of an <see cref="IServiceManager"/> call without a value.</summary>
/// <param name="Outcome">Success, or failure with a message naming the operation and the service.</param>
/// <param name="Failure">The kind of failure; <see cref="ServiceFailure.None"/> on success.</param>
/// <param name="NativeError">The Win32 error code, or 0.</param>
internal sealed record ServiceResult(PlatformResult Outcome, ServiceFailure Failure = ServiceFailure.None, int NativeError = 0)
{
    /// <summary>A successful result.</summary>
    public static ServiceResult Ok { get; } = new(PlatformResult.Ok);

    /// <summary>A failed result.</summary>
    public static ServiceResult Fail(ServiceFailure failure, string message, int nativeError = 0) =>
        new(PlatformResult.Fail(message), failure, nativeError);
}

/// <summary>The outcome of an <see cref="IServiceManager"/> query.</summary>
/// <param name="Value">What was read; null on failure.</param>
/// <param name="Outcome">Success, or failure with a message naming the operation and the service.</param>
/// <param name="Failure">The kind of failure; <see cref="ServiceFailure.None"/> on success.</param>
/// <param name="NativeError">The Win32 error code, or 0.</param>
internal sealed record ServiceResult<T>(T? Value, PlatformResult Outcome, ServiceFailure Failure = ServiceFailure.None, int NativeError = 0)
{
    /// <summary>A successful result carrying <paramref name="value"/>.</summary>
    public static ServiceResult<T> Ok(T value) => new(value, PlatformResult.Ok);

    /// <summary>A failed result.</summary>
    public static ServiceResult<T> Fail(ServiceFailure failure, string message, int nativeError = 0) =>
        new(default, PlatformResult.Fail(message), failure, nativeError);
}

/// <summary>When a service starts.</summary>
internal enum ServiceStartType
{
    /// <summary>At boot (<c>SERVICE_AUTO_START</c>).</summary>
    Automatic,

    /// <summary>Shortly after boot (<c>SERVICE_AUTO_START</c> with delayed auto-start).</summary>
    AutomaticDelayed,

    /// <summary>On demand (<c>SERVICE_DEMAND_START</c>).</summary>
    Manual,

    /// <summary>Never (<c>SERVICE_DISABLED</c>).</summary>
    Disabled,
}

/// <summary>A service's current state; the values are Win32's <c>SERVICE_*</c> states.</summary>
internal enum ServiceState
{
    /// <summary>Not running.</summary>
    Stopped = 1,
    /// <summary>Starting.</summary>
    StartPending = 2,
    /// <summary>Stopping.</summary>
    StopPending = 3,
    /// <summary>Running.</summary>
    Running = 4,
    /// <summary>Resuming from pause.</summary>
    ContinuePending = 5,
    /// <summary>Pausing.</summary>
    PausePending = 6,
    /// <summary>Paused.</summary>
    Paused = 7,
}

/// <summary>A service's state and the id of its process (0 when it has none).</summary>
internal sealed record ServiceStatus(ServiceState State, int ProcessId);

/// <summary>The service SID type (<c>SERVICE_SID_TYPE_*</c>).</summary>
internal enum ServiceSidType
{
    /// <summary>No service SID.</summary>
    None = 0,
    /// <summary>The service SID is added to the process token.</summary>
    Unrestricted = 1,
    /// <summary>A write-restricted token with the service SID.</summary>
    Restricted = 3,
}

/// <summary>What the SCM does when the service fails (<c>SC_ACTION_*</c>).</summary>
internal enum ServiceFailureActionType
{
    /// <summary>Nothing.</summary>
    None = 0,
    /// <summary>Restart the service.</summary>
    Restart = 1,
    /// <summary>Reboot the machine.</summary>
    Reboot = 2,
    /// <summary>Run <see cref="ServiceFailureActions.Command"/>.</summary>
    RunCommand = 3,
}

/// <summary>One failure action and how long the SCM waits before taking it.</summary>
internal sealed record ServiceFailureAction(ServiceFailureActionType Type, TimeSpan Delay);

/// <summary>The service's recovery settings.</summary>
/// <param name="ResetPeriod">After how long without a failure the failure count is reset.</param>
/// <param name="Actions">The action for the first, second, … failure; the last repeats.</param>
/// <param name="Command">The command a <see cref="ServiceFailureActionType.RunCommand"/> action runs.</param>
internal sealed record ServiceFailureActions(TimeSpan ResetPeriod, IReadOnlyList<ServiceFailureAction> Actions, string? Command = null);

/// <summary>
/// What <see cref="IServiceManager.CreateAsync"/> and <see cref="IServiceManager.ChangeConfigurationAsync"/>
/// write. Passwords are never part of it: they travel as a separate <c>char[]</c>.
/// </summary>
internal sealed record ServiceConfiguration
{
    /// <summary>The service (key) name.</summary>
    public required string Name { get; init; }

    /// <summary>The name shown in the Services console.</summary>
    public required string DisplayName { get; init; }

    /// <summary>The description; null writes nothing on create and leaves it unchanged on change.</summary>
    public string? Description { get; init; }

    /// <summary>The full path of the service executable.</summary>
    public required string ExecutablePath { get; init; }

    /// <summary>Arguments passed after the executable.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>When the service starts.</summary>
    public ServiceStartType StartType { get; init; } = ServiceStartType.Automatic;

    /// <summary>The account it runs as; null is LocalSystem.</summary>
    public string? AccountName { get; init; }

    /// <summary>Services that must run first; empty clears them on change.</summary>
    public IReadOnlyList<string> Dependencies { get; init; } = [];

    /// <summary>Recovery settings; null writes none on create and leaves them unchanged on change.</summary>
    public ServiceFailureActions? FailureActions { get; init; }

    /// <summary>The service SID type; always written.</summary>
    public ServiceSidType SidType { get; init; } = ServiceSidType.None;

    /// <summary>The image path written to the SCM (<see cref="ServiceBinaryPath.Build"/>).</summary>
    public string BinaryPath => ServiceBinaryPath.Build(ExecutablePath, Arguments);
}

/// <summary>A service's configuration as read back from the service manager.</summary>
/// <param name="Name">The service name.</param>
/// <param name="DisplayName">The display name.</param>
/// <param name="Description">The description, or null when it has none.</param>
/// <param name="BinaryPath">The raw image path.</param>
/// <param name="StartType">When it starts.</param>
/// <param name="AccountName">The account it runs as (<c>LocalSystem</c> for LocalSystem).</param>
/// <param name="Dependencies">The services it depends on.</param>
/// <param name="FailureActions">Its recovery settings, or null when it has none.</param>
/// <param name="SidType">Its service SID type.</param>
internal sealed record ServiceConfigurationInfo(
    string Name,
    string DisplayName,
    string? Description,
    string BinaryPath,
    ServiceStartType StartType,
    string? AccountName,
    IReadOnlyList<string> Dependencies,
    ServiceFailureActions? FailureActions,
    ServiceSidType SidType);
