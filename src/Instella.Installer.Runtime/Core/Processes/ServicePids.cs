using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Installation;
using Instella.Core.Platform;

namespace Instella.Installer.Runtime.Core.Processes;

/// <summary>
/// The process ids of an installation's running services, which the running-app gate ignores
/// (<see cref="RunningAppGate.IgnoredProcessIds"/>): they are stopped through the service manager,
/// never closed or ended like a program.
/// </summary>
internal static class ServicePids
{
    /// <summary>
    /// The non-zero process id of each service <paramref name="manifest"/> records. Without recorded
    /// services the service manager is never asked (nor created: null uses the OS's own).
    /// </summary>
    public static async Task<IReadOnlySet<int>> ForAsync(InstalledManifest? manifest, IServiceManager? manager, CancellationToken ct)
    {
        var pids = new HashSet<int>();
        if (manifest?.Services is not { Count: > 0 } services) return pids;
        manager ??= ServiceManagerFactory.Create();
        foreach (var service in services)
        {
            var status = await manager.QueryStatusAsync(service.Name, ct);
            if (status.Outcome.Success && status.Value is { ProcessId: > 0 } s)
                pids.Add(s.ProcessId);
        }
        return pids;
    }
}
