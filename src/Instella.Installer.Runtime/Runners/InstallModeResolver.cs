using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Utilities;
using Instella.Core.FileSystem;
using Instella.Core.Installation;
using Instella.Core.Logging;
using Instella.Core.Platform;
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Runtime.Core;
using Instella.Installer.Runtime.Core.Transactions;

namespace Instella.Installer.Runtime.Runners;

/// <summary>The installer refuses to install at the chosen path; <see cref="ExitCode"/> says why.</summary>
internal sealed class InstallRefusedException(InstellaExitCode exitCode, string message) : Exception(message)
{
    public InstellaExitCode ExitCode { get; } = exitCode;
}

/// <summary>
/// Decides between first install, upgrade and repair once the install path is known
///. <see cref="ModeDispatcher"/> cannot: it runs before path resolution.
/// </summary>
internal static class InstallModeResolver
{
    /// <summary>
    /// Recovers any interrupted transaction at <paramref name="installPath"/>, then reads the
    /// installation there and returns the mode to run in.
    /// </summary>
    /// <exception cref="InstallRefusedException">The path holds another app, a newer version
    /// (without <c>--allow-downgrade</c>), or unrelated files (without <c>--force</c>).</exception>
    public static async Task<(InstallerMode Mode, InstalledManifest? Existing)> ResolveAsync(
        FrozenConfig config, IFileSystem fs, string installPath, DispatchResult dispatch, IInstellaLogger log,
        CancellationToken ct)
    {
        if (!fs.DirectoryExists(installPath))
            return (InstallerMode.FirstInstall, null);

        try
        {
            if (await InstallTransaction.RecoverAsync(fs, installPath, ct, log))
                log.Warn($"install: rolled back an interrupted install/update in '{installPath}'");
        }
        catch (UnsupportedJournalException ex)
        {
            throw new InstallRefusedException(InstellaExitCode.UpdateRollbackFailed, ex.Message);
        }

        var writer = new InstallManifestWriter(fs);
        var existing = await writer.ReadAsync(installPath, ct);
        if (existing is null)
        {
            var files = fs.EnumerateFiles(installPath, "*", recursive: true).ToList();
            if (files.Count == 0)
                return (InstallerMode.FirstInstall, null);
            if (!writer.Exists(installPath) && files.All(f => IsStateFile(installPath, f)))
            {
                // Only what an uninstall left in .instella/ (files still in use at the time,
                // the tombstone): not the user's files, so they do not make the folder "taken".
                var state = Path.Combine(installPath, InstellaOwnedPaths.StateDirectory);
                if (!(await fs.DeleteDirectoryAsync(state, recursive: true, ct)).Success)
                    log.Warn($"install: could not delete what the last uninstall left in '{state}'");
                return (InstallerMode.FirstInstall, null);
            }
            if (!dispatch.Force)
            {
                var why = writer.Exists(installPath)
                    ? "contains an Instella installation this installer cannot read"
                    : "is not empty and holds no Instella installation";
                throw new InstallRefusedException(InstellaExitCode.InstallGeneralFailure,
                    $"'{installPath}' {why}. Choose another folder, or pass --force to install into it anyway.");
            }
            log.Warn($"install: --force: installing into non-empty '{installPath}'");
            return (InstallerMode.FirstInstall, null);
        }

        if (Refusal(config, existing, installPath, dispatch) is { } refused)
            throw refused;

        if (AppVersions.Compare(config.AppVersion, existing.Version) > 0) return (InstallerMode.Upgrade, existing);
        if (AppVersions.Equal(config.AppVersion, existing.Version)) return (InstallerMode.Repair, existing);
        log.Warn($"install: --allow-downgrade: replacing v{existing.Version} with v{config.AppVersion}");
        return (InstallerMode.Upgrade, existing);
    }

    /// <summary>
    /// On an upgrade or repair, a shortcut the builder still offers defaults to what the
    /// existing installation has (an unticked shortcut stays gone). The rest follows the
    /// configuration; <c>remove-dropped-integrations</c> removes what it no longer asks for.
    /// </summary>
    public static InstallOptions FollowExisting(InstallOptions options, FrozenConfig config, InstalledManifest? existing) =>
        existing is null ? options : options with
        {
            CreateDesktopShortcut = (config.Shortcuts?.Desktop ?? false) && existing.HasDesktopShortcut,
            CreateStartMenuShortcut = (config.Shortcuts?.StartMenu ?? false) && existing.HasStartMenuShortcut,
        };

    /// <summary>
    /// The read-only part of <see cref="ResolveAsync"/>, for the wizard to report before the
    /// user has clicked through it: the refusal the installation at <paramref name="installPath"/>
    /// certainly causes (another app, or a newer version without <c>--allow-downgrade</c>), or
    /// null. Null too when an interrupted transaction is there, since its recovery can change
    /// the answer; <see cref="ResolveAsync"/> still decides at install time.
    /// </summary>
    public static async Task<InstallRefusedException?> PrecheckAsync(
        FrozenConfig config, IFileSystem fs, string installPath, DispatchResult dispatch, CancellationToken ct)
    {
        if (!fs.DirectoryExists(installPath)) return null;
        var txnDir = Path.Combine(installPath, TransactionJournal.TxnDirectory.Replace('/', Path.DirectorySeparatorChar));
        if (fs.DirectoryExists(txnDir)) return null;

        InstalledManifest? existing;
        try
        {
            existing = await new InstallManifestWriter(fs).ReadAsync(installPath, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;   // unreadable: ResolveAsync reports it
        }
        return existing is null ? null : Refusal(config, existing, installPath, dispatch);
    }

    /// <summary>
    /// The mode <see cref="ResolveAsync"/> will choose for <paramref name="installPath"/>, and
    /// the installation it will upgrade or repair, read without recovering or deleting anything,
    /// so the wizard can pick its pages before the user has clicked through it. Null when the
    /// folder would be refused (another app, a newer version, unrelated files, an unreadable
    /// manifest): <see cref="PrecheckAsync"/> or <see cref="ResolveAsync"/> reports that.
    /// </summary>
    public static async Task<(InstallerMode Mode, InstalledManifest? Existing)?> PredictModeAsync(
        FrozenConfig config, IFileSystem fs, string installPath, DispatchResult dispatch, CancellationToken ct)
    {
        if (!fs.DirectoryExists(installPath))
            return (InstallerMode.FirstInstall, null);

        var writer = new InstallManifestWriter(fs);
        InstalledManifest? existing;
        try
        {
            existing = await writer.ReadAsync(installPath, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
        if (existing is null)
        {
            // Empty apart from what an uninstall or an interrupted first install left in .instella/.
            if (!writer.Exists(installPath)
                && fs.EnumerateFiles(installPath, "*", recursive: true).All(f => IsStateFile(installPath, f)))
                return (InstallerMode.FirstInstall, null);
            return dispatch.Force ? (InstallerMode.FirstInstall, null) : null;
        }
        if (Refusal(config, existing, installPath, dispatch) is not null)
            return null;
        return AppVersions.Equal(config.AppVersion, existing.Version)
            ? (InstallerMode.Repair, existing)
            : (InstallerMode.Upgrade, existing);
    }

    private static InstallRefusedException? Refusal(
        FrozenConfig config, InstalledManifest existing, string installPath, DispatchResult dispatch)
    {
        if (!string.Equals(existing.AppId, config.AppId, StringComparison.Ordinal))
            return new InstallRefusedException(InstellaExitCode.InstallGeneralFailure,
                $"'{installPath}' contains a different application ({existing.AppId}).");
        if (AppVersions.Compare(config.AppVersion, existing.Version) < 0 && !dispatch.AllowDowngrade)
            return new InstallRefusedException(InstellaExitCode.UsageInvalidArgs,
                $"{existing.AppName} {existing.Version} is already installed in '{installPath}'. This installer has " +
                $"version {config.AppVersion}, which is older. Use --allow-downgrade to replace it.");
        return null;
    }

    private static bool IsStateFile(string installPath, string file) =>
        Path.GetRelativePath(installPath, file).Replace('\\', '/')
            .StartsWith(InstellaOwnedPaths.StateDirectory + "/", StringComparison.OrdinalIgnoreCase);
}
