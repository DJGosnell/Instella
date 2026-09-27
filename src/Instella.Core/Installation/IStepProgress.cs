namespace Instella.Core.Installation;

/// <summary>
/// Progress channel passed to each step's Execute. The executor combines per-step
/// fractions via step weights into an overall install percentage.
/// </summary>
public interface IStepProgress
{
    /// <summary>Reports the step's own progress.</summary>
    /// <param name="fraction">How far the step is, clamped into <c>[0.0, 1.0]</c>.</param>
    /// <param name="status">Optional status text shown to the user.</param>
    void Report(double fraction, string? status = null);
}
