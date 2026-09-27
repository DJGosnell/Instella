using Instella.Installer.Runtime.UI.MacOS.Widgets;
using Instella.Installer.Runtime.UI.Widgets;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.UI.MacOS.Widgets;

[TestFixture]
public sealed class CocoaWidgetInstanceTests
{
    [Test]
    public void Properties_exposeConstructorArgs()
    {
        var widget = new FolderPicker("Install to") { Id = "path" };
        var bindings = new[] { new CocoaRadioBinding(100, "a") };

        var instance = new CocoaWidgetInstance(
            widget,
            primaryView: 1,
            rootView: 2,
            browseButtonView: 3,
            radioOptions: bindings,
            image: 4);

        Assert.That(instance.Widget, Is.SameAs(widget));
        Assert.That(instance.PrimaryView, Is.EqualTo((nint)1));
        Assert.That(instance.RootView, Is.EqualTo((nint)2));
        Assert.That(instance.BrowseButtonView, Is.EqualTo((nint)3));
        Assert.That(instance.RadioOptions, Is.EquivalentTo(bindings));
        Assert.That(instance.Image, Is.EqualTo((nint)4));
    }

    [Test]
    public void Dispose_isIdempotent_withEmptyLists()
    {
        var instance = new CocoaWidgetInstance(new Heading("Hi"), 0, 0);
        instance.Dispose();
        Assert.DoesNotThrow(() => instance.Dispose());
    }

    [Test]
    public void Default_radioOptions_isEmpty()
    {
        var instance = new CocoaWidgetInstance(new Paragraph("Body"), 0, 0);
        Assert.That(instance.RadioOptions.Count, Is.EqualTo(0));
        Assert.That(instance.BrowseButtonView, Is.EqualTo((nint)0));
        Assert.That(instance.Image, Is.EqualTo((nint)0));
    }

    [Test]
    public void DelegateTargets_startsEmpty_isMutable()
    {
        var instance = new CocoaWidgetInstance(new CheckBox("C") { Id = "c" }, 0, 0);
        Assert.That(instance.DelegateTargets.Count, Is.EqualTo(0));
        instance.DelegateTargets.Add(123);
        Assert.That(instance.DelegateTargets.Count, Is.EqualTo(1));
        instance.Dispose();
    }
}
