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
/// The account a Windows service runs as: a built-in account, the per-service virtual account, a
/// group-managed service account (<see cref="ManagedServiceAccount"/>) or a user account the person
/// installing supplies (<see cref="FromInstaller"/>). Only the last needs a password.
/// </summary>
public sealed class ServiceAccount
{
    private enum Kind { LocalSystem, LocalService, NetworkService, Virtual, Managed, FromInstaller }

    private readonly Kind _kind;
    private readonly string? _name;

    private ServiceAccount(Kind kind, string? name = null)
    {
        _kind = kind;
        _name = name;
    }

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

    /// <summary>
    /// A group-managed service account, <c>DOMAIN\name$</c>: Windows supplies its password, so none is
    /// asked for. The account is granted "Log on as a service" (<c>SeServiceLogonRight</c>) when it
    /// lacks it.
    /// </summary>
    /// <exception cref="ArgumentException">The name is not <c>DOMAIN\name$</c>.</exception>
    public static ServiceAccount ManagedServiceAccount(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var slash = name.IndexOf('\\', StringComparison.Ordinal);
        if (slash <= 0 || slash != name.LastIndexOf('\\') || !name.EndsWith('$') || name.Length - slash - 1 < 2)
            throw new ArgumentException($"a group-managed service account is 'DOMAIN\\name$', not '{name}'", nameof(name));
        return new ServiceAccount(Kind.Managed, name);
    }

    /// <summary>
    /// A user account the person installing supplies: on the built-in <c>service-account</c> wizard
    /// page, or silently with <c>--service-account</c> and <c>--service-password-file</c> (or the
    /// <c>INSTELLA_SERVICE_PASSWORD</c> environment variable). Every service using it shares the one
    /// account; the <paramref name="defaultUser"/> of the first such service pre-fills the page and is
    /// the silent default. A name without <c>\</c> or <c>@</c> is a local account (<c>.\name</c>).
    /// </summary>
    public static ServiceAccount FromInstaller(string? defaultUser = null) =>
        new(Kind.FromInstaller, string.IsNullOrWhiteSpace(defaultUser) ? null : NormalizeUser(defaultUser));

    /// <summary>
    /// The account name as the service manager takes it: a name without <c>\</c> and without
    /// <c>@</c> gets <c>.\</c> (a local account); any other is used as given, trimmed.
    /// </summary>
    internal static string NormalizeUser(string user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var trimmed = user.Trim();
        return trimmed.Length == 0 || trimmed.Contains('\\') || trimmed.Contains('@') ? trimmed : @".\" + trimmed;
    }

    /// <summary>True for <see cref="FromInstaller"/>: the account is resolved at install time.</summary>
    internal bool IsFromInstaller => _kind == Kind.FromInstaller;

    /// <summary>True for a custom account (gMSA or installer-supplied), as opposed to a built-in or virtual one.</summary>
    internal bool IsCustom => _kind is Kind.Managed or Kind.FromInstaller;

    /// <summary>True when the service manager needs a password for the account (<see cref="FromInstaller"/>).</summary>
    internal bool NeedsPassword => _kind == Kind.FromInstaller;

    /// <summary>True when the account needs "Log on as a service" granted; built-in and virtual accounts have it.</summary>
    internal bool NeedsLogonRight => IsCustom;

    /// <summary>The <see cref="FromInstaller"/> default user (normalised), or null.</summary>
    internal string? DefaultUser => _kind == Kind.FromInstaller ? _name : null;

    /// <summary>
    /// The account name written to the service manager; null is LocalSystem. For
    /// <see cref="FromInstaller"/> it is the default user, until the install resolves the account.
    /// </summary>
    internal string? AccountNameFor(string serviceName) => _kind switch
    {
        Kind.LocalSystem => null,
        Kind.LocalService => @"NT AUTHORITY\LocalService",
        Kind.NetworkService => @"NT AUTHORITY\NetworkService",
        Kind.Managed or Kind.FromInstaller => _name,
        _ => @"NT SERVICE\" + serviceName,
    };

    /// <summary>The service SID type the account needs.</summary>
    internal ServiceSidType SidType => _kind == Kind.Virtual ? ServiceSidType.Unrestricted : ServiceSidType.None;

    /// <summary>
    /// True when <paramref name="accountName"/>, as an installed manifest records it, is a custom
    /// account: not null, not <c>NT AUTHORITY\…</c> or <c>NT SERVICE\…</c>, not LocalSystem and not a
    /// gMSA (<c>…$</c>).
    /// </summary>
    internal static bool IsRecordedCustomUser(string? accountName) =>
        !string.IsNullOrWhiteSpace(accountName)
        && !accountName.StartsWith(@"NT AUTHORITY\", StringComparison.OrdinalIgnoreCase)
        && !accountName.StartsWith(@"NT SERVICE\", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(accountName, "LocalSystem", StringComparison.OrdinalIgnoreCase)
        && !accountName.EndsWith('$');

    /// <inheritdoc/>
    public override string ToString() => _name is null ? _kind.ToString() : $"{_kind}({_name})";
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
