namespace Instella.Core.Installation;

/// <summary>
/// A single unit of work in the install pipeline. Steps are declared in the
/// fluent builder, slotted by <see cref="Stage"/> (and optional before/after
/// step-name anchors), and executed in order by the runtime's StepExecutor.
/// </summary>
/// <remarks>
/// The <c>InstallContext</c> type is defined in <c>Instella.Installer.Runtime</c>
///. Steps declared here in Core take it as <see cref="object"/>
/// through <c>IInstallStepExecution</c> — the Runtime wraps and casts at
/// dispatch time. This keeps Core free of UI / platform dependencies.
/// </remarks>
public interface IInstallStep
{
    /// <summary>Unique name for the step. Used for before/after anchors and logging.</summary>
    string Name { get; }

    /// <summary>Stage slot. Steps run in stage order; custom steps slot via before/after.</summary>
    InstallStage Stage { get; }

    /// <summary>Relative weight for progress aggregation. Defaults to 1.</summary>
    int Weight => 1;
}
