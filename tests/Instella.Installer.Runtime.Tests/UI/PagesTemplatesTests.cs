using Instella.Installer.Runtime.Builders;
using Instella.Installer.Runtime.UI.Widgets;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.UI;

[TestFixture]
public sealed class PagesTemplatesTests
{
    [Test]
    public void License_producesHeadingScrollableTextCheckBox_withContinueWhen()
    {
        var pb = new PageBuilder("license");
        Pages.License("the license text")(pb);
        var spec = pb.Build();

        Assert.That(spec.Widgets[0], Is.InstanceOf<Heading>());
        Assert.That(spec.Widgets[1], Is.InstanceOf<ScrollableText>());
        Assert.That(spec.Widgets[2], Is.InstanceOf<CheckBox>());
        Assert.That(spec.ContinueWhen, Is.Not.Null);

        var state = new PageState();
        Assert.That(spec.ContinueWhen!(state), Is.False);
        state.Set("accepted", true);
        Assert.That(spec.ContinueWhen!(state), Is.True);
    }

    [Test]
    public void Destination_producesHeadingAndFolderPicker_withPathValidation()
    {
        var pb = new PageBuilder("destination");
        Pages.Destination("C:\\Apps\\Demo")(pb);
        var spec = pb.Build();

        Assert.That(spec.Widgets[0], Is.InstanceOf<Heading>());
        Assert.That(spec.Widgets[1], Is.InstanceOf<FolderPicker>());

        var state = new PageState();
        Assert.That(spec.ContinueWhen!(state), Is.False);
        state.Set("path", "C:\\apps\\myapp");
        Assert.That(spec.ContinueWhen!(state), Is.True);
    }

    [Test]
    public void ReleaseNotes_hasNoContinueWhen()
    {
        var pb = new PageBuilder("rn");
        Pages.ReleaseNotes("v1.0 — first release")(pb);
        var spec = pb.Build();

        Assert.That(spec.Widgets[0], Is.InstanceOf<Heading>());
        Assert.That(spec.Widgets[1], Is.InstanceOf<ScrollableText>());
        Assert.That(spec.ContinueWhen, Is.Null);
    }
}
