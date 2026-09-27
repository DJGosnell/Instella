using System.Collections.Generic;
using Instella.Core.Installation;
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Runtime.Installation;

namespace Instella.Installer.Runtime.Runners;

/// <summary>
/// Produces the simulated step list for a given preview mode. Keeps the
/// simulation table off <see cref="PreviewModeRunner"/> so tests can pin step
/// names without spinning up a runner.
/// </summary>
/// <remarks>
/// Wizard-mode simulated lists mirror what the real mode runners would build
/// (for <see cref="InstallerMode.FirstInstall"/> that's
/// <see cref="OfflineInstallRunner.BuildDefaultSteps"/> plus user steps);
/// Update/Uninstall substitute synthetic steps because their real runners
/// don't use <see cref="StepExecutor"/>. Step names are stable — the
/// <c>--preview-fail</c> parser validates against them.
/// </remarks>
internal static class PreviewStepLists
{
    public static IReadOnlyList<IInstallStepExecution> Build(FrozenConfig config, InstallerMode mode)
    {
        return mode switch
        {
            InstallerMode.FirstInstall or InstallerMode.Upgrade or InstallerMode.Repair =>
                BuildInstallSteps(config),
            InstallerMode.Update => BuildUpdateSteps(),
            InstallerMode.Uninstall => BuildUninstallSteps(),
            _ => new List<IInstallStepExecution>(),
        };
    }

    private static IReadOnlyList<IInstallStepExecution> BuildInstallSteps(FrozenConfig config)
    {
        var list = new List<IInstallStepExecution>(OfflineInstallRunner.BuildDefaultSteps());
        foreach (var user in config.UserSteps)
            list.Add(user);
        return list;
    }

    private static IReadOnlyList<IInstallStepExecution> BuildUpdateSteps() => new IInstallStepExecution[]
    {
        new SyntheticStep("download-update", InstallStage.Prereqs, weight: 4),
        new SyntheticStep("extract-update", InstallStage.Extract, weight: 3),
        new SyntheticStep("replace-files", InstallStage.Register, weight: 2),
        new SyntheticStep("finalize-update", InstallStage.Finalize, weight: 1),
    };

    private static IReadOnlyList<IInstallStepExecution> BuildUninstallSteps() => new IInstallStepExecution[]
    {
        new SyntheticStep("remove-shortcuts", InstallStage.Register, weight: 1),
        new SyntheticStep("remove-file-associations", InstallStage.Register, weight: 1),
        new SyntheticStep("remove-path-entry", InstallStage.Register, weight: 1),
        new SyntheticStep("remove-auto-start", InstallStage.Register, weight: 1),
        new SyntheticStep("unregister-uninstall-entry", InstallStage.Register, weight: 1),
        new SyntheticStep("delete-files", InstallStage.Finalize, weight: 3),
    };

    /// <summary>
    /// Step whose body is never invoked — <see cref="SimulatedStepExecutor"/>
    /// only reads <see cref="IInstallStep.Name"/> / <see cref="IInstallStep.Stage"/>
    /// / <see cref="IInstallStep.Weight"/>. If <c>ExecuteAsync</c> is ever
    /// reached (e.g. someone accidentally routes a synthetic step through the
    /// real <c>StepExecutor</c>), it succeeds as a no-op rather than blowing up.
    /// </summary>
    private sealed class SyntheticStep : IInstallStepExecution
    {
        public SyntheticStep(string name, InstallStage stage, int weight)
        {
            Name = name;
            Stage = stage;
            Weight = weight;
        }

        public string Name { get; }
        public InstallStage Stage { get; }
        public int Weight { get; }

        public System.Threading.Tasks.Task<StepResult> ExecuteAsync(
            InstallContext context, IStepProgress progress, System.Threading.CancellationToken cancellationToken)
            => System.Threading.Tasks.Task.FromResult(StepResult.Ok);
    }
}
