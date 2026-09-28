using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Installation;
using Instella.Installer.Runtime.Installation;

namespace Instella.Installer.Runtime.Migrations;

/// <summary>
/// A step the executor must never let fail the install or roll it back: it runs after the install
/// is complete, is skipped (not rolled back) when the run is cancelled, and a throw is a warning.
/// </summary>
internal interface IBestEffortStep
{
    bool IsBestEffort { get; }
}

/// <summary>What <see cref="MigrationExecution.RunAsync"/> did with one migration.</summary>
internal sealed record MigrationExecutionResult(MigrationRun Run, MigrationRunOutcome Outcome, string? Reason);

/// <summary>Runs one migration: run-once check, condition, body; records the outcome.</summary>
internal static class MigrationExecution
{
    /// <summary>
    /// Runs <paramref name="migration"/> in <paramref name="install"/>. Never throws for a migration
    /// failure: the result says <see cref="MigrationRunOutcome.Failed"/> (or, for a
    /// <see cref="MigrationTiming.BeforeCommit"/> migration whose own undo ran,
    /// <see cref="MigrationRunOutcome.RolledBack"/>).
    /// </summary>
    public static async Task<MigrationExecutionResult> RunAsync(InstallMigration migration, InstallContext install, CancellationToken ct)
    {
        var runtime = install.Migrations;
        var id = migration.Id;
        var context = new MigrationContext(install, runtime, new MigrationLogger(install.Log, id));
        var run = new MigrationRun(migration, context);
        var log = context.Log;

        MigrationExecutionResult Done(MigrationRunOutcome outcome, string? reason)
        {
            runtime.Records.Add(new MigrationRunRecord(id, outcome, reason, [.. run.Actions]));
            return new MigrationExecutionResult(run, outcome, reason);
        }

        if (migration.RunOnce && install.ExistingInstallation?.CompletedMigrations is { } completed
            && completed.Contains(id, StringComparer.Ordinal))
        {
            log.Info("skipped: already completed for this installation");
            return Done(MigrationRunOutcome.AlreadyCompleted, "already completed for this installation");
        }

        migration.Bind(run);
        try
        {
            ConditionOutcome condition;
            try
            {
                condition = await migration.GetCondition().EvaluateAsync(context, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Something the condition reads could not be read (access denied, an I/O error).
                // Nothing was changed, so this is a skip, never a failed install; not recorded.
                var reason = $"could not evaluate the condition ({ex.GetType().Name}: {ex.Message})";
                log.Warn($"skipped: {reason}; it is tried again the next time an installer runs");
                return Done(MigrationRunOutcome.Skipped, reason);
            }
            if (!condition.Value)
            {
                log.Info($"skipped: {condition.Reason}");
                return Done(MigrationRunOutcome.Skipped, condition.Reason);
            }

            log.Info($"start ({migration.DisplayName}){(runtime.IsPreview ? " in preview: nothing is changed" : "")}");
            await migration.RunBodyAsync(ct);

            var summary = run.Actions.Count == 0 ? "no changes" : string.Join("; ", run.Actions);
            log.Info($"completed: {summary}");
            if (migration.RunOnce && !runtime.IsPreview && !runtime.Completed.Contains(id, StringComparer.Ordinal))
                runtime.Completed.Add(id);
            return Done(MigrationRunOutcome.Completed, null);
        }
        catch (Exception ex)
        {
            var message = ex is OperationCanceledException ? "cancelled" : ex.Message;
            if (migration.Timing != MigrationTiming.BeforeCommit)
            {
                log.Warn($"failed: {message}; it runs again the next time an installer runs");
                return Done(MigrationRunOutcome.Failed, message);
            }

            log.Error($"failed: {message}; undoing it and rolling the install back");
            await RollbackAsync(run);
            return Done(MigrationRunOutcome.RolledBack, message);
        }
        finally
        {
            migration.Unbind();
        }
    }

    /// <summary>
    /// Undoes a <see cref="MigrationTiming.BeforeCommit"/> migration: its <c>RollbackAsync</c>,
    /// then its built-in actions (newest first), then forgets its completion and adoptions. Never
    /// throws; never inherits a cancelled token.
    /// </summary>
    public static async Task RollbackAsync(MigrationRun run)
    {
        using var cts = new CancellationTokenSource(StepExecutor.RollbackTimeout);
        var migration = run.Migration;
        migration.Bind(run);
        try
        {
            await migration.RunRollbackAsync(cts.Token);
        }
        catch (Exception ex)
        {
            run.Context.Log.Warn($"rollback: RollbackAsync threw: {ex.Message}");
        }
        finally
        {
            migration.Unbind();
        }

        await MigrationActions.UndoAsync(run, cts.Token);
        var runtime = run.Context.Runtime;
        runtime.Completed.Remove(migration.Id);
        runtime.Adopted.RemoveAll(a => a.Source == migration.Id);
    }
}

/// <summary>
/// One migration in the install pipeline, named <c>migration:&lt;id&gt;</c>.
/// <see cref="MigrationTiming.BeforeCommit"/>: a failure fails the install (after undoing
/// itself), and a later failure rolls it back. <see cref="MigrationTiming.AfterCommit"/>:
/// best effort, after every other step; a failure is a warning.
/// </summary>
internal sealed class MigrationStep : IInstallStepExecution, IBestEffortStep
{
    private MigrationRun? _completedRun;

    public MigrationStep(InstallMigration migration)
    {
        Migration = migration;
    }

    public InstallMigration Migration { get; }

    public string Name => MigrationValidation.StepPrefix + Migration.Id;

    public InstallStage Stage => InstallStage.Finalize;

    public int Weight => 1;

    /// <summary>The text the Progress page shows.</summary>
    public string DisplayName => Migration.DisplayName;

    public bool IsBestEffort => Migration.Timing == MigrationTiming.AfterCommit;

    public async Task<StepResult> ExecuteAsync(InstallContext context, IStepProgress progress, CancellationToken cancellationToken)
    {
        var result = await MigrationExecution.RunAsync(Migration, context, cancellationToken);
        progress.Report(1.0);
        switch (result.Outcome)
        {
            case MigrationRunOutcome.Completed:
                if (Migration.Timing == MigrationTiming.BeforeCommit) _completedRun = result.Run;
                return StepResult.Ok;
            case MigrationRunOutcome.Failed:
                return StepResult.OkWithWarnings([$"migration '{Migration.Id}' failed: {result.Reason}"]);
            case MigrationRunOutcome.RolledBack:
                return StepResult.Fail($"migration '{Migration.Id}' failed: {result.Reason}");
            default:
                return StepResult.Ok;
        }
    }

    /// <summary>A later step failed: undo this migration (only BeforeCommit migrations journal anything).</summary>
    public async Task RollbackAsync(InstallContext context, CancellationToken cancellationToken)
    {
        if (_completedRun is not { } run) return;
        _completedRun = null;
        run.Context.Log.Info("rolling back: a later step failed");
        await MigrationExecution.RollbackAsync(run);
        var records = context.Migrations.Records;
        var i = records.FindLastIndex(r => r.Id == Migration.Id);
        if (i >= 0) records[i] = records[i] with { Outcome = MigrationRunOutcome.RolledBack, Reason = "a later step failed" };
    }
}
