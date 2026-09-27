namespace Instella.Core.Installation;

/// <summary>
/// Outcome of a single <see cref="IInstallStep"/> execution. Warnings are
/// non-fatal messages surfaced to the user / log (for example, an optional
/// side-effect that could not be applied but does not invalidate the install).
/// </summary>
/// <param name="Success">True when the step succeeded.</param>
/// <param name="Error">Why the step failed; null on success.</param>
/// <param name="Warnings">Non-fatal messages, or null.</param>
public readonly record struct StepResult(
    bool Success,
    string? Error,
    IReadOnlyList<string>? Warnings)
{
    /// <summary>The step succeeded.</summary>
    public static StepResult Ok { get; } = new(true, null, null);

    /// <summary>The step failed; the install rolls back.</summary>
    public static StepResult Fail(string error) => new(false, error, null);

    /// <summary>The step succeeded with non-fatal warnings.</summary>
    public static StepResult OkWithWarnings(IReadOnlyList<string> warnings)
        => new(true, null, warnings);
}
