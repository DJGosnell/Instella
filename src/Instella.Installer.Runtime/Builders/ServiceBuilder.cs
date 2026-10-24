using System;
using System.Collections.Generic;
using Instella.Core.Installation;
using Instella.Core.Platform;

namespace Instella.Installer.Runtime.Builders;

/// <summary>When a Windows service declared with <see cref="WindowsBuilder.AddService"/> starts.</summary>
public enum ServiceStartMode
{
    /// <summary>At boot. Started after install unless <see cref="ServiceBuilder.StartAfterInstall"/> says otherwise.</summary>
    Automatic,

    /// <summary>Shortly after boot. Started after install unless <see cref="ServiceBuilder.StartAfterInstall"/> says otherwise.</summary>
    AutomaticDelayed,

    /// <summary>On demand. Not started after install unless <see cref="ServiceBuilder.StartAfterInstall"/> is called.</summary>
    Manual,

    /// <summary>Never; the service is registered but not started.</summary>
    Disabled,
}

/// <summary>
/// The account a Windows service runs as. Only the built-in accounts and the per-service virtual
/// account are supported; none of them needs a password.
/// </summary>
public sealed class ServiceAccount
{
    private enum Kind { LocalSystem, LocalService, NetworkService, Virtual }

    private readonly Kind _kind;

    private ServiceAccount(Kind kind) => _kind = kind;

    /// <summary>LocalSystem, the default: full rights on the machine.</summary>
    public static ServiceAccount LocalSystem { get; } = new(Kind.LocalSystem);

    /// <summary><c>NT AUTHORITY\LocalService</c>: few rights, anonymous on the network.</summary>
    public static ServiceAccount LocalService { get; } = new(Kind.LocalService);

    /// <summary><c>NT AUTHORITY\NetworkService</c>: few rights, the machine's identity on the network.</summary>
    public static ServiceAccount NetworkService { get; } = new(Kind.NetworkService);

    /// <summary>
    /// The service's own virtual account, <c>NT SERVICE\{name}</c>, with an unrestricted service SID:
    /// few rights, and files can be granted to that one service.
    /// </summary>
    public static ServiceAccount VirtualAccount { get; } = new(Kind.Virtual);

    /// <summary>The account name written to the service manager; null is LocalSystem.</summary>
    internal string? AccountNameFor(string serviceName) => _kind switch
    {
        Kind.LocalSystem => null,
        Kind.LocalService => @"NT AUTHORITY\LocalService",
        Kind.NetworkService => @"NT AUTHORITY\NetworkService",
        _ => @"NT SERVICE\" + serviceName,
    };

    /// <summary>The service SID type the account needs.</summary>
    internal ServiceSidType SidType => _kind == Kind.Virtual ? ServiceSidType.Unrestricted : ServiceSidType.None;

    /// <inheritdoc/>
    public override string ToString() => _kind.ToString();
}

/// <summary>
/// Configures one Windows service, obtained through <see cref="WindowsBuilder.AddService"/>. Every
/// method returns this builder. Arguments are checked when given; everything else is checked by
/// <see cref="InstallerBuilder.Build"/>.
/// </summary>
public sealed class ServiceBuilder
{
    private readonly string _name;
    private string? _displayName;
    private string? _description;
    private string? _executable;
    private string[] _arguments = [];
    private ServiceStartMode _startMode = ServiceStartMode.Automatic;
    private readonly List<string> _dependencies = new();
    private ServiceFailureActions? _failureActions;
    private ServiceAccount _account = ServiceAccount.LocalSystem;
    private bool? _startAfterInstall;
    private TimeSpan _startTimeout = ServiceSpec.DefaultStartTimeout;
    private TimeSpan _stopTimeout = ServiceSpec.DefaultStopTimeout;
    private bool _startFailureIsWarning;

    internal ServiceBuilder(string name) => _name = name;

    /// <summary>The name shown in the Services console. Defaults to the service name.</summary>
    public ServiceBuilder WithDisplayName(string displayName)
    {
        ArgumentException.ThrowIfNullOrEmpty(displayName);
        _displayName = displayName;
        return this;
    }

    /// <summary>The description shown in the Services console. Defaults to none.</summary>
    public ServiceBuilder WithDescription(string description)
    {
        ArgumentException.ThrowIfNullOrEmpty(description);
        _description = description;
        return this;
    }

    /// <summary>
    /// The service executable, relative to the install directory (for example <c>bin/Worker.exe</c>).
    /// It must be in the payload. Defaults to the app's main executable.
    /// </summary>
    public ServiceBuilder WithExecutable(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        _executable = relativePath;
        return this;
    }

    /// <summary>Arguments passed to the executable; each is quoted as needed.</summary>
    public ServiceBuilder WithArguments(params string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        _arguments = [.. arguments];
        return this;
    }

    /// <summary>When the service starts. Defaults to <see cref="ServiceStartMode.Automatic"/>.</summary>
    public ServiceBuilder WithStartType(ServiceStartMode mode)
    {
        _startMode = mode;
        return this;
    }

    /// <summary>Services that must run before this one (by service name). Calls accumulate.</summary>
    public ServiceBuilder DependsOn(params string[] serviceNames)
    {
        ArgumentNullException.ThrowIfNull(serviceNames);
        foreach (var name in serviceNames)
        {
            ArgumentException.ThrowIfNullOrEmpty(name, nameof(serviceNames));
            _dependencies.Add(name);
        }
        return this;
    }

    /// <summary>
    /// When the service fails, the service manager restarts it after <paramref name="delay"/>, up to
    /// three times; the failure count is reset after <paramref name="resetAfter"/> without a failure.
    /// </summary>
    public ServiceBuilder RestartOnFailure(TimeSpan delay, TimeSpan resetAfter)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(resetAfter, TimeSpan.Zero);
        var restart = new ServiceFailureAction(ServiceFailureActionType.Restart, delay);
        _failureActions = new ServiceFailureActions(resetAfter, [restart, restart, restart]);
        return this;
    }

    /// <summary>The account the service runs as. Defaults to <see cref="ServiceAccount.LocalSystem"/>.</summary>
    public ServiceBuilder RunAs(ServiceAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        _account = account;
        return this;
    }

    /// <summary>
    /// Whether the service is started after install, upgrade and repair. Without this call,
    /// automatic services are started and manual and disabled ones are not. A disabled service is
    /// never started.
    /// </summary>
    public ServiceBuilder StartAfterInstall(bool start = true)
    {
        _startAfterInstall = start;
        return this;
    }

    /// <summary>How long a start may take before it counts as failed. Defaults to 60 seconds.</summary>
    public ServiceBuilder StartTimeout(TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        _startTimeout = timeout;
        return this;
    }

    /// <summary>How long a stop may take before it counts as failed. Defaults to 30 seconds.</summary>
    public ServiceBuilder StopTimeout(TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        _stopTimeout = timeout;
        return this;
    }

    /// <summary>
    /// A failed start is logged as a warning and the install goes on, instead of failing and rolling
    /// back with <see cref="InstellaExitCode.InstallServiceFailed"/>. Only for a service that is
    /// started after install.
    /// </summary>
    public ServiceBuilder StartFailureIsWarning()
    {
        _startFailureIsWarning = true;
        return this;
    }

    internal ServiceSpec Build() => new(
        Name: _name,
        DisplayName: _displayName ?? _name,
        Description: _description,
        Executable: _executable,
        Arguments: _arguments,
        StartMode: _startMode,
        Dependencies: _dependencies.ToArray(),
        FailureActions: _failureActions,
        Account: _account,
        StartAfterInstallSetting: _startAfterInstall,
        StartTimeout: _startTimeout,
        StopTimeout: _stopTimeout,
        StartFailureIsWarning: _startFailureIsWarning);
}

/// <summary>
/// A declared Windows service, frozen by <see cref="InstallerBuilder.Build"/>: every
/// <see cref="ServiceBuilder"/> setting, with its defaults resolved.
/// </summary>
/// <remarks>
/// <c>Executable</c> is relative to the install directory, normalised by <c>Build()</c> (<c>/</c>
/// separators); null is the app's main executable. <c>StartAfterInstallSetting</c> is what
/// <see cref="ServiceBuilder.StartAfterInstall"/> said, null when it was not called.
/// </remarks>
internal sealed record ServiceSpec(
    string Name,
    string DisplayName,
    string? Description,
    string? Executable,
    IReadOnlyList<string> Arguments,
    ServiceStartMode StartMode,
    IReadOnlyList<string> Dependencies,
    ServiceFailureActions? FailureActions,
    ServiceAccount Account,
    bool? StartAfterInstallSetting,
    TimeSpan StartTimeout,
    TimeSpan StopTimeout,
    bool StartFailureIsWarning)
{
    /// <summary>The start timeout without <see cref="ServiceBuilder.StartTimeout"/>.</summary>
    public static readonly TimeSpan DefaultStartTimeout = TimeSpan.FromSeconds(60);

    /// <summary>The stop timeout without <see cref="ServiceBuilder.StopTimeout"/>.</summary>
    public static readonly TimeSpan DefaultStopTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Whether the service is started after install: <see cref="StartAfterInstallSetting"/> when given,
    /// otherwise true for automatic services. A disabled service never starts.
    /// </summary>
    public bool StartsAfterInstall => StartMode != ServiceStartMode.Disabled
        && (StartAfterInstallSetting ?? StartMode is ServiceStartMode.Automatic or ServiceStartMode.AutomaticDelayed);

    /// <summary>The account name written to the service manager; null is LocalSystem.</summary>
    public string? AccountName => Account.AccountNameFor(Name);

    /// <summary>The service SID type written to the service manager.</summary>
    public ServiceSidType SidType => Account.SidType;

    /// <summary>The service manager's start type.</summary>
    public ServiceStartType StartType => StartMode switch
    {
        ServiceStartMode.AutomaticDelayed => ServiceStartType.AutomaticDelayed,
        ServiceStartMode.Manual => ServiceStartType.Manual,
        ServiceStartMode.Disabled => ServiceStartType.Disabled,
        _ => ServiceStartType.Automatic,
    };

    /// <summary>The start type as the installed manifest records it (<see cref="ManifestService"/>).</summary>
    public string ManifestStartType => StartMode switch
    {
        ServiceStartMode.AutomaticDelayed => ManifestService.AutomaticDelayed,
        ServiceStartMode.Manual => ManifestService.Manual,
        ServiceStartMode.Disabled => ManifestService.Disabled,
        _ => ManifestService.Automatic,
    };

    /// <summary>The executable relative to the install directory: <see cref="Executable"/>, or the main executable.</summary>
    public string? ExecutableOr(string? mainExecutable) => Executable ?? mainExecutable;
}
