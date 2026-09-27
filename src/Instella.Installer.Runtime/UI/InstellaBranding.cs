using System.IO;
using Instella.Installer.Runtime.UI.Widgets;

namespace Instella.Installer.Runtime.UI;

/// <summary>
/// The Instella logo, embedded in this assembly from <c>assets/logo/</c>: the Welcome page's
/// default brand image, and the window icon of an installer whose exe has no icon of its own.
/// </summary>
internal static class InstellaBranding
{
    internal const string LogoResource = "Instella.Installer.Runtime.Branding.logo.png";
    internal const string IconResource = "Instella.Installer.Runtime.Branding.instella.ico";

    /// <summary>The logo (256 px PNG with transparency), scaled down by the renderer.</summary>
    internal static ImageSource Logo { get; } =
        ImageSource.FromResource(LogoResource, typeof(InstellaBranding).Assembly);

    /// <summary>The multi-size .ico (16 to 256 px), or null if the resource is missing.</summary>
    internal static byte[]? ReadIcon()
    {
        using var stream = typeof(InstellaBranding).Assembly.GetManifestResourceStream(IconResource);
        if (stream is null) return null;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }
}
