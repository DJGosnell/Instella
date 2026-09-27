using System.Collections.Generic;
using Instella.Core.Installation;

namespace Instella.Installer.Runtime.Installation;

/// <summary>
/// Per-step audit entry collected by <see cref="StepExecutor"/>. Used by the
/// test harness and by the post-run summary to distinguish steps
/// that were skipped, ran, failed, or rolled back.
/// </summary>
/// <param name="Name">The step name.</param>
/// <param name="Stage">The stage it ran in.</param>
/// <param name="Outcome">What happened to it.</param>
/// <param name="Error">Why it failed; null otherwise.</param>
/// <param name="Warnings">Non-fatal messages it produced, or null.</param>
public sealed record StepExecutionRecord(
    string Name,
    InstallStage Stage,
    StepOutcome Outcome,
    string? Error = null,
    IReadOnlyList<string>? Warnings = null);

/// <summary>What happened to one step.</summary>
public enum StepOutcome
{
    /// <summary>Step ran and returned <see cref="StepResult.Ok"/>.</summary>
    Succeeded,

    /// <summary>Step returned <c>StepResult.WithWarnings</c> but success=true.</summary>
    SucceededWithWarnings,

    /// <summary>Step returned a failing <see cref="StepResult"/> or threw.</summary>
    Failed,

    /// <summary>Step was skipped (e.g., <c>When</c> predicate false).</summary>
    Skipped,

    /// <summary>Step had succeeded; rollback ran after a later step failed.</summary>
    RolledBack,
}
