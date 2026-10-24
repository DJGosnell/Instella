using Instella.Core.Installation;
using Instella.Core.Platform;

namespace Instella.Installer.Testing;

/// <summary>
/// An in-memory Windows service manager (<see cref="FakePlatformServices.ServiceManager"/>): services
/// are created, configured, started, stopped and deleted without touching the host. Names are
/// case-insensitive; starts and stops succeed unless <see cref="SetStartBehavior"/> or
/// <see cref="SetStopBehavior"/> says otherwise, and the fake never sleeps.
/// </summary>
/// <remarks>
/// A running service that is deleted is only marked for deletion: it disappears when it stops,
/// and its name cannot be created again until then, as on Windows. Passwords are cleared from the
/// caller's buffer and never kept; <see cref="FakeService.PasswordSupplied"/> says whether one was given.
/// For an account given a password with <see cref="SetAccountPassword"/>, only whether the supplied
/// password matched is kept, and a start fails with error 1069 unless it did.
/// </remarks>
public sealed class FakeServiceManager : IServiceManager
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _services = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FakeServiceBehavior> _startBehavior = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FakeServiceBehavior> _stopBehavior = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _accountPasswords = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _calls = [];
    private int _nextProcessId = 1000;

    /// <summary>The services registered right now, sorted by name.</summary>
    public IReadOnlyList<FakeService> Services
    {
        get
        {
            lock (_gate)
                return _services.Values.Select(e => e.View())
                    .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    /// <summary>The service named <paramref name="name"/> (case-insensitive), or null.</summary>
    public FakeService? Find(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        lock (_gate)
            return _services.TryGetValue(name, out var entry) ? entry.View() : null;
    }

    /// <summary>How starting the service named <paramref name="name"/> goes; it may not exist yet.</summary>
    public void SetStartBehavior(string name, FakeServiceBehavior behavior)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        lock (_gate) _startBehavior[name] = behavior;
    }

    /// <summary>How stopping the service named <paramref name="name"/> goes; it may not exist yet.</summary>
    public void SetStopBehavior(string name, FakeServiceBehavior behavior)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        lock (_gate) _stopBehavior[name] = behavior;
    }

    /// <summary>
    /// Gives <paramref name="account"/> (case-insensitive) a password, as a domain or local account
    /// has one. A service created or reconfigured for that account with a password records only
    /// whether it matched; a change without a password and with the same account keeps the earlier
    /// result. Starting a service under that account fails with error 1069
    /// (<c>ERROR_SERVICE_LOGON_FAILED</c>) unless the last password supplied matched.
    /// </summary>
    public void SetAccountPassword(string account, string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(account);
        ArgumentNullException.ThrowIfNull(password);
        lock (_gate) _accountPasswords[account] = password;
    }

    /// <summary>
    /// Simulates a caller without the rights to change services (not elevated): create, change,
    /// start, stop and delete fail with access denied and change nothing; queries still work.
    /// </summary>
    public bool DenyAccess { get; set; }

    /// <summary>
    /// The operations attempted, in order: <c>create:{name}</c>, <c>config:{name}</c>,
    /// <c>start:{name}</c>, <c>stop:{name}</c> and <c>delete:{name}</c>. Only create is logged for a
    /// name that does not exist. A stop is logged after the stops of its running dependents.
    /// </summary>
    public IReadOnlyList<string> Calls
    {
        get { lock (_gate) return _calls.ToArray(); }
    }

    Task<ServiceResult> IServiceManager.CreateAsync(ServiceConfiguration configuration, char[]? password, CancellationToken ct)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(configuration);
            ct.ThrowIfCancellationRequested();
            _ = configuration.BinaryPath; // refuses an executable path Windows would refuse
            lock (_gate)
            {
                _calls.Add("create:" + configuration.Name);
                if (DenyAccess) return Done(AccessDenied("create", configuration.Name));
                if (_services.TryGetValue(configuration.Name, out var existing))
                    return Done(existing.MarkedForDeletion
                        ? ServiceResult.Fail(ServiceFailure.MarkedForDeletion, $"The service '{existing.Name}' is marked for deletion.", 1072)
                        : ServiceResult.Fail(ServiceFailure.AlreadyExists, $"The service '{existing.Name}' already exists.", 1073));
                if (CreatesACycle(configuration.Name, configuration.Dependencies)) return Done(CircularDependency(configuration.Name));
                _services[configuration.Name] = new Entry(configuration, password is not null)
                {
                    PasswordMatched = password is not null && Matches(configuration.AccountName, password),
                };
                return Done(ServiceResult.Ok);
            }
        }
        finally
        {
            if (password is not null) Array.Clear(password);
        }
    }

    Task<ServiceResult> IServiceManager.ChangeConfigurationAsync(ServiceConfiguration configuration, char[]? password, CancellationToken ct)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(configuration);
            ct.ThrowIfCancellationRequested();
            _ = configuration.BinaryPath;
            lock (_gate)
            {
                if (!_services.TryGetValue(configuration.Name, out var entry)) return Done(NotFound(configuration.Name));
                _calls.Add("config:" + entry.Name);
                if (DenyAccess) return Done(AccessDenied("change the configuration of", entry.Name));
                if (entry.MarkedForDeletion) return Done(MarkedForDeletion(entry.Name));
                if (CreatesACycle(entry.Name, configuration.Dependencies)) return Done(CircularDependency(entry.Name));

                var old = entry.Configuration;
                var sameAccount = string.Equals(old.AccountName, configuration.AccountName, StringComparison.OrdinalIgnoreCase);
                entry.PasswordSupplied = password is not null || (entry.PasswordSupplied && sameAccount);
                entry.PasswordMatched = password is not null
                    ? Matches(configuration.AccountName, password)
                    : entry.PasswordMatched && sameAccount;
                // Null description and failure actions leave them as they are, as on Windows.
                entry.Configuration = configuration with
                {
                    Name = entry.Name,
                    Description = configuration.Description ?? old.Description,
                    FailureActions = configuration.FailureActions ?? old.FailureActions,
                };
                return Done(ServiceResult.Ok);
            }
        }
        finally
        {
            if (password is not null) Array.Clear(password);
        }
    }

    Task<ServiceResult<ServiceConfigurationInfo>> IServiceManager.QueryConfigurationAsync(string name, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(name);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_services.TryGetValue(name, out var entry))
                return Task.FromResult(ServiceResult<ServiceConfigurationInfo>.Fail(ServiceFailure.NotFound, NotFoundMessage(name), 1060));
            var c = entry.Configuration;
            return Task.FromResult(ServiceResult<ServiceConfigurationInfo>.Ok(new ServiceConfigurationInfo(
                entry.Name, c.DisplayName, c.Description, c.BinaryPath, c.StartType, c.AccountName ?? "LocalSystem",
                c.Dependencies.ToArray(), c.FailureActions, c.SidType)));
        }
    }

    Task<ServiceResult<ServiceStatus>> IServiceManager.QueryStatusAsync(string name, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(name);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_services.TryGetValue(name, out var entry))
                return Task.FromResult(ServiceResult<ServiceStatus>.Fail(ServiceFailure.NotFound, NotFoundMessage(name), 1060));
            var state = entry.State switch
            {
                FakeServiceState.StartPending => ServiceState.StartPending,
                FakeServiceState.Running => ServiceState.Running,
                FakeServiceState.StopPending => ServiceState.StopPending,
                _ => ServiceState.Stopped,
            };
            return Task.FromResult(ServiceResult<ServiceStatus>.Ok(new ServiceStatus(state, entry.ProcessId)));
        }
    }

    Task<ServiceResult> IServiceManager.StartAsync(string name, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(name);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_services.TryGetValue(name, out var entry)) return Done(NotFound(name));
            _calls.Add("start:" + entry.Name);
            if (DenyAccess) return Done(AccessDenied("start", entry.Name));
            if (entry.MarkedForDeletion) return Done(MarkedForDeletion(entry.Name));
            if (entry.State == FakeServiceState.Running) return Done(ServiceResult.Ok);
            if (entry.State == FakeServiceState.StopPending)
                return Done(ServiceResult.Fail(ServiceFailure.Failed, $"The service '{entry.Name}' is stopping."));

            if (entry.Configuration.AccountName is { } account && _accountPasswords.ContainsKey(account) && !entry.PasswordMatched)
            {
                entry.State = FakeServiceState.Stopped;
                return Done(ServiceResult.Fail(ServiceFailure.Failed, $"StartService failed for '{entry.Name}' (error 1069).", 1069));
            }

            switch (_startBehavior.GetValueOrDefault(entry.Name))
            {
                case FakeServiceBehavior.Fail:
                    entry.State = FakeServiceState.Stopped;
                    return Done(ServiceResult.Fail(ServiceFailure.Failed, $"The service '{entry.Name}' stopped while starting (exit code 1)."));
                case FakeServiceBehavior.Hang:
                    entry.State = FakeServiceState.StartPending;
                    return Done(ServiceResult.Fail(ServiceFailure.Timeout, $"The service '{entry.Name}' did not reach Running within {timeout.TotalSeconds:0.###} s (it is StartPending)."));
                default:
                    entry.State = FakeServiceState.Running;
                    entry.ProcessId = _nextProcessId++;
                    return Done(ServiceResult.Ok);
            }
        }
    }

    Task<ServiceResult> IServiceManager.StopAsync(string name, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(name);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_services.TryGetValue(name, out var entry)) return Done(NotFound(name));
            return Done(Stop(entry, timeout, new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
        }
    }

    Task<ServiceResult<IReadOnlyList<string>>> IServiceManager.QueryActiveDependentsAsync(string name, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(name);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_services.TryGetValue(name, out var entry))
                return Task.FromResult(ServiceResult<IReadOnlyList<string>>.Fail(ServiceFailure.NotFound, NotFoundMessage(name), 1060));
            var found = new List<string>();
            var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { entry.Name };
            void Collect(Entry of)
            {
                foreach (var dependent in RunningDependents(of))
                {
                    if (!visiting.Add(dependent.Name)) continue;
                    Collect(dependent);
                    found.Add(dependent.Name);
                }
            }
            Collect(entry);
            return Task.FromResult(ServiceResult<IReadOnlyList<string>>.Ok(found));
        }
    }

    Task<ServiceResult> IServiceManager.DeleteAsync(string name, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(name);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_services.TryGetValue(name, out var entry)) return Done(NotFound(name));
            _calls.Add("delete:" + entry.Name);
            if (DenyAccess) return Done(AccessDenied("delete", entry.Name));
            if (entry.MarkedForDeletion) return Done(MarkedForDeletion(entry.Name));
            if (entry.State == FakeServiceState.Stopped) _services.Remove(entry.Name);
            else entry.MarkedForDeletion = true;
            return Done(ServiceResult.Ok);
        }
    }

    /// <summary>
    /// True when giving <paramref name="name"/> these <paramref name="dependencies"/> would make it
    /// depend on itself, directly or through other services: the SCM refuses that
    /// (<c>ERROR_CIRCULAR_DEPENDENCY</c>). Dependencies that do not exist are allowed, as on Windows.
    /// </summary>
    private bool CreatesACycle(string name, IReadOnlyList<string> dependencies)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>(dependencies);
        while (pending.Count > 0)
        {
            var next = pending.Pop();
            if (string.Equals(next, name, StringComparison.OrdinalIgnoreCase)) return true;
            if (!seen.Add(next) || !_services.TryGetValue(next, out var dependency)) continue;
            foreach (var d in dependency.Configuration.Dependencies) pending.Push(d);
        }
        return false;
    }

    /// <summary>
    /// Stops the running dependents (recursively), then the service. Called under the lock;
    /// <paramref name="visiting"/> keeps a dependency cycle from recursing without end.
    /// </summary>
    private ServiceResult Stop(Entry entry, TimeSpan timeout, HashSet<string> visiting)
    {
        if (!visiting.Add(entry.Name)) return ServiceResult.Ok;
        if (DenyAccess)
        {
            _calls.Add("stop:" + entry.Name);
            return AccessDenied("stop", entry.Name);
        }
        if (entry.State != FakeServiceState.Stopped)
        {
            foreach (var dependent in RunningDependents(entry))
            {
                var stopped = Stop(dependent, timeout, visiting);
                if (!stopped.Outcome.Success) return stopped;
            }
        }

        _calls.Add("stop:" + entry.Name);
        if (entry.State == FakeServiceState.Stopped) return ServiceResult.Ok;
        switch (_stopBehavior.GetValueOrDefault(entry.Name))
        {
            case FakeServiceBehavior.Fail:
                return ServiceResult.Fail(ServiceFailure.Failed, $"The service '{entry.Name}' could not be stopped.");
            case FakeServiceBehavior.Hang:
                entry.State = FakeServiceState.StopPending;
                return ServiceResult.Fail(ServiceFailure.Timeout, $"The service '{entry.Name}' did not reach Stopped within {timeout.TotalSeconds:0.###} s (it is StopPending).");
            default:
                entry.State = FakeServiceState.Stopped;
                entry.ProcessId = 0;
                if (entry.MarkedForDeletion) _services.Remove(entry.Name);
                return ServiceResult.Ok;
        }
    }

    /// <summary>The services that are not stopped and depend directly on <paramref name="entry"/>. Called under the lock.</summary>
    private Entry[] RunningDependents(Entry entry) => _services.Values
        .Where(d => d.State != FakeServiceState.Stopped
            && d.Configuration.Dependencies.Contains(entry.Name, StringComparer.OrdinalIgnoreCase))
        .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    /// <summary>Whether <paramref name="password"/> is the one set for <paramref name="account"/>; true for an account without one. Called under the lock.</summary>
    private bool Matches(string? account, char[] password) =>
        account is null || !_accountPasswords.TryGetValue(account, out var expected)
        || password.AsSpan().SequenceEqual(expected);

    private static Task<ServiceResult> Done(ServiceResult result) => Task.FromResult(result);

    private static string NotFoundMessage(string name) => $"The service '{name}' does not exist.";

    private static ServiceResult NotFound(string name) => ServiceResult.Fail(ServiceFailure.NotFound, NotFoundMessage(name), 1060);

    private static ServiceResult AccessDenied(string operation, string name) =>
        ServiceResult.Fail(ServiceFailure.AccessDenied, $"Could not {operation} service '{name}': access is denied.", 5);

    private static ServiceResult CircularDependency(string name) =>
        ServiceResult.Fail(ServiceFailure.Failed, $"The service '{name}' would depend on itself (circular dependency).", 1059);

    private static ServiceResult MarkedForDeletion(string name) =>
        ServiceResult.Fail(ServiceFailure.MarkedForDeletion, $"The service '{name}' is marked for deletion.", 1072);

    private sealed class Entry(ServiceConfiguration configuration, bool passwordSupplied)
    {
        public string Name { get; } = configuration.Name;
        public ServiceConfiguration Configuration { get; set; } = configuration;
        public bool PasswordSupplied { get; set; } = passwordSupplied;
        public bool PasswordMatched { get; set; }
        public FakeServiceState State { get; set; } = FakeServiceState.Stopped;
        public int ProcessId { get; set; }
        public bool MarkedForDeletion { get; set; }

        public FakeService View() => new(
            Name, Configuration.DisplayName, Configuration.Description, Configuration.BinaryPath,
            Configuration.StartType switch
            {
                ServiceStartType.AutomaticDelayed => ManifestService.AutomaticDelayed,
                ServiceStartType.Manual => ManifestService.Manual,
                ServiceStartType.Disabled => ManifestService.Disabled,
                _ => ManifestService.Automatic,
            },
            Configuration.AccountName, PasswordSupplied, Configuration.Dependencies.ToArray(), State, ProcessId,
            MarkedForDeletion);
    }
}

/// <summary>How a <see cref="FakeServiceManager"/> start or stop goes.</summary>
public enum FakeServiceBehavior
{
    /// <summary>The service reaches the wanted state.</summary>
    Succeed,

    /// <summary>The call fails; the service keeps its state (a failed start leaves it stopped).</summary>
    Fail,

    /// <summary>The call times out; the service is left start- or stop-pending.</summary>
    Hang,
}

/// <summary>The state of a <see cref="FakeService"/>.</summary>
public enum FakeServiceState
{
    /// <summary>Not running.</summary>
    Stopped,

    /// <summary>Stuck starting (a <see cref="FakeServiceBehavior.Hang"/> start).</summary>
    StartPending,

    /// <summary>Running.</summary>
    Running,

    /// <summary>Stuck stopping (a <see cref="FakeServiceBehavior.Hang"/> stop).</summary>
    StopPending,
}

/// <summary>A service registered with a <see cref="FakeServiceManager"/>, as it is now.</summary>
/// <param name="Name">The service name, in the case it was created with.</param>
/// <param name="DisplayName">The display name.</param>
/// <param name="Description">The description, or null.</param>
/// <param name="BinaryPath">The image path: the quoted executable, then its arguments.</param>
/// <param name="StartType">One of the <see cref="ManifestService"/> start-type strings.</param>
/// <param name="AccountName">The account it runs as; null for LocalSystem.</param>
/// <param name="PasswordSupplied">Whether a password was given for the account (it is never kept).</param>
/// <param name="Dependencies">The services it depends on.</param>
/// <param name="State">Its state.</param>
/// <param name="ProcessId">Its fake process id while running; 0 otherwise.</param>
/// <param name="MarkedForDeletion">Deleted while running: it disappears when it stops.</param>
public sealed record FakeService(
    string Name,
    string DisplayName,
    string? Description,
    string BinaryPath,
    string StartType,
    string? AccountName,
    bool PasswordSupplied,
    IReadOnlyList<string> Dependencies,
    FakeServiceState State,
    int ProcessId,
    bool MarkedForDeletion);
