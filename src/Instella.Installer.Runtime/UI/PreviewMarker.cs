namespace Instella.Installer.Runtime.UI;

/// <summary>
/// Shared constants used by every widget host when <c>IsPreview=true</c>.
/// Keeps the visual marker (window-title suffix + above-page banner text)
/// identical on Win32, GTK, and Cocoa so reviewers get the same "preview mode"
/// signal regardless of platform.
/// </summary>
internal static class PreviewMarker
{
    /// <summary>Appended to the window title when the host is rendering a preview.</summary>
    public const string TitleSuffix = " — PREVIEW";

    /// <summary>Single-line banner rendered above the page panel.</summary>
    public const string BannerText = "Preview mode — no changes will be made";

    /// <summary>Banner height in design units (DPI-scaled by the Win32 host, device-independent elsewhere).</summary>
    public const int BannerHeight = 24;

    /// <summary>Apply <see cref="TitleSuffix"/> to <paramref name="baseTitle"/>.</summary>
    public static string DecorateTitle(string baseTitle, bool isPreview)
        => isPreview ? baseTitle + TitleSuffix : baseTitle;
}
