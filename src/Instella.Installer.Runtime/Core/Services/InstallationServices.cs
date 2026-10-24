using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Installation;
using Instella.Core.Logging;
using Instella.Core.Platform;
using Instella.Installer.Runtime.Builders;

namespace Instella.Installer.Runtime.Core.Services;

/// <summary>
/// The Windows services an installation records (<see cref="InstalledManifest.Services"/>), as the
/// updater, uninstall and recovery stop, start and delete them. Each service's timeouts and
/// <see cref="ServiceSpec.StartFailureIsWarning"/> come from the stub's declaration of the same
/// name; a recorded service the stub does not declare uses the defaults and is never a warning.
/// </summary>
internal sealed class InstallationServices
{
    /// <summary>How long ending a stuck service's process may take to leave it stopped.</summary>
    internal static readonly TimeSpan EndProcessTimeout = TimeSpan.FromSeconds(10);

    private readonly IReadOnlyList<ServiceSpec> _declared;
    private readonly IInstellaLogger _log;

    private InstallationServices(IServiceManager manager, IReadOnlyList<string> names, IReadOnlyList<ServiceSpec> declared, IInstellaLogger log)
    {
        Manager = manager;
        Names = names;
        _declared = declared;
        _log = log;
    }

    /// <summary>
    /// The services <paramref name="manifest"/> records, or null when it records none: then the
    /// service manager is never created (null <paramref name="manager"/> uses the OS's own).
    /// </summary>
    public static InstallationServices? For(InstalledManifest? manifest, IReadOnlyList<ServiceSpec>? declared,
        IServiceManager? manager, IInstellaLogger log)
    {
        if (manifest?.Services is not { Count: > 0 } recorded) return null;
        var names = recorded.Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new InstallationServices(manager ?? ServiceManagerFactory.Create(), names, declared ?? [], log);
    }

    /// <summary>The service manager every call goes through.</summary>
    public IServiceManager Manager { get; }

    /// <summary>The recorded service names, in manifest order.</summary>
    public IReadOnlyList<string> Names { get; }

    /// <summary>The stub's declaration of <paramref name="name"/>, or null.</summary>
    private ServiceSpec? Declared(string name) =>
        _declared.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    public TimeSpan StopTimeout(string name) => Declared(name)?.StopTimeout ?? ServiceSpec.DefaultStopTimeout;

    public TimeSpan StartTimeout(string name) => Declared(name)?.StartTimeout ?? ServiceSpec.DefaultStartTimeout;

    public bool StartFailureIsWarning(string name) => Declared(name)?.StartFailureIsWarning ?? false;

    /// <summary>
    /// The recorded services, each after the recorded services it depends on (the order to start
    /// them in; reversed, the order to stop them in), from the dependencies the service manager
    /// reports. A service it cannot read keeps its manifest position; ties keep manifest order.
    /// </summary>
    public async Task<IReadOnlyList<string>> InStartOrderAsync(CancellationToken ct)
    {
        var dependencies = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in Names)
        {
            var config = await Manager.QueryConfigurationAsync(name, ct);
            dependencies[name] = config.Outcome.Success ? config.Value!.Dependencies : [];
        }

        var ordered = new List<string>(Names.Count);
        var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Place(string name)
        {
            if (placed.Contains(name) || !visiting.Add(name)) return; // a cycle: the SCM refuses those
            foreach (var dependency in dependencies[name])
                if (dependencies.ContainsKey(dependency)) Place(Names.First(n => string.Equals(n, dependency, StringComparison.OrdinalIgnoreCase)));
            if (placed.Add(name)) ordered.Add(name);
        }
        foreach (var name in Names) Place(name);
        return ordered;
    }

    /// <summary>
    /// Stops every recorded service that exists and is not stopped, dependents first. A stop that
    /// times out with the service still stopping ends its process; a stopped (or gone) service
    /// counts as stopped. Stops at the first service that will not stop.
    /// </summary>
    /// <returns>
    /// What was stopped, in the order it was stopped (running dependents the service manager
    /// stopped along with a service included, such as another product's), and the error naming the
    /// service that would not stop, or null.
    /// </returns>
    public async Task<StopResult> StopAllAsync(CancellationToken ct)
    {
        var stopped = new List<string>();
        foreach (var name in (await InStartOrderAsync(ct)).Reverse())
        {
            var status = await Manager.QueryStatusAsync(name, ct);
            if (!status.Outcome.Success)
            {
                if (status.Failure == ServiceFailure.NotFound) continue;
                return new StopResult(stopped, $"service '{name}' could not be stopped: {status.Outcome.Error}");
            }
            if (status.Value!.State == ServiceState.Stopped) continue;

            var dependents = await Manager.QueryActiveDependentsAsync(name, ct);
            if (!dependents.Outcome.Success)
                return new StopResult(stopped, $"service '{name}' could not be stopped: {dependents.Outcome.Error}");

            var error = await StopOneAsync(name, ct);
            // The dependents the service manager stopped first, whatever came of this service.
            foreach (var dependent in dependents.Value!)
                if (!stopped.Contains(dependent, StringComparer.OrdinalIgnoreCase)
                    && await IsStoppedOrGoneAsync(dependent, ct)) stopped.Add(dependent);
            if (error is not null) return new StopResult(stopped, error);
            stopped.Add(name);
            _log.Info($"services: stopped '{name}'");
        }
        return new StopResult(stopped, null);
    }

    /// <summary>Stops one service, ending its process when it hangs stopping. Returns the error, or null.</summary>
    private async Task<string?> StopOneAsync(string name, CancellationToken ct)
    {
        var result = await Manager.StopAsync(name, StopTimeout(name), ct);
        if (result.Outcome.Success) return null;
        if (result.Failure == ServiceFailure.NotFound) return null;
        if (result.Failure == ServiceFailure.Timeout)
        {
            var status = await Manager.QueryStatusAsync(name, ct);
            if (status.Outcome.Success && status.Value is { State: ServiceState.StopPending, ProcessId: > 0 } pending
                && pending.ProcessId != Environment.ProcessId)
            {
                _log.Warn($"services: '{name}' did not stop in {StopTimeout(name).TotalSeconds:0.###} s; ending its process {pending.ProcessId}");
                var ended = await Manager.EndStopPendingProcessAsync(name, EndProcessTimeout, ct);
                if (await IsStoppedOrGoneAsync(name, ct)) return null;
                if (!ended.Outcome.Success)
                    return $"service '{name}' could not be stopped: {result.Outcome.Error} Ending its process failed: {ended.Outcome.Error}";
            }
        }
        return $"service '{name}' could not be stopped: {result.Outcome.Error}";
    }

    private async Task<bool> IsStoppedOrGoneAsync(string name, CancellationToken ct)
    {
        var status = await Manager.QueryStatusAsync(name, ct);
        return status.Outcome.Success ? status.Value!.State == ServiceState.Stopped : status.Failure == ServiceFailure.NotFound;
    }

    /// <summary>
    /// Starts <paramref name="stoppedInOrder"/>, dependencies first (the reverse of the order they were
    /// stopped in), each that exists and is not running, and waits for each in its start timeout.
    /// </summary>
    /// <returns>Each service that did not start, with the error naming it.</returns>
    public async Task<IReadOnlyList<(string Name, string Error)>> StartAsync(IReadOnlyList<string> stoppedInOrder, CancellationToken ct)
    {
        var failures = new List<(string, string)>();
        for (var i = stoppedInOrder.Count - 1; i >= 0; i--)
        {
            var name = stoppedInOrder[i];
            var status = await Manager.QueryStatusAsync(name, ct);
            if (!status.Outcome.Success && status.Failure == ServiceFailure.NotFound) continue;
            if (status.Outcome.Success && status.Value!.State == ServiceState.Running) continue;
            var result = await Manager.StartAsync(name, StartTimeout(name), ct);
            if (result.Outcome.Success)
                _log.Info($"services: started '{name}'");
            else
                failures.Add((name, $"service '{name}' could not be started: {result.Outcome.Error}"));
        }
        return failures;
    }

    /// <summary>Starts again what a failed operation stopped; every failure is only logged.</summary>
    public async Task StartAgainAsync(IReadOnlyList<string> stoppedInOrder, CancellationToken ct)
    {
        foreach (var (_, error) in await StartAsync(stoppedInOrder, ct))
            _log.Warn($"services: {error}");
    }

    /// <summary>
    /// Waits up to <paramref name="timeout"/> for each of <paramref name="names"/> to be gone from
    /// the service manager (a deleted service stays while it runs or is held open).
    /// </summary>
    /// <returns>The services still there.</returns>
    public async Task<IReadOnlyList<string>> WaitUntilGoneAsync(IReadOnlyList<string> names, TimeSpan timeout, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var remaining = names.ToList();
        while (true)
        {
            for (var i = remaining.Count - 1; i >= 0; i--)
            {
                var status = await Manager.QueryStatusAsync(remaining[i], ct);
                if (!status.Outcome.Success && status.Failure == ServiceFailure.NotFound) remaining.RemoveAt(i);
            }
            var left = timeout - clock.Elapsed;
            if (remaining.Count == 0 || left <= TimeSpan.Zero) return remaining;
            var wait = TimeSpan.FromMilliseconds(250);
            await Task.Delay(wait < left ? wait : left, ct);
        }
    }

    /// <summary>The outcome of <see cref="StopAllAsync"/>.</summary>
    /// <param name="Stopped">What was stopped, in order (dependents first).</param>
    /// <param name="Error">Why a service would not stop, naming it; null when every one stopped.</param>
    internal sealed record StopResult(IReadOnlyList<string> Stopped, string? Error);
}
