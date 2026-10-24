namespace Instella.Core.Platform;

/// <summary>
/// The service manager on platforms without one this seam drives: every call fails with
/// <see cref="ServiceFailure.NotSupported"/> and a message naming the platform, and never throws.
/// </summary>
internal sealed class UnsupportedServiceManager : IServiceManager
{
    private readonly TargetPlatform _platform;

    public UnsupportedServiceManager(TargetPlatform platform) => _platform = platform;

    private string Message => $"Windows services are not supported on {_platform}";

    private Task<ServiceResult> Fail() => Task.FromResult(ServiceResult.Fail(ServiceFailure.NotSupported, Message));

    private Task<ServiceResult<T>> Fail<T>() => Task.FromResult(ServiceResult<T>.Fail(ServiceFailure.NotSupported, Message));

    public Task<ServiceResult> CreateAsync(ServiceConfiguration configuration, char[]? password, CancellationToken ct)
    {
        if (password is not null) Array.Clear(password);
        return Fail();
    }

    public Task<ServiceResult> ChangeConfigurationAsync(ServiceConfiguration configuration, char[]? password, CancellationToken ct)
    {
        if (password is not null) Array.Clear(password);
        return Fail();
    }

    public Task<ServiceResult<ServiceConfigurationInfo>> QueryConfigurationAsync(string name, CancellationToken ct) => Fail<ServiceConfigurationInfo>();

    public Task<ServiceResult<ServiceStatus>> QueryStatusAsync(string name, CancellationToken ct) => Fail<ServiceStatus>();

    public Task<ServiceResult> StartAsync(string name, TimeSpan timeout, CancellationToken ct) => Fail();

    public Task<ServiceResult> StopAsync(string name, TimeSpan timeout, CancellationToken ct) => Fail();

    public Task<ServiceResult> DeleteAsync(string name, CancellationToken ct) => Fail();
}
