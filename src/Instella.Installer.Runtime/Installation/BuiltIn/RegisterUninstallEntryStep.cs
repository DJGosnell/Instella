using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System;
using Instella.Core.Installation;
using Instella.Core.Platform;

namespace Instella.Installer.Runtime.Installation.BuiltIn;

/// <summary>
/// Creates the "Installed Apps" / Add-Remove Programs entry pointing at the
/// staged uninstaller stub. Runs after <see cref="StageUninstallerStubStep"/>.
/// If the stub was not staged (<see cref="InstallContext.ExecutablePath"/>-style
/// warning from the stub step), this step skips ARP registration rather than
/// failing the install.
/// </summary>
internal sealed class RegisterUninstallEntryStep : IInstallStepExecution
{
    public string Name => "register-uninstall-entry";
    public InstallStage Stage => InstallStage.Register;

    public async Task<StepResult> ExecuteAsync(InstallContext context, IStepProgress progress, CancellationToken cancellationToken)
    {
        var stubPath = Path.Combine(context.InstallPath, StageUninstallerStubStep.UninstallExeName);
        var stubStaged = context.Transaction?.IsStaged(StageUninstallerStubStep.UninstallExeName) == true;
        if (!stubStaged && !context.FileSystem.Exists(stubPath))
        {
            context.Log.Warn("skipping ARP registration: uninstaller stub not present");
            progress.Report(1.0);
            return StepResult.Ok;
        }

        var exe = ExecutableResolver.Resolve(context);
        context.ExecutablePath = exe;

        var info = EntryFor(context, context.Manifest.Version, exe, context.ExtractedFiles.Sum(f => f.Size));
        var result = await context.Platform.RegisterUninstallEntryAsync(info, cancellationToken);
        if (!result.Success)
            context.Log.Warn($"could not register the Installed Apps entry: {result.Error}");
        context.UninstallEntryRegistered = result.Success;
        progress.Report(1.0);
        return StepResult.Ok;
    }

    public async Task RollbackAsync(InstallContext context, CancellationToken cancellationToken)
    {
        if (!context.UninstallEntryRegistered) return;

        // Upgrade/repair: put the previous version's entry back rather than removing it.
        if (context.ExistingInstallation is { HasUninstallEntry: true } prev)
        {
            await context.Platform.RegisterUninstallEntryAsync(
                EntryFor(context, prev.Version, ExecutableResolver.PreviousExecutable(context), prev.Files.Sum(f => f.Size)),
                cancellationToken);
            return;
        }

        await context.Platform.UnregisterUninstallEntryAsync(
            context.Manifest.AppId,
            context.Scope == InstallationScope.PerUser,
            cancellationToken);
    }

    /// <summary>
    /// The Installed Apps uninstall command. argv[0] is parsed differently by
    /// <c>CommandLineToArgvW</c>, so the stub path is quoted plainly and the arguments go through
    /// <c>WindowsCommandLine.Join</c>, which doubles backslashes before a quote: a plainly quoted
    /// <c>"D:\Apps\QN\"</c> would parse as <c>D:\Apps\QN"</c>.
    /// </summary>
    internal static string UninstallCommandFor(string stubPath, string installPath) =>
        $"\"{stubPath}\" " + Instella.Core.Platform.Windows.WindowsCommandLine.Join(["--uninstall", "--path", installPath]);

    private static UninstallEntryInfo EntryFor(InstallContext context, Version version, string exe, long totalBytes)
    {
        var stubPath = Path.Combine(context.InstallPath, StageUninstallerStubStep.UninstallExeName);
        return new UninstallEntryInfo(
            AppId: context.Manifest.AppId,
            DisplayName: context.Manifest.AppName,
            DisplayVersion: version.ToString(),
            Publisher: string.IsNullOrWhiteSpace(context.Manifest.Publisher) ? "Unknown" : context.Manifest.Publisher!,
            InstallLocation: context.InstallPath,
            DisplayIcon: ExecutableResolver.Icon(context) ?? exe,
            UninstallCommand: UninstallCommandFor(stubPath, context.InstallPath),
            UrlInfoAbout: context.Manifest.HomepageUrl,
            EstimatedSizeKb: (int)Math.Min(int.MaxValue, totalBytes / 1024),
            PerUser: context.Scope == InstallationScope.PerUser);
    }
}
