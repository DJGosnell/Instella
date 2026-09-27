using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Installation;

namespace Instella.Installer.Runtime.Installation;

/// <summary>
/// Execute-capable install step. Extends <see cref="IInstallStep"/> (metadata
/// only, defined in <c>Instella.Core</c>) with the runtime-side execution and
/// rollback entry points. Lives in the Runtime because <see cref="InstallContext"/>
/// pulls in platform services, filesystem, and the logger — all of which are
/// Runtime concerns that <c>Instella.Core</c> deliberately does not take a
/// dependency on.
/// </summary>
public interface IInstallStepExecution : IInstallStep
{
    /// <summary>
    /// Run the step. Implementations must cooperate with <paramref name="cancellationToken"/>
    /// and avoid throwing for recoverable failures — return a failing
    /// <see cref="StepResult"/> instead so the executor can roll back
    /// predecessors. Uncaught exceptions are caught by the executor and
    /// treated as step failures.
    /// </summary>
    Task<StepResult> ExecuteAsync(InstallContext context, IStepProgress progress, CancellationToken cancellationToken);

    /// <summary>
    /// Reverse the effects of a prior successful <see cref="ExecuteAsync"/>.
    /// Default is no-op. Built-in steps that mutate persistent state either
    /// override this to remove their mutation or rely on
    /// <see cref="InstallContext.TrackFile(string)"/> /
    /// <see cref="InstallContext.TrackDirectory(string, bool)"/> so the
    /// ledger handles it generically.
    /// </summary>
    Task RollbackAsync(InstallContext context, CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <summary>
    /// Predicate consulted by <see cref="StepExecutor"/> immediately before
    /// <see cref="ExecuteAsync"/>. When <c>false</c>, the step is recorded as
    /// <see cref="StepOutcome.Skipped"/> and is NOT added to the rollback
    /// list. Default: always run. Custom steps built via the fluent
    /// <c>StepBuilder.When(...)</c> override this; built-in steps don't.
    /// </summary>
    bool ShouldRun(InstallContext context) => true;

    /// <summary>
    /// True when this step marks the install's "point of no return" — once it
    /// has succeeded, any later step failure must NOT trigger rollback of
    /// this step or the steps that ran before it. Only steps and ledger
    /// entries added <em>after</em> a PONR participate in unwind. Default:
    /// false. Custom steps opt in via the fluent <c>StepBuilder.PointOfNoReturn(reason)</c>.
    /// </summary>
    bool IsPointOfNoReturn => false;

    /// <summary>
    /// Human-readable rationale supplied to <c>PointOfNoReturn(reason)</c>.
    /// Logged when the executor first observes <see cref="IsPointOfNoReturn"/>
    /// for diagnostics. <c>null</c> when <see cref="IsPointOfNoReturn"/> is
    /// false.
    /// </summary>
    string? PointOfNoReturnReason => null;
}
