using System.Linq;
using Instella.Core.Installation;
using Instella.Installer.Runtime.Installation;
using Instella.Installer.Runtime.Installation.BuiltIn;

namespace Instella.Installer.Runtime.Runners;

/// <summary>The exit code for an install pipeline that failed (and was rolled back).</summary>
internal static class InstallFailureExit
{
    public static InstellaExitCode For(ExecutionResult result)
    {
        if (result.Steps.Any(s => s.Name == PrerequisitesStep.StepName && s.Outcome == StepOutcome.Failed))
            return InstellaExitCode.InstallPrereqFailed;
        if (result.Steps.Any(s => s.Name == AppUpgradeStep.StepName && s.Outcome == StepOutcome.Failed))
            return InstellaExitCode.InstallAppUpgradeFailed;
        if (result.Steps.Any(s => s.Name == ServiceStepNames.StartServices && s.Outcome == StepOutcome.Failed))
            return InstellaExitCode.InstallServiceFailed;
        return result.Warnings.Count > 0
            ? InstellaExitCode.InstallRollbackCompletedWithWarnings
            : InstellaExitCode.InstallGeneralFailure;
    }
}
