using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Installation;
using Instella.Core.Platform;

namespace Instella.Installer.Runtime.Installation.BuiltIn;

/// <summary>
/// Creates each declared Windows service, or reconfigures the one this installation already
/// records, dependencies first, and records them for the installed manifest. A running service
/// keeps running the old files until <c>stop-services</c>: a configuration change only takes
/// effect at its next start.
/// </summary>
/// <remarks>
/// Rollback deletes what it created (stopping it first) and writes back the configuration it
/// snapshotted before each change, unless <c>stop-services</c>' rollback already did, then starts
/// whatever <c>stop-services</c> stopped and nothing has restarted yet.
/// </remarks>
internal sealed class RegisterServicesStep(ServiceRun run) : IInstallStepExecution
{
    private readonly List<string> _created = [];

    public string Name => ServiceStepNames.RegisterServices;
    public InstallStage Stage => InstallStage.Register;
    public int Weight => 1;

    public async Task<StepResult> ExecuteAsync(InstallContext context, IStepProgress progress, CancellationToken cancellationToken)
    {
        var recorded = new List<ManifestService>();
        context.ManifestServices = recorded;
        if (run.Declared.Count == 0)
        {
            progress.Report(1.0);
            return StepResult.Ok;
        }

        // The runner refused a missing executable before anything changed; this catches a
        // payload it could not list.
        var extracted = context.ExtractedFiles.Select(f => f.RelativePath.Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var spec in run.Declared)
        {
            var rel = ServiceRun.RelativeExecutable(spec, context);
            if (extracted.Count > 0 && !extracted.Contains(rel))
                return StepResult.Fail($"service '{spec.Name}': executable '{rel}' is not in the payload");
        }

        var scm = ServiceRun.Manager(context);
        var ordered = run.InStartOrder();
        for (var i = 0; i < ordered.Count; i++)
        {
            var spec = ordered[i];
            var configuration = ServiceRun.ConfigurationFor(spec, context);
            var previous = ServiceRun.Recorded(context, spec.Name);
            var existing = await scm.QueryConfigurationAsync(spec.Name, cancellationToken);
            string? error;
            if (existing.Outcome.Success)
            {
                if (previous is null)
                    error = $"a service named '{spec.Name}' already exists and is not part of this installation";
                else
                {
                    run.Snapshots[spec.Name] = existing.Value!;
                    var changed = await scm.ChangeConfigurationAsync(configuration, null, cancellationToken);
                    error = changed.Outcome.Success ? null : $"service '{spec.Name}' could not be reconfigured: {changed.Outcome.Error}";
                    if (error is null) context.Log.Info($"services: reconfigured '{spec.Name}' ({configuration.BinaryPath})");
                }
            }
            else if (existing.Failure == ServiceFailure.NotFound)
            {
                var created = await scm.CreateAsync(configuration, null, cancellationToken);
                error = created.Outcome.Success ? null : $"service '{spec.Name}' could not be created: {created.Outcome.Error}";
                if (error is null)
                {
                    _created.Add(spec.Name);
                    context.Log.Info($"services: created '{spec.Name}' ({configuration.BinaryPath})");
                }
            }
            else
            {
                error = $"service '{spec.Name}' could not be read: {existing.Outcome.Error}";
            }

            if (error is not null)
            {
                // The executor rolls back only the steps that completed: undo this one's work here.
                await UndoAfterFailureAsync(context);
                return StepResult.Fail(error);
            }

            recorded.Add(new ManifestService(spec.Name, ServiceRun.RelativeExecutable(spec, context), spec.AccountName,
                spec.ManifestStartType, previous?.CreatedByInstella ?? true));
            progress.Report((i + 1) / (double)ordered.Count);
        }

        // The manifest lists them in declaration order.
        recorded.Sort((a, b) => IndexOf(a.Name).CompareTo(IndexOf(b.Name)));
        return StepResult.Ok;
    }

    private int IndexOf(string name)
    {
        for (var i = 0; i < run.Declared.Count; i++)
            if (string.Equals(run.Declared[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
        return int.MaxValue;
    }

    private async Task UndoAfterFailureAsync(InstallContext context)
    {
        try
        {
            await RollbackAsync(context, CancellationToken.None);
        }
        catch (Exception ex)
        {
            context.Log.Warn($"{Name}: {ex.Message}");
        }
    }

    public async Task RollbackAsync(InstallContext context, CancellationToken cancellationToken)
    {
        var scm = ServiceRun.Manager(context);
        var problems = new List<string>();
        for (var i = _created.Count - 1; i >= 0; i--)
        {
            var name = _created[i];
            var (exists, stopped) = await ServiceRun.StateAsync(scm, name, cancellationToken);
            if (!exists) continue;
            if (!stopped)
            {
                var stop = await scm.StopAsync(name, run.Find(name)?.StopTimeout ?? Runtime.Builders.ServiceSpec.DefaultStopTimeout, cancellationToken);
                if (!stop.Outcome.Success) problems.Add($"could not stop service '{name}': {stop.Outcome.Error}");
            }
            var deleted = await scm.DeleteAsync(name, cancellationToken);
            if (deleted.Outcome.Success)
                context.Log.Info($"services: deleted '{name}', which this install had created");
            else
                problems.Add($"could not delete service '{name}': {deleted.Outcome.Error}");
        }
        _created.Clear();

        await run.RestoreSnapshotsAsync(context, problems, cancellationToken);
        await run.RestartStoppedAsync(context, problems, cancellationToken);
        ServiceRun.ThrowIfProblems(context, Name, problems);
    }
}
