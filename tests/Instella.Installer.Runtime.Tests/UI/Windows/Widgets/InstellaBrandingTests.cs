using System;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Runtime.Runners;
using Instella.Installer.Runtime.UI;
using Instella.Installer.Runtime.UI.Widgets;
using Instella.Installer.Runtime.UI.Windows;
using Instella.Installer.Runtime.UI.Windows.Widgets;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.UI.Windows.Widgets;

/// <summary>The Instella logo: embedded resources, the fallback window icon and the Welcome brand image.</summary>
[TestFixture]
public sealed class InstellaBrandingTests
{
    [Test]
    public void TheIconAndLogo_AreEmbedded()
    {
        Assert.That(InstellaBranding.ReadIcon(), Is.Not.Null.And.Not.Empty);
        using var logo = typeof(InstellaBranding).Assembly.GetManifestResourceStream(InstellaBranding.LogoResource);
        Assert.That(logo, Is.Not.Null);
    }

    [TestCase(16, 16)]
    [TestCase(30, 32)]
    [TestCase(32, 32)]
    [TestCase(200, 256)]
    [TestCase(512, 256)]
    public void SelectImage_PicksTheSmallestAtLeastAsBig_ElseTheBiggest(int size, int expected)
    {
        var ico = InstellaBranding.ReadIcon()!;

        var image = Win32Branding.SelectImage(ico, size);

        Assert.That(image, Is.Not.Null);
        var entry = Enumerable.Range(0, BitConverter.ToUInt16(ico, 4))
            .Select(i => (Width: ico[6 + (16 * i)] == 0 ? 256 : ico[6 + (16 * i)], Offset: BitConverter.ToInt32(ico, 6 + (16 * i) + 12)))
            .Single(e => e.Offset == image!.Value.Offset);
        Assert.That(entry.Width, Is.EqualTo(expected));
    }

    [Test]
    public void SelectImage_RefusesAMalformedFile()
    {
        Assert.That(Win32Branding.SelectImage([], 32), Is.Null);
        Assert.That(Win32Branding.SelectImage([0, 0, 2, 0, 1, 0], 32), Is.Null, "a cursor, not an icon");
        var truncated = InstellaBranding.ReadIcon()!.AsSpan(0, 200).ToArray();
        Assert.That(Win32Branding.SelectImage(truncated, 32), Is.Null, "an image beyond the end of the file");
    }

    [Test]
    [Platform("Win")]
    [SupportedOSPlatform("windows")]
    public void FallbackIcon_IsCreated()
    {
        var icon = Win32Branding.CreateFallbackIcon(32);

        Assert.That(icon, Is.Not.EqualTo((nint)0));
        Win32.DestroyIcon(icon);
    }

    [TestCase(256, 256, 64, 64, 64, 64)]
    [TestCase(200, 100, 64, 64, 64, 32)]
    [TestCase(32, 32, 64, 64, 32, 32)]
    [TestCase(256, 256, 0, 0, 256, 256)]
    public void FitWithin_ScalesDownOnly_KeepingTheAspectRatio(int w, int h, int maxW, int maxH, int expectedW, int expectedH)
    {
        Assert.That(GdiPlusImageLoader.FitWithin(w, h, maxW, maxH), Is.EqualTo((expectedW, expectedH)));
    }

    [Test]
    [Platform("Win")]
    [Apartment(ApartmentState.STA)]
    [SupportedOSPlatform("windows")]
    public void Logo_LoadsScaledToTheSlot()
    {
        // A STATIC control crops an image bigger than its slot: the 256 px logo must be scaled.
        using var bitmap = GdiPlusImageLoader.Load(InstellaBranding.Logo, 500, 64, Win32Branding.DialogBackgroundArgb());

        Assert.That(bitmap, Is.Not.Null);
        Assert.That((bitmap!.Width, bitmap.Height), Is.EqualTo((64, 64)));
    }

    [Test]
    public void WelcomePage_ShowsTheInstellaLogo_ByDefault()
    {
        var page = InteractivePages.BuildWelcomePage(Config(b => { }));

        var image = page.Widgets.OfType<BrandImage>().Single();
        Assert.That(page.Widgets[0], Is.SameAs(image), "the logo is at the top");
        Assert.That(image.Source, Is.SameAs(InstellaBranding.Logo));
        Assert.That(image.MaxHeight, Is.EqualTo(InteractivePages.WelcomeBrandImageHeight));
    }

    [Test]
    public void WelcomePage_ShowsTheAuthorsImage_OrNone()
    {
        var own = ImageSource.FromBytes([1, 2, 3]);

        var custom = InteractivePages.BuildWelcomePage(Config(b => b.WithBrandImage(own)));
        var none = InteractivePages.BuildWelcomePage(Config(b => b.WithBrandImage(null)));

        Assert.That(custom.Widgets.OfType<BrandImage>().Single().Source, Is.SameAs(own));
        Assert.That(none.Widgets.OfType<BrandImage>(), Is.Empty);
    }

    private static FrozenConfig Config(Action<InstallerBuilder> configure)
    {
        var b = new InstallerBuilder();
        b.WithApp("Brand Test", "com.example.brand", new Version(1, 0, 0));
        configure(b);
        return ((InstellaInstallerImpl)b.Build()).ConfigForTests;
    }
}
