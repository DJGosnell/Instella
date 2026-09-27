namespace Instella.Core.Platform;

/// <summary>
/// Information used to register an "Installed Apps" (a.k.a. Add/Remove
/// Programs) entry on Windows. Written to the per-user or per-machine
/// Uninstall registry key depending on <see cref="PerUser"/>.
/// </summary>
/// <param name="AppId">Unique application identifier; used as the registry subkey name.</param>
/// <param name="DisplayName">Name shown in the Installed Apps list.</param>
/// <param name="DisplayVersion">Version string (e.g. "1.2.0").</param>
/// <param name="Publisher">Publisher / vendor display name.</param>
/// <param name="InstallLocation">Absolute path to the installation directory.</param>
/// <param name="DisplayIcon">Absolute path to the icon to show (usually the installed main executable, or a .ico file).</param>
/// <param name="UninstallCommand">Full command line (already quoted) that runs the uninstaller, e.g. <c>"C:\Program Files\MyApp\uninstall.exe" --uninstall --silent</c>.</param>
/// <param name="UrlInfoAbout">Optional homepage URL.</param>
/// <param name="EstimatedSizeKb">Estimated install size in kilobytes. Shown in Installed Apps.</param>
/// <param name="PerUser">When true, register under HKCU (per-user); when false, under HKLM (system-wide).</param>
public sealed record UninstallEntryInfo(
    string AppId,
    string DisplayName,
    string DisplayVersion,
    string Publisher,
    string InstallLocation,
    string DisplayIcon,
    string UninstallCommand,
    string? UrlInfoAbout,
    int EstimatedSizeKb,
    bool PerUser);

/// <summary>Where Windows keeps an app's Installed Apps entry, under HKCU or HKLM.</summary>
internal static class UninstallEntryKeys
{
    public static string PathFor(string appId) => $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{appId}";
}
