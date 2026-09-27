using System.Text;
using Instella.Core.Installation;
using Instella.Installer.Runtime.Builders;

namespace Instella.Installer.Runtime.Runners;

/// <summary>
/// Pure stdout-report builders for the headless preview modes (<c>manage</c>,
/// <c>cleanup</c>). Factored out of <see cref="PreviewModeRunner"/> so tests
/// can pin exact wording without driving a full runner.
/// </summary>
internal static class PreviewReport
{
    public static string BuildManageReport(FrozenConfig config)
    {
        var sb = new StringBuilder();
        sb.Append("preview[manage]: ").Append(config.AppName).Append(" v").Append(config.AppVersion).AppendLine();
        sb.AppendLine("preview[manage]: (no install detected — synthesized report)");
        sb.AppendLine("preview[manage]: actions in the real runner would include reading the sibling manifest,");
        sb.AppendLine("preview[manage]: reporting install date, file count, and per-user vs. system-wide scope.");
        return sb.ToString();
    }

    public static string BuildCleanupReport(FrozenConfig config)
    {
        var sb = new StringBuilder();
        sb.Append("preview[cleanup]: ").Append(config.AppName).Append(" v").Append(config.AppVersion).AppendLine();
        sb.AppendLine("preview[cleanup]: simulated best-effort sweep (no real files touched)");
        sb.AppendLine("preview[cleanup]: real runner retries up to 6 times with 1s/2s/4s/8s/16s backoff");
        sb.AppendLine("preview[cleanup]: deletes tracked files, removes empty subdirectories bottom-up,");
        sb.AppendLine("preview[cleanup]: then removes the install directory itself");
        return sb.ToString();
    }

    /// <summary>
    /// Human-readable description of which simulated step sequence
    /// <see cref="PreviewModeRunner"/> will run for a given mode.
    /// </summary>
    public static string DescribeModeSteps(InstallerMode mode) => mode switch
    {
        InstallerMode.FirstInstall or InstallerMode.Upgrade or InstallerMode.Repair =>
            "prerequisites → stage payload → shortcuts → file associations → PATH → auto-start → stage uninstaller → register Installed-Apps entry → stage manifest → commit",
        InstallerMode.Update =>
            "verify signed release → stage changed files (patch or download) → commit → finalize",
        InstallerMode.Uninstall =>
            "remove shortcuts → remove file associations → remove PATH entry → remove auto-start → unregister → delete files",
        InstallerMode.Manage => "report status",
        InstallerMode.Cleanup => "retry-sweep install directory",
        InstallerMode.Recover => "finish or undo an interrupted update",
        _ => "(unknown mode)",
    };
}
