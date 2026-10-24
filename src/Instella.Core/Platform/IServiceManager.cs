namespace Instella.Core.Platform;

/// <summary>
/// Creates, configures, queries, starts, stops and deletes operating-system services (the Windows
/// Service Control Manager). Every call returns a <see cref="ServiceResult"/> rather than throwing;
/// only cancellation throws <see cref="OperationCanceledException"/>.
/// </summary>
/// <remarks>
/// Passwords travel as a <c>char[]</c>: when <see cref="CreateAsync"/> or
/// <see cref="ChangeConfigurationAsync"/> returns, success or failure, every element of the array is
/// <c>'\0'</c>. Implementations never put a password in a result, a message or a log.
/// </remarks>
internal interface IServiceManager
{
    /// <summary>Creates an own-process service from <paramref name="configuration"/>.</summary>
    Task<ServiceResult> CreateAsync(ServiceConfiguration configuration, char[]? password, CancellationToken ct);

    /// <summary>Applies every field of <paramref name="configuration"/> to the service it names.</summary>
    Task<ServiceResult> ChangeConfigurationAsync(ServiceConfiguration configuration, char[]? password, CancellationToken ct);

    /// <summary>Reads the service's configuration.</summary>
    Task<ServiceResult<ServiceConfigurationInfo>> QueryConfigurationAsync(string name, CancellationToken ct);

    /// <summary>Reads the service's state and process id.</summary>
    Task<ServiceResult<ServiceStatus>> QueryStatusAsync(string name, CancellationToken ct);

    /// <summary>Starts the service and waits up to <paramref name="timeout"/> for it to run.</summary>
    Task<ServiceResult> StartAsync(string name, TimeSpan timeout, CancellationToken ct);

    /// <summary>Stops the service (its running dependents first) and waits up to <paramref name="timeout"/>.</summary>
    Task<ServiceResult> StopAsync(string name, TimeSpan timeout, CancellationToken ct);

    /// <summary>
    /// The services, direct and indirect, that depend on <paramref name="name"/> and are not
    /// stopped: the ones <see cref="StopAsync"/> stops first, in the order it stops them.
    /// </summary>
    Task<ServiceResult<IReadOnlyList<string>>> QueryActiveDependentsAsync(string name, CancellationToken ct);

    /// <summary>Deletes the service; a running one is removed once it stops.</summary>
    Task<ServiceResult> DeleteAsync(string name, CancellationToken ct);

    /// <summary>
    /// Ends the process of a service stuck in <see cref="ServiceState.StopPending"/> and waits up to
    /// <paramref name="timeout"/> for it to be stopped. Succeeds at once for a stopped service and
    /// fails for one that is not stopping; never ends the calling process.
    /// </summary>
    /// <remarks>Implementations without a way to end a process keep this default: not supported.</remarks>
    Task<ServiceResult> EndStopPendingProcessAsync(string name, TimeSpan timeout, CancellationToken ct) =>
        Task.FromResult(ServiceResult.Fail(ServiceFailure.NotSupported, $"Cannot end the process of service '{name}' here."));

    /// <summary>
    /// Whether the service is deleted but still registered (running, or held open by another
    /// program such as the Services console): it goes once the last handle closes, and its name
    /// cannot be created again until then.
    /// </summary>
    /// <remarks>Implementations that cannot tell keep this default: not supported.</remarks>
    Task<ServiceResult<bool>> IsMarkedForDeletionAsync(string name, CancellationToken ct) =>
        Task.FromResult(ServiceResult<bool>.Fail(ServiceFailure.NotSupported, $"Cannot tell whether service '{name}' is marked for deletion here."));
}
