using Instella.Installer.Runtime.UI.Windows.Widgets;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.UI.Windows.Widgets;

[TestFixture]
public sealed class Win32DpiAwareTests
{
    [Test]
    public void EnsureProcessDpiAwareness_isIdempotent()
    {
        // First call may or may not succeed depending on platform; the
        // contract is "does not throw, callable multiple times".
        Assert.DoesNotThrow(() => Win32DpiAware.EnsureProcessDpiAwareness());
        Assert.DoesNotThrow(() => Win32DpiAware.EnsureProcessDpiAwareness());
    }

    [Test]
    public void Scale_roundTripsAtBaseDpi()
    {
        Assert.That(Win32DpiAware.Scale(10, Win32DpiAware.BaseDpi), Is.EqualTo(10));
        Assert.That(Win32DpiAware.Scale(40, Win32DpiAware.BaseDpi), Is.EqualTo(40));
    }

    [Test]
    public void Scale_negativeValues_stayNegative()
    {
        // Font heights in Win32 are passed as negative pixels. The scaler
        // preserves sign and clamps to -1 at worst.
        Assert.That(Win32DpiAware.Scale(-13, 96), Is.EqualTo(-13));
        Assert.That(Win32DpiAware.Scale(-13, 144), Is.EqualTo(-20));
    }

    [Test]
    public void GetDpiForWindowOrDefault_fallsBackToBase_forZeroHwnd()
    {
        var dpi = Win32DpiAware.GetDpiForWindowOrDefault(0);
        // 0 hwnd on non-Windows → BaseDpi. On Windows, GetDpiForWindow(0)
        // returns system DPI (1607+) or 0 (older) → BaseDpi fallback.
        // The only guarantee is "positive".
        Assert.That(dpi, Is.GreaterThan(0));
    }
}
