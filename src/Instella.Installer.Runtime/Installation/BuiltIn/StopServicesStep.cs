using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Installation;
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Runtime.Core.Services;

namespace Instella.Installer.Runtime.Installation.BuiltIn;

/// <summary>
/// On an upgrade or repair, stops the installation's running services right before
/// <c>commit-transaction</c>, dependents first, so the commit replaces files no service holds
/// and each service starts on the new files in <c>start-services</c>. A service that does not
/// stop in its <see cref="ServiceBuilder.StopTimeout"/> fails the install.
/// </summary>
/// <remarks>
/// Rollback first writes back the configuration <c>register-services</c> changed, so the old
/// version never starts on the new one, then starts what it stopped, dependencies first.
/// </remarks>
internal sealed class StopServicesStep(ServiceRun run) : IInstallStepExecution
{
    public string Name => ServiceStepNames.StopServices;
    public InstallStage Stage => InstallStage.Finalize;
    public int Weight => 1;

    public async Task<StepResult> ExecuteAsync(InstallContext context, IStepProgress progress, CancellationToken cancellationToken)
    {
        var recorded = context.ExistingInstallation?.Services ?? [];
        if (recorded.Count == 0)
        {
            progress.Report(1.0);
            return StepResult.Ok;
        }

        // Declared services in reverse start order (dependents first), then any other recorded one.
        var names = run.InStartOrder().Reverse().Select(s => s.Name)
            .Where(n => recorded.Any(r => string.Equals(r.Name, n, StringComparison.OrdinalIgnoreCase)))
            .Concat(recorded.Select(r => r.Name).Where(n => run.Find(n) is null))
            .ToList();

        var scm = ServiceRun.Manager(context);
        foreach (var name in names)
        {
            var (exists, stopped) = await ServiceRun.StateAsync(scm, name, cancellationToken);
            if (!exists || stopped) continue;
            var timeout = run.Find(name)?.StopTimeout ?? ServiceSpec.DefaultStopTimeout;

            // The SCM stops running dependents first, other products' services included: remember
            // them too, so start-services (or the rollback) starts them again.
            var dependents = await scm.QueryActiveDependentsAsync(name, cancellationToken);
            if (!dependents.Outcome.Success)
                return await FailAsync(context, $"service '{name}' could not be stopped: {dependents.Outcome.Error}");
            foreach (var dependent in dependents.Value!)
                if (!run.Stopped.Contains(dependent, StringComparer.OrdinalIgnoreCase)) run.Stopped.Add(dependent);

            var result = await scm.StopAsync(name, timeout, cancellationToken);
            if (!result.Outcome.Success)
                return await FailAsync(context, $"service '{name}' could not be stopped: {result.Outcome.Error}");
            run.Stopped.Add(name);
            context.Log.Info($"services: stopped '{name}' for the update of its files");
        }

        // So that --recover starts them again if the install is interrupted before start-services.
        if (context.Transaction is { } txn)
        {
            try { await ServicesRunningFile.WriteAsync(context.FileSystem, txn, run.Stopped, cancellationToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                context.Log.Warn($"{Name}: {ex.Message}");
            }
        }

        progress.Report(1.0);
        return StepResult.Ok;
    }

    private async Task<StepResult> FailAsync(InstallContext context, string message)
    {
        // The executor rolls back only the steps that completed: undo this one's own work.
        try { await RollbackAsync(context, CancellationToken.None); }
        catch (Exception ex) { context.Log.Warn($"{Name}: {ex.Message}"); }
        return StepResult.Fail(message);
    }

    public async Task RollbackAsync(InstallContext context, CancellationToken cancellationToken)
    {
        var problems = new List<string>();
        await run.RestoreSnapshotsAsync(context, problems, cancellationToken);
        await run.RestartStoppedAsync(context, problems, cancellationToken);
        ServiceRun.ThrowIfProblems(context, Name, problems);
    }
}
