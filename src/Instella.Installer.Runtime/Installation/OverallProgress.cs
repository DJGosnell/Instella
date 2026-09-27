using Instella.Core.Installation;

namespace Instella.Installer.Runtime.Installation;

/// <summary>
/// Aggregated installer progress passed to the caller's
/// <c>IProgress&lt;OverallProgress&gt;</c> by <see cref="StepExecutor"/>. The
/// fraction is computed from each step's reported fraction weighted by its
/// <see cref="IInstallStep.Weight"/>.
/// </summary>
/// <param name="Fraction">Overall progress in [0.0, 1.0].</param>
/// <param name="Stage">Stage currently running (Prereqs → Extract → Register → Finalize).</param>
/// <param name="StepName">Name of the step currently executing, or <c>null</c> between steps.</param>
/// <param name="Status">Free-form message from the step's own <c>Report</c> call.</param>
public readonly record struct OverallProgress(
    double Fraction,
    InstallStage Stage,
    string? StepName,
    string? Status);
