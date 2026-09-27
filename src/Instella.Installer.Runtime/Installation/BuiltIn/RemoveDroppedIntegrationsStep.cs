using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Installation;
using Instella.Core.Platform;

namespace Instella.Installer.Runtime.Installation.BuiltIn;

/// <summary>
/// On an upgrade or repair, removes the integrations the previous version had but this run
/// does not ask for: a shortcut that was unticked, a file association the new version no longer
/// declares, a PATH entry or auto-start the new version dropped. Without it they stayed forever
/// and survived uninstall, because the new manifest records only what this run created.
/// </summary>
/// <remarks>
/// Every removal is reversible until commit: rollback re-creates exactly what was removed,
/// pointing at the previous executable (as <see cref="CreateShortcutsStep"/> does).
/// </remarks>
internal sealed class RemoveDroppedIntegrationsStep : IInstallStepExecution
{
    public string Name => "remove-dropped-integrations";
    public InstallStage Stage => InstallStage.Register;

    private readonly List<ShortcutLocation> _shortcuts = [];
    private readonly List<string> _associations = [];
    private bool _path;
    private bool _autoStart;

    public async Task<StepResult> ExecuteAsync(InstallContext context, IStepProgress progress, CancellationToken cancellationToken)
    {
        if (context.ExistingInstallation is not { } previous)
        {
            progress.Report(1.0);
            return StepResult.Ok;
        }

        var options = context.Options;
        var perUser = context.Scope == InstallationScope.PerUser;
        var previousExe = ExecutableResolver.PreviousExecutable(context);

        if (previous.HasDesktopShortcut && !options.CreateDesktopShortcut)
            await RemoveShortcutAsync(context, ShortcutLocation.Desktop, previousExe, perUser, cancellationToken);
        if (previous.HasStartMenuShortcut && !options.CreateStartMenuShortcut)
            await RemoveShortcutAsync(context, ShortcutLocation.StartMenu, previousExe, perUser, cancellationToken);

        var wanted = options.RegisterFileAssociations
            ? (context.Manifest.FileAssociations ?? []).Select(a => a.Extension).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ext in previous.FileAssociations ?? [])
        {
            if (wanted.Contains(ext)) continue;
            var result = await context.Platform.UnregisterFileAssociationAsync(ext, context.Manifest.AppId, perUser, cancellationToken);
            Record(context, $"file association {ext}", result, () => _associations.Add(ext));
        }

        if (previous.AddedToPath && !options.AddToPath)
        {
            var result = await context.Platform.RemoveFromPathAsync(context.InstallPath, perUser, cancellationToken);
            Record(context, "PATH entry", result, () => _path = true);
        }

        if (previous.HasAutoStart && !options.ConfigureAutoStart)
        {
            var result = await context.Platform.RemoveAutoStartAsync(context.Manifest.AppId, perUser, cancellationToken);
            Record(context, "auto-start entry", result, () => _autoStart = true);
        }

        progress.Report(1.0);
        return StepResult.Ok;
    }

    private async Task RemoveShortcutAsync(InstallContext context, ShortcutLocation location, string previousExe, bool perUser, CancellationToken ct)
    {
        var result = await context.Platform.RemoveShortcutAsync(
            new ShortcutInfo(context.AppName, previousExe, null, null, location, PerUser: perUser), ct);
        Record(context, $"{(location == ShortcutLocation.Desktop ? "desktop" : "start-menu")} shortcut", result, () => _shortcuts.Add(location));
    }

    /// <summary>A failed removal is a warning (the item then stays recorded nowhere, as before), never a failed install.</summary>
    private static void Record(InstallContext context, string what, PlatformResult result, Action onRemoved)
    {
        if (result.Success)
        {
            onRemoved();
            context.DroppedIntegrations.Add(what);
            context.Log.Info($"removed the {what} the previous version had and this version does not");
        }
        else
        {
            context.Log.Warn($"could not remove the {what} the previous version had: {result.Error}");
        }
    }

    public async Task RollbackAsync(InstallContext context, CancellationToken cancellationToken)
    {
        if (context.ExistingInstallation is null) return;
        var perUser = context.Scope == InstallationScope.PerUser;
        var previousExe = ExecutableResolver.PreviousExecutable(context);

        foreach (var location in _shortcuts)
            await context.Platform.CreateShortcutAsync(
                new ShortcutInfo(context.AppName, previousExe, ExecutableResolver.Icon(context), null, location, PerUser: perUser), cancellationToken);
        foreach (var ext in _associations)
        {
            var declared = context.Manifest.FileAssociations?.FirstOrDefault(a => string.Equals(a.Extension, ext, StringComparison.OrdinalIgnoreCase));
            await context.Platform.RegisterFileAssociationAsync(
                new FileAssociationInfo(ext, declared?.Description ?? $"{context.AppName} file", context.Manifest.AppId, previousExe,
                    declared is null ? ExecutableResolver.Icon(context) : ExecutableResolver.AssociationIcon(context, declared), PerUser: perUser),
                cancellationToken);
        }
        if (_path)
            await context.Platform.AddToPathAsync(context.InstallPath, perUser, cancellationToken);
        if (_autoStart)
            await context.Platform.ConfigureAutoStartAsync(
                new AutoStartInfo(context.Manifest.AppId, previousExe, null, PerUser: perUser), cancellationToken);
    }
}
