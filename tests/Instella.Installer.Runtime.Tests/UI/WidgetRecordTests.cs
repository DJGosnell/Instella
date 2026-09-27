using System;
using Instella.Installer.Runtime.UI.Widgets;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.UI;

[TestFixture]
public sealed class WidgetRecordTests
{
    [Test]
    public void Heading_equality_basedOnText()
    {
        var a = new Heading("Welcome");
        var b = new Heading("Welcome");
        Assert.That(a, Is.EqualTo(b));
    }

    [Test]
    public void Heading_inequality_whenTextDiffers()
    {
        var a = new Heading("Welcome");
        var b = new Heading("Hello");
        Assert.That(a, Is.Not.EqualTo(b));
    }

    [Test]
    public void TextInput_withExpression_updatesLabel()
    {
        var original = new TextInput("Name") { Id = "full-name" };
        var updated = original with { Label = "Full name" };
        Assert.That(updated.Label, Is.EqualTo("Full name"));
        Assert.That(updated.Id, Is.EqualTo("full-name"));
        Assert.That(original.Label, Is.EqualTo("Name")); // records are immutable
    }

    [Test]
    public void Widget_Visible_isOptional()
    {
        var w = new Heading("Static");
        Assert.That(w.Visible, Is.Null);
        Assert.That(w.Enabled, Is.Null);
    }

    [Test]
    public void Widget_Visible_predicateRunsAgainstPageState()
    {
        var state = new PageState();
        state.Set("show", true);

        var w = new Paragraph("Body") { Visible = s => s.Bool("show") };
        Assert.That(w.Visible!(state), Is.True);

        state.Set("show", false);
        Assert.That(w.Visible!(state), Is.False);
    }

    [Test]
    public void BrandImage_defaultMaxHeight_is96()
    {
        var w = new BrandImage(ImageSource.FromFile("logo.png"));
        Assert.That(w.MaxHeight, Is.EqualTo(96));
    }

    [Test]
    public void BrandImage_customMaxHeight_isStored()
    {
        var w = new BrandImage(ImageSource.FromFile("logo.png"), MaxHeight: 128);
        Assert.That(w.MaxHeight, Is.EqualTo(128));
    }

    [Test]
    public void RadioGroup_carriesOptionsList()
    {
        var w = new RadioGroup("Edition", new[]
        {
            new RadioOption("free", "Free"),
            new RadioOption("pro", "Pro"),
        });
        Assert.That(w.Options.Count, Is.EqualTo(2));
        Assert.That(w.Options[1].Label, Is.EqualTo("Pro"));
    }

    [Test]
    public void FileFilter_carriesExtensions()
    {
        var filter = new FileFilter("Images", new[] { "png", "jpg" });
        Assert.That(filter.Extensions, Is.EqualTo(new[] { "png", "jpg" }));
    }

    [Test]
    public void ProgressAndStatusLine_needNoConstructorArgs()
    {
        Assert.DoesNotThrow(() => { _ = new Progress(); _ = new StatusLine(); });
    }
}
