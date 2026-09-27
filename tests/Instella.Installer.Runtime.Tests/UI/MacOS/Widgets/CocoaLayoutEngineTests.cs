using System.Collections.Generic;
using Instella.Installer.Runtime.UI.MacOS.Widgets;
using Instella.Installer.Runtime.UI.Widgets;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.UI.MacOS.Widgets;

[TestFixture]
public sealed class CocoaLayoutEngineTests
{
    [Test]
    public void Compute_singleHeading_producesOneSlotInsidePadding()
    {
        var widgets = new List<Widget> { new Heading("Welcome") };
        var slots = CocoaLayoutEngine.Compute(widgets, new PageState(), pageWidthPx: 400);

        Assert.That(slots.Count, Is.EqualTo(1));
        Assert.That(slots[0].Left, Is.EqualTo(16));
        Assert.That(slots[0].Y, Is.EqualTo(16));
        Assert.That(slots[0].Width, Is.EqualTo(368));
        Assert.That(slots[0].Height, Is.EqualTo(40));
    }

    [Test]
    public void Compute_multipleWidgets_stackWithGap()
    {
        var widgets = new List<Widget>
        {
            new Heading("Hi"),
            new CheckBox("Accept"),
            new CheckBox("Also"),
        };
        var slots = CocoaLayoutEngine.Compute(widgets, new PageState(), 400);

        Assert.That(slots.Count, Is.EqualTo(3));
        Assert.That(slots[1].Y, Is.EqualTo(16 + 40 + 10)); // padding + heading + gap
        Assert.That(slots[2].Y, Is.EqualTo(slots[1].Y + 22 + 10));
    }

    [Test]
    public void Compute_hiddenWidget_excludedFromLayout()
    {
        var widgets = new List<Widget>
        {
            new Heading("A"),
            new Paragraph("Hidden") { Visible = _ => false },
            new CheckBox("C"),
        };
        var slots = CocoaLayoutEngine.Compute(widgets, new PageState(), 400);

        Assert.That(slots.Count, Is.EqualTo(2));
    }

    [Test]
    public void ComputeIgnoringVisible_includesHiddenWidgets()
    {
        var widgets = new List<Widget>
        {
            new Heading("A"),
            new Paragraph("Hidden") { Visible = _ => false },
            new CheckBox("C"),
        };
        var slots = CocoaLayoutEngine.ComputeIgnoringVisible(widgets, 400);

        Assert.That(slots.Count, Is.EqualTo(3));
    }

    [Test]
    public void FlipY_invertsTopOriginToBottomOrigin()
    {
        // Top-origin Y=16, slot height 40, panel height 400.
        // Bottom-origin Y should be 400 - 16 - 40 = 344.
        Assert.That(CocoaLayoutEngine.FlipY(16, 40, 400), Is.EqualTo(344));
    }

    [Test]
    public void FlipY_isInvolution()
    {
        const int panelHeight = 500;
        const int slotHeight = 22;

        for (int topY = 0; topY <= panelHeight - slotHeight; topY += 37)
        {
            var bottom = CocoaLayoutEngine.FlipY(topY, slotHeight, panelHeight);
            var flipped = CocoaLayoutEngine.FlipY(bottom, slotHeight, panelHeight);
            Assert.That(flipped, Is.EqualTo(topY));
        }
    }

    [Test]
    public void ComputeTotalHeight_addsPaddingAndGaps()
    {
        var widgets = new List<Widget> { new Heading("X"), new CheckBox("Y") };
        var total = CocoaLayoutEngine.ComputeTotalHeight(widgets, new PageState(), 400);

        // 16 + 40 + 10 + 22 + 16 = 104.
        Assert.That(total, Is.EqualTo(104));
    }
}
