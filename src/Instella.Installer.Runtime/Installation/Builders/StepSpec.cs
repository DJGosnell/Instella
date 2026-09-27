using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Installation;

namespace Instella.Installer.Runtime.Installation.Builders;

/// <summary>
/// Frozen result of <see cref="StepBuilder.Build"/>. Implements
/// <see cref="IInstallStepExecution"/> so the runtime executor treats user
/// steps and built-ins uniformly. The user never instantiates this directly
/// — <c>InstallerBuilder</c> collects builders, calls
/// <c>Build()</c>, and assembles the resulting specs in topological order
/// before handing them to <see cref="StepExecutor"/>.
/// </summary>
internal sealed class StepSpec : IInstallStepExecution
{
    private readonly StepExecuteAsync _execute;
    private readonly StepRollbackAsync? _rollback;
    private readonly RollbackMode _mode;
    private readonly string? _noRollbackReason;
    private readonly Func<InstallContext, bool>? _whenPredicate;

    public StepSpec(
        string name,
        InstallStage stage,
        int weight,
        StepExecuteAsync execute,
        StepRollbackAsync? rollback,
        RollbackMode mode,
        string? noRollbackReason,
        string? ponrReason,
        Func<InstallContext, bool>? whenPredicate,
        IReadOnlyList<StepOrderingHint> orderingHints)
    {
        Name = name;
        Stage = stage;
        Weight = weight;
        _execute = execute;
        _rollback = rollback;
        _mode = mode;
        _noRollbackReason = noRollbackReason;
        PointOfNoReturnReason = ponrReason;
        _whenPredicate = whenPredicate;
        OrderingHints = orderingHints;
    }

    public string Name { get; }
    public InstallStage Stage { get; }
    public int Weight { get; }

    public bool IsPointOfNoReturn => PointOfNoReturnReason is not null;
    public string? PointOfNoReturnReason { get; }

    /// <summary>Set with <c>StepBuilder.WithDisplayName</c>; null shows <see cref="Name"/>.</summary>
    internal string? DisplayName { get; init; }

    /// <summary>Uninstall-time counterpart set with <c>StepBuilder.OnUninstall</c>; null when none.</summary>
    internal Func<InstallContext, CancellationToken, Task>? OnUninstall { get; init; }

    /// <summary>Slotting hints surfaced to the builder's ordering. Empty when no hint was given.</summary>
    internal IReadOnlyList<StepOrderingHint> OrderingHints { get; }

    /// <summary>Rollback mode chosen by the builder. Inspected by tests; runtime cares only about <see cref="RollbackAsync"/> behavior.</summary>
    internal RollbackMode RollbackMode => _mode;

    public bool ShouldRun(InstallContext context)
        => _whenPredicate is null || _whenPredicate(context);

    public Task<StepResult> ExecuteAsync(InstallContext context, IStepProgress progress, CancellationToken cancellationToken)
        => _execute(context, progress, cancellationToken);

    public Task RollbackAsync(InstallContext context, CancellationToken cancellationToken)
    {
        switch (_mode)
        {
            case RollbackMode.Explicit:
                return _rollback!(context, cancellationToken);

            case RollbackMode.Tracked:
                // Tracked-mode rollback is a no-op at the per-step level: the
                // ledger entries this step recorded will be reversed by the
                // executor's call to TrackingLedger.UnwindAsync after every
                // RollbackAsync has been called.
                return Task.CompletedTask;

            case RollbackMode.None:
                if (_noRollbackReason is not null)
                    context.Log.Info($"step '{Name}': skipped rollback by design ({_noRollbackReason})");
                return Task.CompletedTask;

            default:
                return Task.CompletedTask;
        }
    }
}
