using System.Collections.Generic;
using Instella.Installer.Runtime.UI.Linux.Widgets;
using Instella.Installer.Runtime.UI.Widgets;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.UI.Linux.Widgets;

[TestFixture]
public sealed class GtkLayoutEngineTests
{
    [Test]
    public void DescribeLayout_preservesWidgetOrder()
    {
        var widgets = new List<Widget>
        {
            new Heading("First"),
            new Paragraph("Second"),
            new CheckBox("Third"),
        };

        var slots = GtkLayoutEngine.DescribeLayout(widgets, new PageState());

        Assert.That(slots.Count, Is.EqualTo(3));
        Assert.That(slots[0].Widget, Is.InstanceOf<Heading>());
        Assert.That(slots[1].Widget, Is.InstanceOf<Paragraph>());
        Assert.That(slots[2].Widget, Is.InstanceOf<CheckBox>());
        Assert.That(slots[0].Index, Is.EqualTo(0));
        Assert.That(slots[2].Index, Is.EqualTo(2));
    }

    [Test]
    public void DescribeLayout_flagsCompoundWidgets()
    {
        var widgets = new List<Widget>
        {
            new Heading("H"),
            new TextInput("Name"),
            new FolderPicker("Install to"),
            new RadioGroup("Flavour", new[] { new RadioOption("a", "A") }),
            new Dropdown("Lang", new[] { "en" }),
            new CheckBox("Accept"),
        };

        var slots = GtkLayoutEngine.DescribeLayout(widgets, new PageState());

        Assert.That(slots[0].IsCompound, Is.False);     // Heading
        Assert.That(slots[1].IsCompound, Is.True);      // TextInput
        Assert.That(slots[2].IsCompound, Is.True);      // FolderPicker
        Assert.That(slots[3].IsCompound, Is.True);      // RadioGroup
        Assert.That(slots[4].IsCompound, Is.True);      // Dropdown
        Assert.That(slots[5].IsCompound, Is.False);     // CheckBox
    }

    [Test]
    public void DescribeLayout_reportsVisibilityPerWidget()
    {
        var widgets = new List<Widget>
        {
            new Heading("Shown"),
            new Paragraph("Hidden") { Visible = _ => false },
            new CheckBox("Shown too"),
        };

        var slots = GtkLayoutEngine.DescribeLayout(widgets, new PageState());

        Assert.That(slots[0].Visible, Is.True);
        Assert.That(slots[1].Visible, Is.False);
        Assert.That(slots[2].Visible, Is.True);
        // Unlike Win32, hidden widgets still appear in the slot list
        // because GTK reflows automatically when set_visible toggles.
        Assert.That(slots.Count, Is.EqualTo(3));
    }

    [Test]
    public void LayoutConstants_matchDesignIntent()
    {
        // These numbers feed directly into gtk_widget_set_margin_* and
        // gtk_box_new(..., spacing). Changes are visible in rendered
        // output, so tests pin them.
        Assert.That(GtkLayoutEngine.ContentPadding, Is.EqualTo(16));
        Assert.That(GtkLayoutEngine.VerticalSpacing, Is.EqualTo(10));
        Assert.That(GtkLayoutEngine.LabelToControlSpacing, Is.EqualTo(4));
        Assert.That(GtkLayoutEngine.HorizontalRowSpacing, Is.EqualTo(6));
        Assert.That(GtkLayoutEngine.ScrollableTextMinHeight, Is.EqualTo(160));
    }

    [Test]
    public void IsCompound_classifiesEveryConcreteWidget()
    {
        // Every widget record in the closed hierarchy must be classified
        // deterministically so the factory doesn't drop one silently.
        Assert.That(GtkLayoutEngine.IsCompound(new Heading("")), Is.False);
        Assert.That(GtkLayoutEngine.IsCompound(new Paragraph("")), Is.False);
        Assert.That(GtkLayoutEngine.IsCompound(new ScrollableText("")), Is.False);
        Assert.That(GtkLayoutEngine.IsCompound(new CheckBox("")), Is.False);
        Assert.That(GtkLayoutEngine.IsCompound(new Progress()), Is.False);
        Assert.That(GtkLayoutEngine.IsCompound(new StatusLine()), Is.False);
        Assert.That(GtkLayoutEngine.IsCompound(new TextInput("")), Is.True);
        Assert.That(GtkLayoutEngine.IsCompound(new Dropdown("", new[] { "x" })), Is.True);
        Assert.That(GtkLayoutEngine.IsCompound(new FolderPicker("")), Is.True);
        Assert.That(GtkLayoutEngine.IsCompound(new FilePicker("", new[] { new FileFilter("all", new[] { "*" }) })), Is.True);
        Assert.That(GtkLayoutEngine.IsCompound(new RadioGroup("", new[] { new RadioOption("a", "A") })), Is.True);
    }
}
