using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Installation;

namespace Instella.Installer.Runtime.Installation.BuiltIn;

/// <summary>
/// Adds the install directory to the PATH environment variable (user or
/// system scope depending on <see cref="InstallContext.Scope"/>). Rollback
/// removes the same entry unless the previous version (upgrade/repair) had added it too.
/// </summary>
internal sealed class AddToPathStep : IInstallStepExecution
{
    public string Name => "add-to-path";
    public InstallStage Stage => InstallStage.Register;

    private bool _added;

    public async Task<StepResult> ExecuteAsync(InstallContext context, IStepProgress progress, CancellationToken cancellationToken)
    {
        if (!context.Options.AddToPath)
        {
            progress.Report(1.0);
            return StepResult.Ok;
        }

        context.Log.Info($"adding to PATH: {context.InstallPath}");
        var result = await context.Platform.AddToPathAsync(context.InstallPath, context.Scope == InstallationScope.PerUser, cancellationToken);
        _added = result.Success;
        if (!result.Success)
            context.Log.Warn($"could not add '{context.InstallPath}' to PATH: {result.Error}");
        context.AddedToPath = _added;
        progress.Report(1.0);
        return StepResult.Ok;
    }

    public async Task RollbackAsync(InstallContext context, CancellationToken cancellationToken)
    {
        if (!_added || context.ExistingInstallation?.AddedToPath == true) return;
        await context.Platform.RemoveFromPathAsync(context.InstallPath, context.Scope == InstallationScope.PerUser, cancellationToken);
    }
}
