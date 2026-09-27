using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System;
using Instella.Core.Installation;
using Instella.Core.Platform;

namespace Instella.Installer.Runtime.Installation.BuiltIn;

/// <summary>
/// Registers manifest-declared file associations. Rollback removes only the
/// extensions this step actually registered; one the previous version (upgrade/repair)
/// also had is pointed back at the previous executable instead.
/// </summary>
internal sealed class RegisterFileAssociationsStep : IInstallStepExecution
{
    public string Name => "register-file-associations";
    public InstallStage Stage => InstallStage.Register;

    private readonly List<string> _registered = new();

    public async Task<StepResult> ExecuteAsync(InstallContext context, IStepProgress progress, CancellationToken cancellationToken)
    {
        if (!context.Options.RegisterFileAssociations || context.Manifest.FileAssociations is not { Count: > 0 })
        {
            progress.Report(1.0);
            return StepResult.Ok;
        }

        var exe = ExecutableResolver.Resolve(context);
        context.ExecutablePath = exe;

        var total = context.Manifest.FileAssociations.Count;
        var idx = 0;
        foreach (var assoc in context.Manifest.FileAssociations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            context.Log.Info($"file association: {assoc.Extension}");

            var ok = await context.Platform.RegisterFileAssociationAsync(
                new FileAssociationInfo(assoc.Extension, assoc.Description, context.Manifest.AppId, exe, ExecutableResolver.AssociationIcon(context, assoc), PerUser: context.Scope == InstallationScope.PerUser),
                cancellationToken);

            if (ok.Success)
            {
                _registered.Add(assoc.Extension);
                context.RegisteredAssociations.Add(assoc.Extension);
            }
            else
            {
                context.Log.Warn($"could not register the {assoc.Extension} association: {ok.Error}");
            }
            idx++;
            progress.Report(idx / (double)total);
        }

        return StepResult.Ok;
    }

    public async Task RollbackAsync(InstallContext context, CancellationToken cancellationToken)
    {
        var previous = context.ExistingInstallation?.FileAssociations ?? [];
        foreach (var ext in _registered)
        {
            var assoc = context.Manifest.FileAssociations?.FirstOrDefault(a => a.Extension == ext);
            if (assoc is not null && previous.Contains(ext, StringComparer.OrdinalIgnoreCase))
                await context.Platform.RegisterFileAssociationAsync(
                    new FileAssociationInfo(ext, assoc.Description, context.Manifest.AppId, ExecutableResolver.PreviousExecutable(context), ExecutableResolver.AssociationIcon(context, assoc), PerUser: context.Scope == InstallationScope.PerUser),
                    cancellationToken);
            else
                await context.Platform.UnregisterFileAssociationAsync(ext, context.Manifest.AppId, context.Scope == InstallationScope.PerUser, cancellationToken);
        }
    }
}
