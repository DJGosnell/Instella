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

    /// <summary>Deletes the service; a running one is removed once it stops.</summary>
    Task<ServiceResult> DeleteAsync(string name, CancellationToken ct);
}
