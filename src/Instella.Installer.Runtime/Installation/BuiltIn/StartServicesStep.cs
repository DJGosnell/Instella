using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Installation;
using Instella.Installer.Runtime.Builders;

namespace Instella.Installer.Runtime.Installation.BuiltIn;

/// <summary>
/// Starts the services right after <c>app-upgrade</c>, dependencies first: each one that starts
/// after install (<see cref="ServiceBuilder.StartAfterInstall"/>), and each one
/// <c>stop-services</c> stopped unless it is now disabled. Each start waits for Running up to its
/// <see cref="ServiceBuilder.StartTimeout"/>. A failed start fails the install (exit 16) unless the
/// service says <see cref="ServiceBuilder.StartFailureIsWarning"/>, which makes it a warning.
/// </summary>
/// <remarks>Rollback stops the services this step started.</remarks>
internal sealed class StartServicesStep(ServiceRun run) : IInstallStepExecution
{
    private readonly List<string> _started = [];

    public string Name => ServiceStepNames.StartServices;
    public InstallStage Stage => InstallStage.Finalize;
    public int Weight => 1;

    public async Task<StepResult> ExecuteAsync(InstallContext context, IStepProgress progress, CancellationToken cancellationToken)
    {
        var wasStopped = new HashSet<string>(run.Stopped, StringComparer.OrdinalIgnoreCase);
        var toStart = run.InStartOrder()
            .Where(s => s.StartsAfterInstall || (wasStopped.Contains(s.Name) && s.StartMode != ServiceStartMode.Disabled))
            .ToList();
        if (toStart.Count == 0)
        {
            progress.Report(1.0);
            return StepResult.Ok;
        }

        var scm = ServiceRun.Manager(context);
        var warnings = new List<string>();
        for (var i = 0; i < toStart.Count; i++)
        {
            var spec = toStart[i];
            var result = await scm.StartAsync(spec.Name, spec.StartTimeout, cancellationToken);
            if (result.Outcome.Success)
            {
                _started.Add(spec.Name);
                context.Log.Info($"services: started '{spec.Name}'");
            }
            else
            {
                var message = $"service '{spec.Name}' could not be started: {result.Outcome.Error}";
                if (!spec.StartFailureIsWarning)
                {
                    // The executor rolls back only the steps that completed: stop what this one started.
                    try { await RollbackAsync(context, CancellationToken.None); }
                    catch (Exception ex) { context.Log.Warn($"{Name}: {ex.Message}"); }
                    return StepResult.Fail(message);
                }
                context.Log.Warn($"{message}; the install goes on (StartFailureIsWarning)");
                warnings.Add(message);
            }
            progress.Report((i + 1) / (double)toStart.Count);
        }

        return warnings.Count > 0 ? StepResult.OkWithWarnings(warnings) : StepResult.Ok;
    }

    public async Task RollbackAsync(InstallContext context, CancellationToken cancellationToken)
    {
        var scm = ServiceRun.Manager(context);
        var problems = new List<string>();
        for (var i = _started.Count - 1; i >= 0; i--)
        {
            var name = _started[i];
            var result = await scm.StopAsync(name, run.Find(name)?.StopTimeout ?? ServiceSpec.DefaultStopTimeout, cancellationToken);
            if (result.Outcome.Success)
                context.Log.Info($"services: stopped '{name}' again");
            else
                problems.Add($"could not stop service '{name}': {result.Outcome.Error}");
        }
        _started.Clear();
        ServiceRun.ThrowIfProblems(context, Name, problems);
    }
}
