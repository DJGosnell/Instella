using System;
using System.Diagnostics;
using System.IO;
using Instella.Core.Logging;

namespace Instella.Installer.Runtime.Core.Processes;

/// <summary>Starts the installed app after an update or a wizard install.</summary>
internal interface IAppLauncher
{
    /// <summary>Starts <paramref name="exePath"/>, never with this process's admin rights.</summary>
    void Launch(string exePath, string workingDirectory);
}

/// <summary>
/// Starts the app without elevation. The updater and the wizard run elevated for machine-wide
/// installs; a child they start directly would inherit the admin token. Through Explorer, which
/// runs as the signed-in user, the app starts unelevated. Arguments can't be passed that way, so
/// none are passed on any path: the post-update marker carries them instead.
/// </summary>
internal sealed class AppLauncher(IInstellaLogger log) : IAppLauncher
{
    public void Launch(string exePath, string workingDirectory)
    {
        var dropElevation = OperatingSystem.IsWindows()
            && ShouldDropElevation(Environment.IsPrivilegedProcess, TokenElevation.Current());
        var psi = BuildStartInfo(exePath, workingDirectory, dropElevation);
        log.Info($"launching {exePath}{(psi.FileName == exePath ? "" : " through Explorer (not elevated)")}");
        try
        {
            Process.Start(psi)?.Dispose();
        }
        catch (Exception ex)
        {
            log.Warn($"could not start {exePath}: {ex.Message}");
        }
    }

    /// <summary>
    /// Whether the app must be started through Explorer to shed this process's admin rights. Only
    /// the elevated half of a UAC token pair has an unelevated token for the same user to fall back
    /// to. With UAC off, or as the built-in Administrator, every process of the user is elevated:
    /// there is nothing to drop to, and Explorer may not even be running (a service session, a
    /// headless build machine), in which case the Explorer route would start nothing at all.
    /// </summary>
    internal static bool ShouldDropElevation(bool privileged, TokenElevationType elevationType) =>
        privileged && elevationType == TokenElevationType.Full;

    /// <summary>The start info <see cref="Launch"/> uses; <paramref name="dropElevation"/> selects the Explorer route.</summary>
    internal static ProcessStartInfo BuildStartInfo(string exePath, string workingDirectory, bool dropElevation)
    {
        if (dropElevation)
        {
            // The absolute path: a bare "explorer.exe" goes through a search path.
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            var viaExplorer = new ProcessStartInfo(explorer) { UseShellExecute = false };
            viaExplorer.ArgumentList.Add(exePath);
            return viaExplorer;
        }
        return new ProcessStartInfo(exePath) { UseShellExecute = false, WorkingDirectory = workingDirectory };
    }
}
