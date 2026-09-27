using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Installation;
using Instella.Core.Platform;

namespace Instella.Installer.Runtime.Installation.BuiltIn;

/// <summary>
/// Configures launch-on-login for the installed app. Rollback removes the
/// auto-start registration by <see cref="Instella.Core.Manifest.InstellaManifest.AppId"/>,
/// or re-registers the previous executable when the previous version had auto-start.
/// </summary>
internal sealed class ConfigureAutoStartStep : IInstallStepExecution
{
    public string Name => "configure-auto-start";
    public InstallStage Stage => InstallStage.Register;

    private bool _configured;

    public async Task<StepResult> ExecuteAsync(InstallContext context, IStepProgress progress, CancellationToken cancellationToken)
    {
        if (!context.Options.ConfigureAutoStart)
        {
            progress.Report(1.0);
            return StepResult.Ok;
        }

        var exe = ExecutableResolver.Resolve(context);
        context.ExecutablePath = exe;

        context.Log.Info("configuring auto-start");
        var result = await context.Platform.ConfigureAutoStartAsync(
            new AutoStartInfo(context.Manifest.AppId, exe, null, PerUser: context.Scope == InstallationScope.PerUser),
            cancellationToken);
        _configured = result.Success;
        if (!result.Success)
            context.Log.Warn($"could not configure auto-start: {result.Error}");
        context.ConfiguredAutoStart = _configured;
        progress.Report(1.0);
        return StepResult.Ok;
    }

    public async Task RollbackAsync(InstallContext context, CancellationToken cancellationToken)
    {
        if (!_configured) return;
        if (context.ExistingInstallation?.HasAutoStart == true)
            await context.Platform.ConfigureAutoStartAsync(
                new AutoStartInfo(context.Manifest.AppId, ExecutableResolver.PreviousExecutable(context), null, PerUser: context.Scope == InstallationScope.PerUser), cancellationToken);
        else
            await context.Platform.RemoveAutoStartAsync(context.Manifest.AppId, context.Scope == InstallationScope.PerUser, cancellationToken);
    }
}
