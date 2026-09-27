using System.Collections.Generic;
using Instella.Installer.Runtime.UI.Widgets;
using Instella.Installer.Runtime.UI.Windows.Widgets;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.UI.Windows.Widgets;

[TestFixture]
public sealed class Win32LayoutEngineTests
{
    [Test]
    public void Compute_singleHeading_producesOneSlotInsidePadding()
    {
        var widgets = new List<Widget> { new Heading("Welcome") };
        var slots = Win32LayoutEngine.Compute(widgets, new PageState(), pageWidthPx: 400, dpi: 96);

        Assert.That(slots.Count, Is.EqualTo(1));
        Assert.That(slots[0].Widget, Is.InstanceOf<Heading>());
        Assert.That(slots[0].Left, Is.EqualTo(16)); // content padding at 96 DPI
        Assert.That(slots[0].Top, Is.EqualTo(16));
        Assert.That(slots[0].Width, Is.EqualTo(400 - 32));
        Assert.That(slots[0].Height, Is.EqualTo(40));
    }

    [Test]
    public void Compute_multipleWidgets_stacksVerticallyWithGap()
    {
        var widgets = new List<Widget>
        {
            new Heading("Hi"),
            new Paragraph("Short body"),
            new CheckBox("Accept"),
        };
        var slots = Win32LayoutEngine.Compute(widgets, new PageState(), pageWidthPx: 400, dpi: 96);

        Assert.That(slots.Count, Is.EqualTo(3));
        // Heading at y=16, height=40 → next starts at 16 + 40 + 10 = 66
        Assert.That(slots[1].Top, Is.EqualTo(66));
        // Paragraph height is line-based (~20 px for short text), next at 66 + 20 + 10 = 96
        Assert.That(slots[2].Top, Is.EqualTo(96));
    }

    [Test]
    public void Compute_withPageHeight_scrollableTextFillsTheSpareHeight()
    {
        // Hand test: the licence box stayed 160 px tall with half the page empty below it.
        var widgets = new List<Widget> { new Heading("Licence"), new ScrollableText("MIT"), new CheckBox("I accept") };

        var fixedSize = Win32LayoutEngine.Compute(widgets, new PageState(), pageWidthPx: 400, dpi: 96);
        var filled = Win32LayoutEngine.Compute(widgets, new PageState(), pageWidthPx: 400, dpi: 96, pageHeightPx: 400);

        Assert.That(fixedSize[1].Height, Is.EqualTo(160), "no page height: the design height");
        // 400 - padding 2*16 - gaps 2*10 - heading 40 - checkbox 22 = 286
        Assert.That(filled[1].Height, Is.EqualTo(286));
        Assert.That(filled[2].Top, Is.EqualTo(filled[1].Top + 286 + 10), "the checkbox follows the box");
        Assert.That(filled[2].Top + filled[2].Height, Is.EqualTo(400 - 16), "the stack ends at the bottom padding");
    }

    [Test]
    public void Compute_withPageHeight_leavesPagesWithoutScrollableTextAlone()
    {
        var widgets = new List<Widget> { new Heading("Hi"), new CheckBox("Accept") };

        var slots = Win32LayoutEngine.Compute(widgets, new PageState(), pageWidthPx: 400, dpi: 96, pageHeightPx: 400);

        Assert.That(slots[1].Top, Is.EqualTo(66));
        Assert.That(slots[1].Height, Is.EqualTo(22));
    }

    [Test]
    public void Compute_at192Dpi_approximatelyDoublesCoordinates()
    {
        var widgets = new List<Widget> { new Heading("Title") };
        var at96 = Win32LayoutEngine.Compute(widgets, new PageState(), pageWidthPx: 800, dpi: 96);
        var at192 = Win32LayoutEngine.Compute(widgets, new PageState(), pageWidthPx: 800, dpi: 192);

        // At 2x DPI, padding and height double.
        Assert.That(at192[0].Left, Is.EqualTo(at96[0].Left * 2));
        Assert.That(at192[0].Top, Is.EqualTo(at96[0].Top * 2));
        Assert.That(at192[0].Height, Is.EqualTo(at96[0].Height * 2));
    }

    [Test]
    public void Compute_hiddenWidget_excludedFromLayout()
    {
        var widgets = new List<Widget>
        {
            new Heading("Visible"),
            new Paragraph("Hidden") { Visible = _ => false },
            new CheckBox("Also visible"),
        };
        var slots = Win32LayoutEngine.Compute(widgets, new PageState(), pageWidthPx: 400, dpi: 96);

        Assert.That(slots.Count, Is.EqualTo(2));
        Assert.That(slots[0].Widget, Is.InstanceOf<Heading>());
        Assert.That(slots[1].Widget, Is.InstanceOf<CheckBox>());
    }

    [Test]
    public void ComputeIgnoringVisible_includesHiddenWidgets()
    {
        var widgets = new List<Widget>
        {
            new Heading("A"),
            new Paragraph("B") { Visible = _ => false },
            new CheckBox("C"),
        };
        var slots = Win32LayoutEngine.ComputeIgnoringVisible(widgets, pageWidthPx: 400, dpi: 96);

        Assert.That(slots.Count, Is.EqualTo(3));
        Assert.That(slots[1].Widget, Is.InstanceOf<Paragraph>());
    }

    [Test]
    public void Compute_radioGroup_heightScalesWithOptionCount()
    {
        var oneOption = Win32LayoutEngine.Compute(
            new List<Widget> { new RadioGroup("Pick", new[] { new RadioOption("a", "A") }) },
            new PageState(), pageWidthPx: 400, dpi: 96);
        var fourOptions = Win32LayoutEngine.Compute(
            new List<Widget>
            {
                new RadioGroup("Pick", new[]
                {
                    new RadioOption("a", "A"),
                    new RadioOption("b", "B"),
                    new RadioOption("c", "C"),
                    new RadioOption("d", "D"),
                }),
            },
            new PageState(), pageWidthPx: 400, dpi: 96);

        Assert.That(fourOptions[0].Height, Is.GreaterThan(oneOption[0].Height));
    }

    [Test]
    public void ComputeTotalHeight_addsTopAndBottomPadding()
    {
        var widgets = new List<Widget> { new Heading("X") };
        var total = Win32LayoutEngine.ComputeTotalHeight(widgets, new PageState(), contentWidthPx: 400, dpi: 96);

        // Two paddings (top + bottom, 16 each) + heading height (40) = 72.
        Assert.That(total, Is.EqualTo(72));
    }

    [Test]
    public void Scale_neverReturnsZero_forNonZeroInput()
    {
        // Below 96 DPI, 1 px would round to 0; guard against this to keep
        // borders visible. 10 DPI is absurd but exercises the floor.
        Assert.That(Win32DpiAware.Scale(1, 10), Is.EqualTo(1));
        Assert.That(Win32DpiAware.Scale(1, 96), Is.EqualTo(1));
    }

    [Test]
    public void Scale_zeroInput_returnsZero()
    {
        Assert.That(Win32DpiAware.Scale(0, 192), Is.EqualTo(0));
    }

    [Test]
    public void Scale_at144Dpi_scalesBy1Point5()
    {
        Assert.That(Win32DpiAware.Scale(10, 144), Is.EqualTo(15));
        Assert.That(Win32DpiAware.Scale(20, 144), Is.EqualTo(30));
    }
}
