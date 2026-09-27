using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Threading;
using Instella.Core.Installation;
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Runtime.UI.Widgets;
using Instella.Installer.Runtime.UI.Windows.Widgets;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.UI.Windows.Widgets;

/// <summary>
/// Pins that <see cref="Win32WidgetHost.IsPreview"/> drives the
/// banner-HWND child + decorated window title. Windows-only; gated via
/// <see cref="Platform"/>.
/// </summary>
[TestFixture]
[Apartment(ApartmentState.STA)]
[Platform("Win")]
public sealed class Win32WidgetHostPreviewTests
{
    [SetUp]
    public void SetUp()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("Windows-only tests.");
    }

    [SupportedOSPlatform("windows")]
    [Test]
    public void IsPreviewFalse_producesNoBannerHwnd()
    {
        using var host = new Win32WidgetHost(
            "Installer",
            new[] { MakeSpec("p") },
            new[] { new PageState() });
        host.CreateHeadless();

        Assert.That(host.PreviewBannerHwnd, Is.EqualTo((nint)0));
    }

    [SupportedOSPlatform("windows")]
    [Test]
    public void IsPreviewTrue_createsBannerHwnd()
    {
        using var host = new Win32WidgetHost(
            "Installer",
            new[] { MakeSpec("p") },
            new[] { new PageState() })
        {
            IsPreview = true,
        };
        host.CreateHeadless();

        Assert.That(host.PreviewBannerHwnd, Is.Not.EqualTo((nint)0));
    }

    [SupportedOSPlatform("windows")]
    [Test]
    public void IsPreviewTrue_windowTitleHasPreviewSuffix()
    {
        using var host = new Win32WidgetHost(
            "Installer",
            new[] { MakeSpec("p") },
            new[] { new PageState() })
        {
            IsPreview = true,
        };
        host.CreateHeadless();

        // Read the window text via GetWindowTextW.
        var buf = new char[128];
        int len;
        unsafe
        {
            fixed (char* p = buf)
                len = Win32Probe.GetWindowTextW(host.Hwnd, (nint)p, buf.Length);
        }
        var title = new string(buf, 0, len);
        Assert.That(title, Does.EndWith("PREVIEW"));
    }

    private static PageSpec MakeSpec(string id)
    {
        return new PageSpec(
            Id: id,
            Widgets: new Widget[] { new Heading("Preview") },
            ContinueWhen: null,
            OnEnter: null, OnLeave: null, OnValidate: null,
            AllowedModes: new HashSet<InstallerMode> { InstallerMode.FirstInstall },
            When: null);
    }

    // Test-only import of user32!GetWindowTextW — avoids taking a dependency
    // on the runtime's internal Win32 P/Invoke class.
    internal static class Win32Probe
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, ExactSpelling = true)]
        public static extern int GetWindowTextW(nint hWnd, nint lpString, int nMaxCount);
    }
}
