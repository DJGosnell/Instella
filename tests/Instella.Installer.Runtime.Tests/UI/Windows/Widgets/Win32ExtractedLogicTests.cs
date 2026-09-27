using System.Linq;
using Instella.Installer.Runtime.UI.Windows;
using Instella.Installer.Runtime.UI.Windows.Widgets;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.UI.Windows.Widgets;

/// <summary>
/// Covers the pure helpers extracted from the
/// native Win32 widget host so the accelerator-entry mapping and the progress
/// clamp/scale are verified without a live window. The remaining native surface
/// (message loop, DPI rebuild, icon extraction) stays integration-tested.
/// </summary>
[TestFixture]
public sealed class Win32ExtractedLogicTests
{
    [Test]
    public void BuildAcceleratorEntries_fourAltMnemonics_finishSharesContinue()
    {
        var accels = Win32WidgetHost.BuildAcceleratorEntries();

        Assert.That(accels, Has.Length.EqualTo(4));

        var expectedVirt = (byte)(FVIRT.VIRTKEY | FVIRT.ALT);
        Assert.That(accels.Select(a => a.fVirt), Is.All.EqualTo(expectedVirt));

        // Alt+C / Alt+B / Alt+N / Alt+F in order.
        Assert.That(accels.Select(a => a.key).ToArray(),
            Is.EqualTo(new ushort[] { VK.C, VK.B, VK.N, VK.F }));

        // Finish (Alt+F) routes to the same control id as Continue (Alt+N).
        Assert.That(accels[3].cmd, Is.EqualTo(accels[2].cmd));
        // Cancel / Back / Continue are three distinct control ids.
        Assert.That(new[] { accels[0].cmd, accels[1].cmd, accels[2].cmd }.Distinct().Count(),
            Is.EqualTo(3));
    }

    [TestCase(0.0, 0)]
    [TestCase(1.0, 1000)]
    [TestCase(0.5, 500)]
    [TestCase(0.25, 250)]
    [TestCase(-0.3, 0)]
    [TestCase(2.0, 1000)]
    public void ProgressBarPosition_clampsAndScales(double fraction, int expected)
    {
        Assert.That(Win32PagePanel.ProgressBarPosition(fraction), Is.EqualTo(expected));
    }
}
