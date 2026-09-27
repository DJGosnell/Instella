using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.FileSystem;
using Instella.Core.Installation;
using Instella.Core.Logging;
using Instella.Installer.Runtime.Core.Transactions;

namespace Instella.Installer.Runtime.Runners;

/// <summary>
/// <c>--recover</c>: finishes or undoes an interrupted install transaction in the
/// installation at <c>--path</c> (or the stub's own directory), then exits. The SDK's
/// <c>StartRecoveryAsync</c> launches this when the app finds an interrupted update.
/// </summary>
internal sealed class RecoverModeRunner(IInstellaLogger log, IFileSystem fileSystem)
{
    /// <summary>Test seam: how long to wait for another Instella process on the folder.</summary>
    internal TimeSpan? LockTimeout { get; init; }

    /// <summary>Errors; null shows message boxes (stderr when silent).</summary>
    internal IUserMessages? Messages { get; init; }

    /// <summary>The app's name, for messages.</summary>
    internal string AppName { get; init; } = "the application";

    public async Task<InstellaExitCode> RunAsync(DispatchResult dispatch, CancellationToken ct)
    {
        var messages = Messages ?? new UserMessages(dispatch.IsSilent);
        // Every failure is logged and shown.
        InstellaExitCode Fail(InstellaExitCode code, string text)
        {
            log.Error($"recover: {text}");
            messages.Error($"Repair {AppName}", UserMessages.WithLog(text));
            return code;
        }

        var installPath = dispatch.InstallPath ?? Path.GetDirectoryName(Environment.ProcessPath);
        if (string.IsNullOrEmpty(installPath))
            return Fail(InstellaExitCode.UsageInvalidArgs, "could not resolve the install path (use --path)");

        // Recovering while another process commits would roll its commit back halfway.
        await using var held = await InstallRootLock.AcquireOrReportAsync(
            installPath, dispatch.IsSilent, LockTimeout, AppName, log, ct);
        if (held is null) return Fail(InstellaExitCode.InstallationBusy, InstallRootLock.BusyMessage(AppName));

        try
        {
            var rolledBack = await InstallTransaction.RecoverAsync(fileSystem, installPath, ct, log);
            log.Info(rolledBack
                ? $"recover: an interrupted update in '{installPath}' was rolled back"
                : $"recover: '{installPath}' is consistent; nothing to do");
            return InstellaExitCode.Success;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.Error($"recover: {ex.Message}", ex);
            messages.Error($"Repair {AppName}", UserMessages.WithLog($"Could not repair the interrupted update: {ex.Message}"));
            return InstellaExitCode.UpdateRollbackFailed;
        }
    }
}
