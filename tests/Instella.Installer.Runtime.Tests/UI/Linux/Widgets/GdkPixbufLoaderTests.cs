using System;
using System.Runtime.Versioning;
using Instella.Installer.Runtime.UI.Linux.Widgets;
using Instella.Installer.Runtime.UI.Widgets;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.UI.Linux.Widgets;

[TestFixture]
[Platform("Linux")]
public sealed class GdkPixbufLoaderTests
{
    // Same tiny 1x1 PNG used by the Win32 image loader tests.
    private const string TinyPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

    [SetUp]
    public void SetUp()
    {
        if (!OperatingSystem.IsLinux()) Assert.Ignore("Linux-only tests.");
    }

    [SupportedOSPlatform("linux")]
    [Test]
    public void Load_fromBytes_returnsNonZeroPixbuf()
    {
        var bytes = Convert.FromBase64String(TinyPngBase64);
        var pixbuf = GdkPixbufLoader.Load(ImageSource.FromBytes(bytes));

        Assert.That(pixbuf, Is.Not.EqualTo((nint)0));

        // Width/height are 1 for the sample.
        Assert.That(Instella.Installer.Runtime.UI.Linux.Gtk.gdk_pixbuf_get_width(pixbuf), Is.EqualTo(1));
        Assert.That(Instella.Installer.Runtime.UI.Linux.Gtk.gdk_pixbuf_get_height(pixbuf), Is.EqualTo(1));

        // Release the pixbuf ref that Load handed us.
        Instella.Installer.Runtime.UI.Linux.Gtk.g_object_unref(pixbuf);
    }

    [SupportedOSPlatform("linux")]
    [Test]
    public void Load_fromEmptyBytes_returnsZero()
    {
        Assert.That(GdkPixbufLoader.Load(ImageSource.FromBytes(Array.Empty<byte>())), Is.EqualTo((nint)0));
    }

    [SupportedOSPlatform("linux")]
    [Test]
    public void Load_fromMissingFile_returnsZero()
    {
        Assert.That(GdkPixbufLoader.Load(ImageSource.FromFile("/tmp/instella-nonexistent.png")), Is.EqualTo((nint)0));
    }
}
