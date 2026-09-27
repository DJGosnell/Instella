using System;
using System.Runtime.Versioning;
using System.Threading;
using Instella.Installer.Runtime.UI.Widgets;
using Instella.Installer.Runtime.UI.Windows.Widgets;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.UI.Windows.Widgets;

[TestFixture]
[Apartment(ApartmentState.STA)]
[Platform("Win")]
public sealed class GdiPlusImageLoaderTests
{
    // Tiny valid 1x1 RGBA PNG — the smallest we can hand to GDI+ without
    // fighting a codec. Hard-coded (not a fixture file) so tests don't
    // depend on filesystem content. 70 bytes.
    private const string TinyPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

    [SetUp]
    public void SetUp()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("Windows-only tests.");
    }

    [SupportedOSPlatform("windows")]
    [Test]
    public void Load_fromBytes_producesBitmapWithExpectedDimensions()
    {
        var bytes = Convert.FromBase64String(TinyPngBase64);
        using var bitmap = GdiPlusImageLoader.Load(ImageSource.FromBytes(bytes));

        Assert.That(bitmap, Is.Not.Null);
        Assert.That(bitmap!.Width, Is.EqualTo(1));
        Assert.That(bitmap.Height, Is.EqualTo(1));
        Assert.That(bitmap.Handle, Is.Not.EqualTo((nint)0));
    }

    [SupportedOSPlatform("windows")]
    [Test]
    public void Load_fromEmptyBytes_returnsNull()
    {
        using var bitmap = GdiPlusImageLoader.Load(ImageSource.FromBytes(Array.Empty<byte>()));
        Assert.That(bitmap, Is.Null);
    }

    [SupportedOSPlatform("windows")]
    [Test]
    public void Load_fromGarbageBytes_returnsNull()
    {
        using var bitmap = GdiPlusImageLoader.Load(ImageSource.FromBytes(new byte[] { 1, 2, 3, 4 }));
        Assert.That(bitmap, Is.Null);
    }

    [SupportedOSPlatform("windows")]
    [Test]
    public void Load_fromMissingFile_returnsNull()
    {
        using var bitmap = GdiPlusImageLoader.Load(ImageSource.FromFile(@"C:\nonexistent\instella-test.png"));
        Assert.That(bitmap, Is.Null);
    }

    [SupportedOSPlatform("windows")]
    [Test]
    public void Load_concurrentCalls_succeed()
    {
        // Exercises the refcounted GDI+ startup/shutdown path. If the
        // ref-count was off, one loader would tear down the library
        // underneath another; both should succeed.
        var bytes = Convert.FromBase64String(TinyPngBase64);
        Win32Bitmap? a = null, b = null;
        Exception? failure = null;
        var t1 = new Thread(() => { try { a = GdiPlusImageLoader.Load(ImageSource.FromBytes(bytes)); } catch (Exception ex) { failure = ex; } });
        var t2 = new Thread(() => { try { b = GdiPlusImageLoader.Load(ImageSource.FromBytes(bytes)); } catch (Exception ex) { failure = ex; } });
        t1.SetApartmentState(ApartmentState.STA);
        t2.SetApartmentState(ApartmentState.STA);
        t1.Start(); t2.Start();
        t1.Join(); t2.Join();

        Assert.That(failure, Is.Null);
        Assert.That(a, Is.Not.Null);
        Assert.That(b, Is.Not.Null);
        a?.Dispose();
        b?.Dispose();
    }
}
