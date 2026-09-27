using System.Collections.Generic;

namespace Instella.Installer.Runtime.Installation;

/// <summary>
/// Terminal result of running an install step list.
/// </summary>
/// <param name="Success">True when every executed step succeeded (or was skipped).</param>
/// <param name="Error">Failure message for the step that caused the run to abort, or <c>null</c>.</param>
/// <param name="Steps">Ordered audit trail. Same length as the step list, except when cancellation short-circuits.</param>
/// <param name="Warnings">Non-fatal notes — notably rollback-of-rollback failures collected during unwind.</param>
public sealed record ExecutionResult(
    bool Success,
    string? Error,
    IReadOnlyList<StepExecutionRecord> Steps,
    IReadOnlyList<string> Warnings)
{
    internal static ExecutionResult Ok(IReadOnlyList<StepExecutionRecord> steps, IReadOnlyList<string> warnings)
        => new(true, null, steps, warnings);

    internal static ExecutionResult Fail(string error, IReadOnlyList<StepExecutionRecord> steps, IReadOnlyList<string> warnings)
        => new(false, error, steps, warnings);
}
