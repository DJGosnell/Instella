using Instella.Installer.Runtime.UI;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.UI;

/// <summary>
/// Platform-free tests for the shared preview-marker constants.
/// Platform-specific host banner tests live under <c>UI/Windows/</c>,
/// <c>UI/Linux/</c>, and <c>UI/MacOS/</c>.
/// </summary>
[TestFixture]
public sealed class PreviewMarkerTests
{
    [Test]
    public void DecorateTitle_appendsSuffix_whenPreview()
        => Assert.That(PreviewMarker.DecorateTitle("Installer", isPreview: true),
            Is.EqualTo("Installer" + PreviewMarker.TitleSuffix));

    [Test]
    public void DecorateTitle_returnsInput_whenNotPreview()
        => Assert.That(PreviewMarker.DecorateTitle("Installer", isPreview: false),
            Is.EqualTo("Installer"));

    [Test]
    public void BannerText_isHumanReadable()
    {
        Assert.That(PreviewMarker.BannerText, Does.Contain("Preview"));
        Assert.That(PreviewMarker.BannerText, Does.Contain("no changes"));
    }

    [Test]
    public void BannerHeight_isSmallEnough_toNotObscureContent()
    {
        Assert.That(PreviewMarker.BannerHeight, Is.GreaterThan(0));
        Assert.That(PreviewMarker.BannerHeight, Is.LessThanOrEqualTo(48));
    }
}
