using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Installation;

namespace Instella.Installer.Runtime.Installation;

/// <summary>
/// Runs an ordered list of <see cref="IInstallStepExecution"/> against an
/// <see cref="InstallContext"/>. On any step failure, reverses completed
/// steps in LIFO order (step <see cref="IInstallStepExecution.RollbackAsync"/>
/// first, then the tracking ledger's generic unwind), collecting
/// rollback-of-rollback failures as warnings rather than propagating.
/// </summary>
/// <remarks>
/// Honors <see cref="IInstallStepExecution.ShouldRun"/>
/// (skipped steps don't enter the rollback list) and
/// <see cref="IInstallStepExecution.IsPointOfNoReturn"/> (after a PONR step
/// succeeds, only post-PONR steps and post-PONR ledger entries are unwound on
/// later failure). Pushes a <c>step:{Name}</c> log scope around every step's
/// execute so structured log lines carry the originating step name.
/// </remarks>
internal sealed class StepExecutor
{
    private readonly IReadOnlyList<IInstallStepExecution> _steps;

    public StepExecutor(IReadOnlyList<IInstallStepExecution> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        _steps = steps;
    }

    public async Task<ExecutionResult> ExecuteAsync(
        InstallContext context,
        IProgress<OverallProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var audit = new List<StepExecutionRecord>(_steps.Count);
        var warnings = new List<string>();
        var completed = new List<IInstallStepExecution>();
        // -1 means PONR has not been reached yet. When a PONR step succeeds
        // these snapshot the rollback boundary: the PONR step is at
        // completed[ponrCompletedIndex], and ledger entries before
        // ponrLedgerIndex pre-date the PONR.
        var ponrCompletedIndex = -1;
        var ponrLedgerIndex = -1;
        var totalWeight = 0;
        foreach (var s in _steps) totalWeight += Math.Max(1, s.Weight);
        if (totalWeight == 0) totalWeight = 1;

        var weightRun = 0;

        for (var i = 0; i < _steps.Count; i++)
        {
            var step = _steps[i];
            var bestEffort = step is Migrations.IBestEffortStep { IsBestEffort: true };

            if (cancellationToken.IsCancellationRequested && bestEffort)
            {
                // After the commit: cancelling skips what is left, it never undoes the install.
                context.Log.Info($"step[{step.Stage}] {step.Name}: skipped (cancelled; the install is complete)");
                audit.Add(new StepExecutionRecord(step.Name, step.Stage, StepOutcome.Skipped, Error: "cancelled"));
                continue;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                audit.Add(new StepExecutionRecord(step.Name, step.Stage, StepOutcome.Skipped, Error: "cancelled"));
                await RollbackWithOwnTokenAsync(context, completed, warnings, ponrCompletedIndex, ponrLedgerIndex);
                return ExecutionResult.Fail("Installation cancelled", audit, warnings);
            }

            if (!step.ShouldRun(context))
            {
                context.Log.Info($"step[{step.Stage}] {step.Name}: skipped (When predicate false)");
                audit.Add(new StepExecutionRecord(step.Name, step.Stage, StepOutcome.Skipped));
                weightRun += Math.Max(1, step.Weight);
                progress?.Report(new OverallProgress(
                    Fraction: Math.Clamp((double)weightRun / totalWeight, 0.0, 1.0),
                    Stage: step.Stage,
                    StepName: step.Name,
                    Status: "skipped"));
                continue;
            }

            var stepProgress = new StepProgress(step, totalWeight, weightRun, progress);
            // Name the running step now: a step that reports nothing until it is done (a
            // commit of many files) would otherwise sit under the previous step's name.
            stepProgress.Report(0.0);
            StepResult result;

            using (context.Log.Scope($"step:{step.Name}"))
            {
                try
                {
                    context.Log.Info($"step[{step.Stage}] {step.Name}: start");
                    context.Ledger.RecordingUserStep = step is Builders.StepSpec;
                    try
                    {
                        result = await step.ExecuteAsync(context, stepProgress, cancellationToken);
                    }
                    finally
                    {
                        context.Ledger.RecordingUserStep = false;
                    }
                }
                catch (OperationCanceledException) when (bestEffort)
                {
                    audit.Add(new StepExecutionRecord(step.Name, step.Stage, StepOutcome.Skipped, Error: "cancelled"));
                    continue;
                }
                catch (OperationCanceledException)
                {
                    audit.Add(new StepExecutionRecord(step.Name, step.Stage, StepOutcome.Skipped, Error: "cancelled"));
                    await RollbackWithOwnTokenAsync(context, completed, warnings, ponrCompletedIndex, ponrLedgerIndex);
                    return ExecutionResult.Fail("Installation cancelled", audit, warnings);
                }
                catch (Exception ex) when (bestEffort)
                {
                    context.Log.Warn($"step[{step.Stage}] {step.Name}: threw {ex.GetType().Name}: {ex.Message}; the install is kept");
                    audit.Add(new StepExecutionRecord(step.Name, step.Stage, StepOutcome.SucceededWithWarnings, Warnings: [ex.Message]));
                    continue;
                }
                catch (Exception ex)
                {
                    context.Log.Error($"step[{step.Stage}] {step.Name}: threw {ex.GetType().Name}", ex);
                    audit.Add(new StepExecutionRecord(step.Name, step.Stage, StepOutcome.Failed, Error: ex.Message));
                    await RollbackWithOwnTokenAsync(context, completed, warnings, ponrCompletedIndex, ponrLedgerIndex);
                    return ExecutionResult.Fail(ex.Message, audit, warnings);
                }
            }

            if (!result.Success && bestEffort)
            {
                context.Log.Warn($"step[{step.Stage}] {step.Name}: failed ({result.Error}); the install is kept");
                audit.Add(new StepExecutionRecord(step.Name, step.Stage, StepOutcome.SucceededWithWarnings, Warnings: [result.Error ?? "failed"]));
                continue;
            }

            if (!result.Success)
            {
                context.Log.Error($"step[{step.Stage}] {step.Name}: failed ({result.Error})");
                audit.Add(new StepExecutionRecord(step.Name, step.Stage, StepOutcome.Failed, result.Error, result.Warnings));
                await RollbackWithOwnTokenAsync(context, completed, warnings, ponrCompletedIndex, ponrLedgerIndex);
                return ExecutionResult.Fail(result.Error ?? "step failed", audit, warnings);
            }

            var outcome = result.Warnings is { Count: > 0 } ? StepOutcome.SucceededWithWarnings : StepOutcome.Succeeded;
            audit.Add(new StepExecutionRecord(step.Name, step.Stage, outcome, Warnings: result.Warnings));
            completed.Add(step);

            if (step.IsPointOfNoReturn && ponrCompletedIndex < 0)
            {
                ponrCompletedIndex = completed.Count - 1;
                ponrLedgerIndex = context.Ledger.Count;
                context.Log.Info($"step[{step.Stage}] {step.Name}: point-of-no-return reached ({step.PointOfNoReturnReason ?? "no reason given"})");
            }

            weightRun += Math.Max(1, step.Weight);
            progress?.Report(new OverallProgress(
                Fraction: Math.Clamp((double)weightRun / totalWeight, 0.0, 1.0),
                Stage: step.Stage,
                StepName: step.Name,
                Status: null));
        }

        // Every step succeeded: the committed transaction is final, so drop its staging and
        // backup folder. Until this point a failure could still roll the commit back.
        if (context.Transaction is { } txn)
        {
            // Custom Finalize steps and AfterCommit migrations run after the commit; record what
            // they tracked, completed and adopted. The install is complete either way.
            try
            {
                await BuiltIn.WriteManifestStep.AmendManifestAsync(context, CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                context.Log.Warn($"could not update the installed manifest after the commit: {ex.Message}");
            }
            await txn.CompleteAsync();
        }

        await RunCompletionActionsAsync(context);
        LogMigrationSummary(context);

        return ExecutionResult.Ok(audit, warnings);
    }

    /// <summary>
    /// Runs the clean-ups registered for when the work before them is final (migration undo
    /// copies): after a successful run, and after a failure past a point of no return, where
    /// rollback stops at that step and everything before it stays.
    /// </summary>
    private static async Task RunCompletionActionsAsync(InstallContext context)
    {
        foreach (var completion in context.CompletionActions)
        {
            try
            {
                await completion();
            }
            catch (Exception ex)
            {
                context.Log.Warn($"clean-up after the install: {ex.Message}");
            }
        }
        context.CompletionActions.Clear();
    }

    /// <summary>
    /// Rollback never inherits the caller's token: cancelling means "stop moving forward",
    /// never "leave the system half-changed" (C2). Platform calls wrap their work in
    /// <c>Task.Run(work, ct)</c>, which would not even start on a cancelled token. A generous
    /// timeout guards against a hung platform call.
    /// </summary>
    private static async Task RollbackWithOwnTokenAsync(
        InstallContext context,
        List<IInstallStepExecution> completed,
        List<string> warnings,
        int ponrCompletedIndex,
        int ponrLedgerIndex)
    {
        using var rollbackCts = new CancellationTokenSource(RollbackTimeout);
        await RollbackCompletedAsync(context, completed, warnings, ponrCompletedIndex, ponrLedgerIndex, rollbackCts.Token);
        // Past a point of no return, what ran before it stays: its undo copies are no longer needed.
        if (ponrCompletedIndex >= 0)
            await RunCompletionActionsAsync(context);
    }

    /// <summary>One line naming what each migration did, e.g. <c>migrations: 1 completed (a), 1 skipped (b)</c>.</summary>
    private static void LogMigrationSummary(InstallContext context)
    {
        var records = context.Migrations.Records;
        if (records.Count == 0) return;
        var parts = records
            .GroupBy(r => r.Outcome)
            .Select(g => $"{g.Count()} {g.Key.ToString().ToLowerInvariant()} ({string.Join(", ", g.Select(r => r.Id))})");
        context.Log.Info($"migrations: {string.Join(", ", parts)}");
    }

    /// <summary>Upper bound on a whole rollback.</summary>
    internal static readonly TimeSpan RollbackTimeout = TimeSpan.FromMinutes(5);

    private static async Task RollbackCompletedAsync(
        InstallContext context,
        List<IInstallStepExecution> completed,
        List<string> warnings,
        int ponrCompletedIndex,
        int ponrLedgerIndex,
        CancellationToken cancellationToken)
    {
        // Steps strictly after the PONR roll back; the PONR step itself and
        // anything before it stay in place. When ponrCompletedIndex == -1 the
        // expression collapses to "all completed": everything that ran is rolled back.
        var rollbackBoundary = ponrCompletedIndex; // exclusive lower bound

        for (var i = completed.Count - 1; i > rollbackBoundary; i--)
        {
            var step = completed[i];
            try
            {
                context.Log.Info($"rollback: {step.Name}");
                await step.RollbackAsync(context, cancellationToken);
            }
            catch (Exception ex)
            {
                warnings.Add($"rollback step '{step.Name}' threw: {ex.Message}");
            }
        }

        // Ledger entries written before the PONR survive; entries after are unwound.
        var ledgerStart = ponrLedgerIndex < 0 ? 0 : ponrLedgerIndex;
        await context.Ledger.UnwindAsync(context, warnings, ledgerStart, cancellationToken);
    }

    private sealed class StepProgress : IStepProgress
    {
        private readonly IInstallStepExecution _step;
        private readonly int _totalWeight;
        private readonly int _runBefore;
        private readonly IProgress<OverallProgress>? _sink;

        public StepProgress(IInstallStepExecution step, int totalWeight, int runBefore, IProgress<OverallProgress>? sink)
        {
            _step = step;
            _totalWeight = totalWeight;
            _runBefore = runBefore;
            _sink = sink;
        }

        public void Report(double fraction, string? status = null)
        {
            var clamped = Math.Clamp(fraction, 0.0, 1.0);
            var stepWeight = Math.Max(1, _step.Weight);
            var overall = (_runBefore + stepWeight * clamped) / _totalWeight;
            _sink?.Report(new OverallProgress(
                Fraction: Math.Clamp(overall, 0.0, 1.0),
                Stage: _step.Stage,
                StepName: _step.Name,
                Status: status));
        }
    }
}
