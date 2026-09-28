using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Installation;
using Instella.Core.Utilities;
using Instella.Installer.Runtime.AppUpgrade;

namespace Instella.Installer.Runtime.Installation.BuiltIn;

/// <summary>
/// Runs the app's upgrade program (<c>instella-upgrade.json</c>) right after
/// <c>commit-transaction</c>, on the new files, while the commit can still be rolled back: the
/// commit is held until the program exits 0, then confirmed. A failure fails the install, and the
/// executor rolls the commit back (exit 15). Runs before every custom Finalize step, so a point of
/// no return can never stop that rollback. Skipped when the app declares no program.
/// </summary>
internal sealed class AppUpgradeStep : IInstallStepExecution
{
    public const string StepName = "app-upgrade";

    private bool _ran;
    private Version? _to;

    public string Name => StepName;
    public InstallStage Stage => InstallStage.Finalize;
    public int Weight => 3;

    /// <summary>Whether the files being installed declare an upgrade program.</summary>
    public static bool IsDeclared(InstallContext context) =>
        context.ExtractedFiles.Any(f => string.Equals(f.RelativePath.Replace('\\', '/'),
            AppUpgradeContract.DeclarationFileName, StringComparison.OrdinalIgnoreCase));

    public bool ShouldRun(InstallContext context) => IsDeclared(context);

    public async Task<StepResult> ExecuteAsync(InstallContext context, IStepProgress progress, CancellationToken cancellationToken)
    {
        // Once the program starts it is never cancelled: only its time limit stops it.
        cancellationToken.ThrowIfCancellationRequested();
        var runner = new AppUpgradeRunner(context.FileSystem, context.Migrations.Programs, context.Log);
        var request = new AppUpgradeRequest
        {
            Mode = ModeFor(context),
            From = context.ExistingInstallation?.Version,
            To = context.AppVersion,
            Scope = context.Scope,
            InstallPath = context.InstallPath,
            AppId = context.AppId,
            Platform = context.Platform.Platform,
            InstalledFiles = context.ExtractedFiles.Select(f => f.RelativePath).ToList(),
        };
        var result = await runner.RunAsync(request, new Progress(progress), CancellationToken.None);
        if (!result.Success)
            return StepResult.Fail($"the app's data could not be upgraded: {result.Message}");

        _ran = result.Ran;
        _to = context.AppVersion;
        if (context.Transaction is { } txn)
            await txn.ConfirmCommitAsync();
        progress.Report(1.0);
        return StepResult.Ok;
    }

    /// <summary>
    /// The files go back, but the app's data cannot: say so. Only reached when a later step
    /// failed after the program had succeeded.
    /// </summary>
    public Task RollbackAsync(InstallContext context, CancellationToken cancellationToken)
    {
        if (_ran)
            context.Log.Warn($"app-upgrade: the files are rolled back, but the app's upgrade program already ran. " +
                             $"The previous version starts on data version {_to} upgraded");
        return Task.CompletedTask;
    }

    /// <summary>The <c>--mode</c> for an install-pipeline run.</summary>
    internal static AppUpgradeLaunchMode ModeFor(InstallContext context)
    {
        if (context.ExistingInstallation is not { } existing)
            return AppUpgradeLaunchMode.FirstInstall;
        var order = AppVersions.Compare(context.AppVersion, existing.Version);
        return order == 0 ? AppUpgradeLaunchMode.Repair
            : order < 0 ? AppUpgradeLaunchMode.Downgrade
            : AppUpgradeLaunchMode.Upgrade;
    }

    private sealed class Progress(IStepProgress step) : IProgress<AppUpgradeProgress>
    {
        public void Report(AppUpgradeProgress value) =>
            step.Report(value.Fraction, value.Text ?? AppUpgradeRunner.DefaultStatus);
    }
}
