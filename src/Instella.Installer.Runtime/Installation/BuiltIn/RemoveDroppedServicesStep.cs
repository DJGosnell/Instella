using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Installation;
using Instella.Core.Platform;
using Instella.Installer.Runtime.Builders;

namespace Instella.Installer.Runtime.Installation.BuiltIn;

/// <summary>
/// On an upgrade or repair, stops and deletes the services the previous version recorded and
/// this installer no longer declares, as <see cref="RemoveDroppedIntegrationsStep"/> does for
/// shortcuts and associations. The new manifest no longer lists them.
/// </summary>
/// <remarks>
/// Rollback re-creates each from the configuration read before deleting it (or, when that could
/// not be read, from what the manifest recorded) and starts it again if it was running.
/// </remarks>
internal sealed class RemoveDroppedServicesStep(ServiceRun run) : IInstallStepExecution
{
    private readonly List<(ServiceConfiguration Configuration, bool WasRunning)> _removed = [];

    public string Name => ServiceStepNames.RemoveDroppedServices;
    public InstallStage Stage => InstallStage.Register;
    public int Weight => 1;

    public async Task<StepResult> ExecuteAsync(InstallContext context, IStepProgress progress, CancellationToken cancellationToken)
    {
        var dropped = (context.ExistingInstallation?.Services ?? [])
            .Where(s => run.Find(s.Name) is null)
            .Reverse()
            .ToList();
        if (dropped.Count == 0)
        {
            progress.Report(1.0);
            return StepResult.Ok;
        }

        var scm = ServiceRun.Manager(context);
        foreach (var recorded in dropped)
        {
            var query = await scm.QueryConfigurationAsync(recorded.Name, cancellationToken);
            if (!query.Outcome.Success && query.Failure == ServiceFailure.NotFound)
            {
                context.Log.Info($"services: '{recorded.Name}' is no longer declared and is already gone");
                continue;
            }
            var configuration = query.Outcome.Success
                ? ServiceRun.FromSnapshot(query.Value!)
                : ServiceRun.FromManifest(recorded, context);
            var (_, stopped) = await ServiceRun.StateAsync(scm, recorded.Name, cancellationToken);

            string? error = null;
            var stop = await scm.StopAsync(recorded.Name, ServiceSpec.DefaultStopTimeout, cancellationToken);
            if (!stop.Outcome.Success)
                error = $"service '{recorded.Name}', which this version no longer has, could not be stopped: {stop.Outcome.Error}";
            else
            {
                var deleted = await scm.DeleteAsync(recorded.Name, cancellationToken);
                if (deleted.Outcome.Success)
                {
                    _removed.Add((configuration, !stopped));
                    context.Log.Info($"services: deleted '{recorded.Name}', which this version no longer has");
                }
                else
                {
                    error = $"service '{recorded.Name}', which this version no longer has, could not be deleted: {deleted.Outcome.Error}";
                    if (!stopped) await scm.StartAsync(recorded.Name, ServiceSpec.DefaultStartTimeout, CancellationToken.None);
                }
            }

            if (error is not null)
            {
                try { await RollbackAsync(context, CancellationToken.None); }
                catch (Exception ex) { context.Log.Warn($"{Name}: {ex.Message}"); }
                return StepResult.Fail(error);
            }
        }

        progress.Report(1.0);
        return StepResult.Ok;
    }

    public async Task RollbackAsync(InstallContext context, CancellationToken cancellationToken)
    {
        var scm = ServiceRun.Manager(context);
        var problems = new List<string>();
        for (var i = _removed.Count - 1; i >= 0; i--)
        {
            var (configuration, wasRunning) = _removed[i];
            var created = await scm.CreateAsync(configuration, null, cancellationToken);
            if (!created.Outcome.Success)
            {
                problems.Add($"could not re-create service '{configuration.Name}': {created.Outcome.Error}");
                continue;
            }
            context.Log.Info($"services: re-created '{configuration.Name}'");
            if (!wasRunning) continue;
            var started = await scm.StartAsync(configuration.Name, ServiceSpec.DefaultStartTimeout, cancellationToken);
            if (!started.Outcome.Success)
                problems.Add($"could not start service '{configuration.Name}' again: {started.Outcome.Error}");
        }
        _removed.Clear();
        ServiceRun.ThrowIfProblems(context, Name, problems);
    }
}
