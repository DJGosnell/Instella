using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Installation;
using Instella.Core.Platform;

namespace Instella.Installer.Runtime.Installation.BuiltIn;

/// <summary>
/// Creates desktop / start-menu shortcuts per user choice. Rollback removes the shortcuts
/// the step created, except that a shortcut the previous version also had (upgrade or
/// repair) is re-created as it was, so a failed upgrade leaves the old version usable.
/// </summary>
internal sealed class CreateShortcutsStep : IInstallStepExecution
{
    public string Name => "create-shortcuts";
    public InstallStage Stage => InstallStage.Register;

    private bool _createdDesktop;
    private bool _createdStartMenu;

    public async Task<StepResult> ExecuteAsync(InstallContext context, IStepProgress progress, CancellationToken cancellationToken)
    {
        var options = context.Options;
        if (!options.CreateDesktopShortcut && !options.CreateStartMenuShortcut)
        {
            progress.Report(1.0);
            return StepResult.Ok;
        }

        var exe = ExecutableResolver.Resolve(context);
        context.ExecutablePath = exe;

        if (options.CreateDesktopShortcut)
        {
            context.Log.Info("creating desktop shortcut");
            _createdDesktop = Report(context, "desktop", await context.Platform.CreateShortcutAsync(
                new ShortcutInfo(context.AppName, exe, ExecutableResolver.Icon(context), null, ShortcutLocation.Desktop, PerUser: context.Scope == InstallationScope.PerUser),
                cancellationToken));
        }
        progress.Report(0.5);

        if (options.CreateStartMenuShortcut)
        {
            context.Log.Info("creating start-menu shortcut");
            _createdStartMenu = Report(context, "start-menu", await context.Platform.CreateShortcutAsync(
                new ShortcutInfo(context.AppName, exe, ExecutableResolver.Icon(context), null, ShortcutLocation.StartMenu, PerUser: context.Scope == InstallationScope.PerUser),
                cancellationToken));
        }

        context.CreatedDesktopShortcut = _createdDesktop;
        context.CreatedStartMenuShortcut = _createdStartMenu;
        progress.Report(1.0);
        return StepResult.Ok;
    }

    /// <summary>A shortcut failure never fails the install; it is logged with its reason.</summary>
    private static bool Report(InstallContext context, string which, PlatformResult result)
    {
        if (!result.Success)
            context.Log.Warn($"could not create the {which} shortcut: {result.Error}");
        return result.Success;
    }

    public async Task RollbackAsync(InstallContext context, CancellationToken cancellationToken)
    {
        var prev = context.ExistingInstallation;
        if (_createdDesktop)
            await UndoAsync(context, ShortcutLocation.Desktop, prev?.HasDesktopShortcut == true, cancellationToken);
        if (_createdStartMenu)
            await UndoAsync(context, ShortcutLocation.StartMenu, prev?.HasStartMenuShortcut == true, cancellationToken);
    }

    private static async Task UndoAsync(InstallContext context, ShortcutLocation location, bool previouslyExisted, CancellationToken ct)
    {
        if (previouslyExisted)
        {
            await context.Platform.CreateShortcutAsync(
                new ShortcutInfo(context.AppName, ExecutableResolver.PreviousExecutable(context), ExecutableResolver.Icon(context), null, location, PerUser: context.Scope == InstallationScope.PerUser), ct);
        }
        else
        {
            await context.Platform.RemoveShortcutAsync(
                new ShortcutInfo(context.AppName, ExecutableResolver.Resolve(context), null, null, location, PerUser: context.Scope == InstallationScope.PerUser), ct);
        }
    }
}
