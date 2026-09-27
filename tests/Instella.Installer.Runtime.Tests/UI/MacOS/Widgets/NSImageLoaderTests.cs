using System;
using System.Runtime.Versioning;
using Instella.Installer.Runtime.UI.MacOS.Widgets;
using Instella.Installer.Runtime.UI.Widgets;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.UI.MacOS.Widgets;

[TestFixture]
[Platform("MacOsX")]
public sealed class NSImageLoaderTests
{
    // Same tiny 1x1 PNG used by other loader tests.
    private const string TinyPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

    [SetUp]
    public void SetUp()
    {
        if (!OperatingSystem.IsMacOS()) Assert.Ignore("macOS-only tests.");
    }

    [SupportedOSPlatform("macos")]
    [Test]
    public void Load_fromBytes_returnsNonZeroImage()
    {
        var bytes = Convert.FromBase64String(TinyPngBase64);
        var image = NSImageLoader.Load(ImageSource.FromBytes(bytes));

        Assert.That(image, Is.Not.EqualTo((nint)0));
        Instella.Installer.Runtime.UI.MacOS.NS.Release(image);
    }

    [SupportedOSPlatform("macos")]
    [Test]
    public void Load_fromEmptyBytes_returnsZero()
    {
        Assert.That(NSImageLoader.Load(ImageSource.FromBytes(Array.Empty<byte>())), Is.EqualTo((nint)0));
    }

    [SupportedOSPlatform("macos")]
    [Test]
    public void Load_fromMissingFile_returnsZero()
    {
        Assert.That(NSImageLoader.Load(ImageSource.FromFile("/tmp/instella-nonexistent.png")), Is.EqualTo((nint)0));
    }
}
