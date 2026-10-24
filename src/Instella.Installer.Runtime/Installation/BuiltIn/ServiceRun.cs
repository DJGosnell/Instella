using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.FileSystem;
using Instella.Core.Installation;
using Instella.Core.Platform;
using Instella.Installer.Runtime.Builders;

namespace Instella.Installer.Runtime.Installation.BuiltIn;

/// <summary>
/// What the four service steps of one run share: the declared services, and what each step did
/// so the others' rollbacks can undo it in the right order. <c>register-services</c> snapshots
/// the configuration of every service it reconfigures; <c>stop-services</c> remembers what it
/// stopped. Whichever rollback runs first restores a snapshot or restarts a service, and marks
/// it, so the old version never starts on the new configuration and nothing is done twice.
/// </summary>
internal sealed class ServiceRun(IReadOnlyList<ServiceSpec> declared)
{
    /// <summary>The services this installer declares, in declaration order.</summary>
    public IReadOnlyList<ServiceSpec> Declared { get; } = declared;

    /// <summary>The configuration a recorded service had before <c>register-services</c> changed it.</summary>
    public Dictionary<string, ServiceConfigurationInfo> Snapshots { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Snapshots a rollback has written back already.</summary>
    public HashSet<string> Restored { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The services <c>stop-services</c> stopped, in the order it stopped them (dependents first),
    /// including running dependents the service manager stopped along with them, such as another
    /// product's service that depends on one of ours.
    /// </summary>
    public List<string> Stopped { get; } = [];

    /// <summary>Stopped services a rollback has started again.</summary>
    public HashSet<string> Restarted { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The declared service named <paramref name="name"/>, or null.</summary>
    public ServiceSpec? Find(string name) =>
        Declared.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The service manager the runner gave the context.</summary>
    public static IServiceManager Manager(InstallContext context) =>
        context.ServiceManager ?? throw new InvalidOperationException("no service manager was given to the install");

    /// <summary>The service <paramref name="name"/> as the existing installation records it, or null.</summary>
    public static ManifestService? Recorded(InstallContext context, string name) =>
        context.ExistingInstallation?.Services?.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The service's executable relative to the install directory, with <c>/</c> separators: its
    /// own, or the app's main executable (resolved into the manifest at publish time).
    /// </summary>
    public static string RelativeExecutable(ServiceSpec spec, InstallContext context)
    {
        var main = string.IsNullOrWhiteSpace(context.Manifest.ExecutableName)
            ? $"{context.Manifest.AppName}.exe"
            : ExecutableResolver.WithPlatformExtension(context.Manifest.ExecutableName!);
        var rel = spec.ExecutableOr(main)!;
        return SafePath.TryNormalizeRelative(rel, out var normalized, out _) ? normalized : rel.Replace('\\', '/');
    }

    /// <summary>What <c>register-services</c> writes for <paramref name="spec"/>.</summary>
    public static ServiceConfiguration ConfigurationFor(ServiceSpec spec, InstallContext context) => new()
    {
        Name = spec.Name,
        DisplayName = spec.DisplayName,
        Description = spec.Description,
        ExecutablePath = SafePath.Combine(context.InstallPath, RelativeExecutable(spec, context)),
        Arguments = spec.Arguments,
        StartType = spec.StartType,
        AccountName = spec.AccountName,
        Dependencies = spec.Dependencies,
        FailureActions = spec.FailureActions,
        SidType = spec.SidType,
    };

    /// <summary>A snapshot read back from the service manager, as a configuration to write again.</summary>
    public static ServiceConfiguration FromSnapshot(ServiceConfigurationInfo info)
    {
        var (exe, args) = ServiceBinaryPath.Parse(info.BinaryPath);
        return new ServiceConfiguration
        {
            Name = info.Name,
            DisplayName = info.DisplayName,
            Description = info.Description,
            ExecutablePath = exe,
            Arguments = args,
            StartType = info.StartType,
            AccountName = IsLocalSystem(info.AccountName) ? null : info.AccountName,
            Dependencies = info.Dependencies,
            FailureActions = info.FailureActions,
            SidType = info.SidType,
        };
    }

    /// <summary>A service the manifest records, as a configuration, when no snapshot could be taken.</summary>
    public static ServiceConfiguration FromManifest(ManifestService recorded, InstallContext context) => new()
    {
        Name = recorded.Name,
        DisplayName = recorded.Name,
        ExecutablePath = Path.Combine(context.InstallPath, recorded.ExecutablePath.Replace('/', Path.DirectorySeparatorChar)),
        StartType = recorded.StartType switch
        {
            ManifestService.AutomaticDelayed => ServiceStartType.AutomaticDelayed,
            ManifestService.Manual => ServiceStartType.Manual,
            ManifestService.Disabled => ServiceStartType.Disabled,
            _ => ServiceStartType.Automatic,
        },
        AccountName = recorded.AccountName,
    };

    private static bool IsLocalSystem(string? account) =>
        account is null
        || string.Equals(account, "LocalSystem", StringComparison.OrdinalIgnoreCase)
        || string.Equals(account, @".\LocalSystem", StringComparison.OrdinalIgnoreCase)
        || string.Equals(account, @"NT AUTHORITY\SYSTEM", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The declared services, each after the declared services it depends on (the order to start
    /// them in; reversed, the order to stop them in). Build() refused cycles; ties keep declaration order.
    /// </summary>
    public IReadOnlyList<ServiceSpec> InStartOrder()
    {
        var ordered = new List<ServiceSpec>(Declared.Count);
        var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Place(ServiceSpec spec, int depth)
        {
            if (placed.Contains(spec.Name) || depth > Declared.Count) return;
            foreach (var dependency in spec.Dependencies)
                if (Find(dependency) is { } declaredDependency) Place(declaredDependency, depth + 1);
            if (placed.Add(spec.Name)) ordered.Add(spec);
        }
        foreach (var spec in Declared) Place(spec, 0);
        return ordered;
    }

    /// <summary>Whether the service is stopped (or gone).</summary>
    public static async Task<(bool Exists, bool Stopped)> StateAsync(IServiceManager scm, string name, CancellationToken ct)
    {
        var status = await scm.QueryStatusAsync(name, ct);
        if (!status.Outcome.Success) return (status.Failure != ServiceFailure.NotFound, true);
        return (true, status.Value!.State == ServiceState.Stopped);
    }

    /// <summary>
    /// Writes back the snapshot of every reconfigured service not restored yet. Failures are
    /// collected in <paramref name="problems"/>.
    /// </summary>
    public async Task RestoreSnapshotsAsync(InstallContext context, List<string> problems, CancellationToken ct)
    {
        var scm = Manager(context);
        foreach (var (name, snapshot) in Snapshots)
        {
            if (!Restored.Add(name)) continue;
            var result = await scm.ChangeConfigurationAsync(FromSnapshot(snapshot), null, ct);
            if (result.Outcome.Success)
                context.Log.Info($"services: restored the previous configuration of '{name}'");
            else
                problems.Add($"could not restore the previous configuration of service '{name}': {result.Outcome.Error}");
        }
    }

    /// <summary>
    /// Starts again, dependencies first, every service <c>stop-services</c> stopped and no rollback
    /// has restarted yet.
    /// </summary>
    public async Task RestartStoppedAsync(InstallContext context, List<string> problems, CancellationToken ct)
    {
        var scm = Manager(context);
        for (var i = Stopped.Count - 1; i >= 0; i--)
        {
            var name = Stopped[i];
            if (!Restarted.Add(name)) continue;
            var timeout = Find(name)?.StartTimeout ?? ServiceSpec.DefaultStartTimeout;
            var result = await scm.StartAsync(name, timeout, ct);
            if (result.Outcome.Success)
                context.Log.Info($"services: started '{name}' again");
            else
                problems.Add($"could not start service '{name}' again: {result.Outcome.Error}");
        }
    }

    /// <summary>
    /// Ends a rollback: every problem is logged, and the rollback throws so the executor records
    /// it as a rollback warning (exit 13 unless a more specific failure wins).
    /// </summary>
    public static void ThrowIfProblems(InstallContext context, string step, List<string> problems)
    {
        if (problems.Count == 0) return;
        foreach (var problem in problems) context.Log.Warn($"{step}: {problem}");
        throw new InvalidOperationException(string.Join("; ", problems));
    }
}
