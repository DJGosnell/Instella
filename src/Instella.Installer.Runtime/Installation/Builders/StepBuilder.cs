using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Installation;

namespace Instella.Installer.Runtime.Installation.Builders;

/// <summary>
/// Fluent surface for declaring a custom install step from inside the user's
/// <c>*.Installer</c> project. The user obtains a <see cref="StepBuilder"/>
/// from <c>InstallerBuilder.AddStep(name, sb =&gt; ...)</c> and
/// configures it with <see cref="Execute(StepExecuteAsync)"/> plus exactly one
/// rollback mode (<see cref="Rollback(StepRollbackAsync)"/>,
/// <see cref="UseTrackedRollback"/>, or <see cref="NoRollbackNeeded(string)"/>).
/// <see cref="Build"/> validates the rollback-mode cardinality and produces an
/// internal <see cref="StepSpec"/> that the runtime treats as an
/// <see cref="IInstallStepExecution"/>.
/// </summary>
/// <remarks>
/// Slotting hints (<see cref="InStage"/>, <see cref="Before(string)"/>,
/// <see cref="After(string)"/>) are only recorded on the spec: <c>InstallerBuilder</c>
/// performs the topological sort over the full step list and feeds the resulting linear
/// sequence to <see cref="StepExecutor"/>, which runs steps in the order it is given.
/// </remarks>
public sealed class StepBuilder
{
    private readonly string _name;
    private InstallStage _stage = InstallStage.Register;
    private bool _stageExplicit;
    private Func<InstallContext, CancellationToken, Task>? _onUninstall;
    private int _weight = 1;
    private StepExecuteAsync? _execute;
    private StepRollbackAsync? _rollback;
    private bool _useTrackedRollback;
    private string? _noRollbackReason;
    private string? _ponrReason;
    private Func<InstallContext, bool>? _whenPredicate;
    private string? _displayName;
    private readonly List<StepOrderingHint> _orderingHints = new();

    internal StepBuilder(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        _name = name;
    }

    /// <summary>Static factory for tests; user code goes through <c>InstallerBuilder.AddStep</c>.</summary>
    internal static StepBuilder Create(string name) => new(name);

    /// <summary>Relative weight for progress aggregation. Defaults to 1; values &lt;= 0 are clamped to 1 by the executor.</summary>
    public StepBuilder Weight(int weight)
    {
        _weight = weight;
        return this;
    }

    /// <summary>Place this step in <paramref name="stage"/>. Default: <see cref="InstallStage.Register"/>.</summary>
    public StepBuilder InStage(InstallStage stage)
    {
        _stage = stage;
        _stageExplicit = true;
        return this;
    }

    /// <summary>The step's body. Required — <see cref="Build"/> throws if not called.</summary>
    public StepBuilder Execute(StepExecuteAsync execute)
    {
        ArgumentNullException.ThrowIfNull(execute);
        _execute = execute;
        return this;
    }

    /// <summary>
    /// Provide an explicit rollback delegate. Mutually exclusive with
    /// <see cref="UseTrackedRollback"/> and <see cref="NoRollbackNeeded(string)"/>
    /// — choosing two is a build-time error.
    /// </summary>
    public StepBuilder Rollback(StepRollbackAsync rollback)
    {
        ArgumentNullException.ThrowIfNull(rollback);
        _rollback = rollback;
        return this;
    }

    /// <summary>
    /// Use the generic tracked rollback: every <c>ctx.TrackFile</c> /
    /// <c>ctx.TrackDirectory</c> / <c>ctx.TrackRegistryValue</c> /
    /// <c>ctx.TrackRegistryKey</c> / <c>ctx.TrackPathEntry</c> recorded during
    /// <see cref="Execute(StepExecuteAsync)"/> is reversed in LIFO order on
    /// install failure. Mutually exclusive with the other two rollback modes.
    /// </summary>
    public StepBuilder UseTrackedRollback()
    {
        _useTrackedRollback = true;
        return this;
    }

    /// <summary>
    /// Declare that this step intentionally has no rollback. The reason is
    /// logged if a later step fails (so audit trails make clear why earlier
    /// state was retained). Mutually exclusive with the other two rollback
    /// modes.
    /// </summary>
    public StepBuilder NoRollbackNeeded(string reason)
    {
        ArgumentException.ThrowIfNullOrEmpty(reason);
        _noRollbackReason = reason;
        return this;
    }

    /// <summary>
    /// Mark this step as the install's point of no return. After it succeeds,
    /// any subsequent step failure rolls back ONLY the steps and ledger
    /// entries that were added after this point — earlier work stays on disk.
    /// A point-of-no-return step runs in <see cref="InstallStage.Finalize"/>, after the
    /// install transaction has committed (so the installed manifest always records what
    /// such a step leaves behind); it defaults to that stage, and any other stage is an error.
    /// </summary>
    public StepBuilder PointOfNoReturn(string reason)
    {
        ArgumentException.ThrowIfNullOrEmpty(reason);
        _ponrReason = reason;
        return this;
    }

    /// <summary>
    /// Code to run when the app is uninstalled, for a step whose effects the generic tracked
    /// rollback cannot undo. The installed stub is this installer program, so the delegate
    /// is available at uninstall time; uninstall runs these in reverse stage order before
    /// removing Instella's own registrations.
    /// </summary>
    public StepBuilder OnUninstall(Func<InstallContext, CancellationToken, Task> uninstall)
    {
        ArgumentNullException.ThrowIfNull(uninstall);
        _onUninstall = uninstall;
        return this;
    }

    /// <summary>
    /// The name the wizard shows while the step runs ("Installing drivers…") and when it fails.
    /// Without it the step's <c>name</c> is shown.
    /// </summary>
    public StepBuilder WithDisplayName(string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        _displayName = displayName;
        return this;
    }

    /// <summary>Skip the step at runtime when the predicate returns false.</summary>
    public StepBuilder When(Func<InstallContext, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        _whenPredicate = predicate;
        return this;
    }

    /// <summary>Record a "run before this named step" ordering hint, honored by the builder's topological sort.</summary>
    public StepBuilder Before(string stepName)
    {
        ArgumentException.ThrowIfNullOrEmpty(stepName);
        _orderingHints.Add(StepOrderingHint.BeforeStep(stepName));
        return this;
    }

    /// <summary>Record a "run after this named step" ordering hint, honored by the builder's topological sort.</summary>
    public StepBuilder After(string stepName)
    {
        ArgumentException.ThrowIfNullOrEmpty(stepName);
        _orderingHints.Add(StepOrderingHint.AfterStep(stepName));
        return this;
    }

    /// <summary>Record a "run before any step in this stage" ordering hint.</summary>
    public StepBuilder Before(InstallStage stage)
    {
        _orderingHints.Add(StepOrderingHint.BeforeStage(stage));
        return this;
    }

    /// <summary>Record a "run after any step in this stage" ordering hint.</summary>
    public StepBuilder After(InstallStage stage)
    {
        _orderingHints.Add(StepOrderingHint.AfterStage(stage));
        return this;
    }

    /// <summary>
    /// Freeze the builder into a <see cref="StepSpec"/>. Throws if Execute was
    /// not called or if the rollback-mode cardinality is wrong (must be
    /// exactly one of: explicit Rollback delegate, UseTrackedRollback,
    /// NoRollbackNeeded).
    /// </summary>
    internal StepSpec Build()
    {
        if (_execute is null)
            throw new InvalidOperationException($"step '{_name}': Execute(...) is required");

        var modeCount = (_rollback is not null ? 1 : 0)
                        + (_useTrackedRollback ? 1 : 0)
                        + (_noRollbackReason is not null ? 1 : 0);
        if (modeCount == 0)
            throw new InvalidOperationException(
                $"step '{_name}': must declare exactly one of Rollback(...), UseTrackedRollback(), or NoRollbackNeeded(reason)");
        if (modeCount > 1)
            throw new InvalidOperationException(
                $"step '{_name}': Rollback(...), UseTrackedRollback(), and NoRollbackNeeded(reason) are mutually exclusive");

        var stage = _stage;
        if (_ponrReason is not null)
        {
            if (_stageExplicit && _stage != InstallStage.Finalize)
                throw new InvalidOperationException(
                    $"step '{_name}': a point-of-no-return step must be in the Finalize stage, which runs after the install transaction commits");
            stage = InstallStage.Finalize;
        }

        var mode = _rollback is not null
            ? RollbackMode.Explicit
            : _useTrackedRollback
                ? RollbackMode.Tracked
                : RollbackMode.None;

        return new StepSpec(
            _name,
            stage,
            _weight,
            _execute,
            _rollback,
            mode,
            _noRollbackReason,
            _ponrReason,
            _whenPredicate,
            _orderingHints)
        {
            OnUninstall = _onUninstall,
            DisplayName = _displayName,
        };
    }
}

/// <summary>Delegate signature for <see cref="StepBuilder.Execute(StepExecuteAsync)"/>.</summary>
public delegate Task<StepResult> StepExecuteAsync(InstallContext context, IStepProgress progress, CancellationToken cancellationToken);

/// <summary>Delegate signature for <see cref="StepBuilder.Rollback(StepRollbackAsync)"/>.</summary>
public delegate Task StepRollbackAsync(InstallContext context, CancellationToken cancellationToken);

internal enum RollbackMode { Explicit, Tracked, None }

internal readonly record struct StepOrderingHint(StepOrderingHintKind Kind, string? StepName, InstallStage Stage)
{
    public static StepOrderingHint BeforeStep(string name) => new(StepOrderingHintKind.BeforeStep, name, default);
    public static StepOrderingHint AfterStep(string name) => new(StepOrderingHintKind.AfterStep, name, default);
    public static StepOrderingHint BeforeStage(InstallStage stage) => new(StepOrderingHintKind.BeforeStage, null, stage);
    public static StepOrderingHint AfterStage(InstallStage stage) => new(StepOrderingHintKind.AfterStage, null, stage);
}

internal enum StepOrderingHintKind { BeforeStep, AfterStep, BeforeStage, AfterStage }
