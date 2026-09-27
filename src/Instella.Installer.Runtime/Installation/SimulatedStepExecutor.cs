using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Installation;
using Instella.Installer.Runtime.Builders;

namespace Instella.Installer.Runtime.Installation;

/// <summary>
/// Preview-mode sibling of <see cref="StepExecutor"/>. Walks the same step
/// list and produces the same <see cref="ExecutionResult"/> / progress
/// stream shape but never invokes the step's real
/// <see cref="IInstallStepExecution.ExecuteAsync"/> — instead it sleeps per
/// the configured speed bucket, emits interpolated synthetic progress, and
/// marks each step <see cref="StepOutcome.Succeeded"/>.
/// </summary>
/// <remarks>
/// <para>Failure injection: when <c>--preview-fail=&lt;step-name&gt;</c> is
/// supplied (see <see cref="PreviewCliArgs"/>), the named step reports
/// <see cref="StepOutcome.Failed"/> with a canned message so reviewers can
/// exercise the Error UI without authoring a broken build. Unknown step
/// names throw <see cref="InvalidOperationException"/> at construction — the
/// check runs early so a typo in the CLI produces a clear error rather than
/// silently proceeding.</para>
/// <para>The simulator deliberately does not touch
/// <see cref="InstallContext.Platform"/>, <see cref="InstallContext.FileSystem"/>,
/// <see cref="InstallContext.Ledger"/>, or <see cref="InstallContext.PayloadArchive"/>.
/// Preview mode never needs real installs — or real no-op platform services —
/// because no step bodies run.</para>
/// </remarks>
internal sealed class SimulatedStepExecutor
{
    private const int ProgressTicks = 5;

    private readonly IReadOnlyList<IInstallStepExecution> _steps;
    private readonly PreviewSpeed _speed;
    private readonly string? _failAtStep;

    public SimulatedStepExecutor(
        IReadOnlyList<IInstallStepExecution> steps,
        PreviewSpeed speed,
        string? failAtStep)
    {
        ArgumentNullException.ThrowIfNull(steps);
        _steps = steps;
        _speed = speed;
        _failAtStep = failAtStep;

        if (!string.IsNullOrEmpty(failAtStep))
        {
            if (!steps.Any(s => string.Equals(s.Name, failAtStep, StringComparison.OrdinalIgnoreCase)))
            {
                var names = string.Join(", ", steps.Select(s => s.Name));
                throw new InvalidOperationException(
                    $"--preview-fail: unknown step '{failAtStep}'. Available steps: {names}.");
            }
        }
    }

    public async Task<ExecutionResult> ExecuteAsync(
        InstallContext context,
        IProgress<OverallProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var audit = new List<StepExecutionRecord>(_steps.Count);
        var warnings = new List<string>();
        var totalWeight = 0;
        foreach (var s in _steps) totalWeight += Math.Max(1, s.Weight);
        if (totalWeight == 0) totalWeight = 1;

        var weightRun = 0;
        var stepDelay = PreviewCliArgsParser.DelayFor(_speed);
        var tickDelay = TimeSpan.FromTicks(stepDelay.Ticks / ProgressTicks);

        for (var i = 0; i < _steps.Count; i++)
        {
            var step = _steps[i];
            if (cancellationToken.IsCancellationRequested)
            {
                audit.Add(new StepExecutionRecord(step.Name, step.Stage, StepOutcome.Skipped, Error: "cancelled"));
                return ExecutionResult.Fail("Installation cancelled", audit, warnings);
            }

            context.Log.Info($"preview: step[{step.Stage}] {step.Name}: simulating");

            var stepWeight = Math.Max(1, step.Weight);
            for (var tick = 1; tick <= ProgressTicks; tick++)
            {
                try
                {
                    await Task.Delay(tickDelay, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    audit.Add(new StepExecutionRecord(step.Name, step.Stage, StepOutcome.Skipped, Error: "cancelled"));
                    return ExecutionResult.Fail("Installation cancelled", audit, warnings);
                }

                var tickFraction = (double)tick / ProgressTicks;
                var overall = (weightRun + stepWeight * tickFraction) / totalWeight;
                progress?.Report(new OverallProgress(
                    Fraction: Math.Clamp(overall, 0.0, 1.0),
                    Stage: step.Stage,
                    StepName: step.Name,
                    Status: null));
            }

            var isInjectedFailure = _failAtStep is not null
                && string.Equals(step.Name, _failAtStep, StringComparison.OrdinalIgnoreCase);

            if (isInjectedFailure)
            {
                var error = $"preview: injected failure at step '{step.Name}' (--preview-fail)";
                context.Log.Error($"preview: step[{step.Stage}] {step.Name}: {error}");
                audit.Add(new StepExecutionRecord(step.Name, step.Stage, StepOutcome.Failed, Error: error));
                return ExecutionResult.Fail(error, audit, warnings);
            }

            audit.Add(new StepExecutionRecord(step.Name, step.Stage, StepOutcome.Succeeded));
            weightRun += stepWeight;
            progress?.Report(new OverallProgress(
                Fraction: Math.Clamp((double)weightRun / totalWeight, 0.0, 1.0),
                Stage: step.Stage,
                StepName: step.Name,
                Status: null));
        }

        return ExecutionResult.Ok(audit, warnings);
    }
}
