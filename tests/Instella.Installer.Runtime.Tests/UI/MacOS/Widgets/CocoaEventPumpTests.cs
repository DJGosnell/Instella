using Instella.Installer.Runtime.UI.MacOS.Widgets;
using Instella.Installer.Runtime.UI.Widgets;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.UI.MacOS.Widgets;

/// <summary>
/// Exercises the pure-managed state-write helpers on
/// <see cref="CocoaEventPump"/> without calling into AppKit. The
/// target/action wiring and ObjC-runtime class registration are
/// covered on Apple CI only.
/// </summary>
[TestFixture]
public sealed class CocoaEventPumpTests
{
    [Test]
    public void WriteCheckBox_writesBoolToState()
    {
        var state = new PageState();
        var pump = new CocoaEventPump(state);
        var widget = new CheckBox("Accept") { Id = "accept" };
        var instance = new CocoaWidgetInstance(widget, primaryView: 0, rootView: 0);

        pump.WriteCheckBox(instance, isChecked: true);
        Assert.That(state.Bool("accept"), Is.True);
    }

    [Test]
    public void WriteEditText_writesTextForTextInput()
    {
        var state = new PageState();
        var pump = new CocoaEventPump(state);
        var widget = new TextInput("Name") { Id = "name" };
        var instance = new CocoaWidgetInstance(widget, 0, 0);

        pump.WriteEditText(instance, "Ada");
        Assert.That(state.Text("name"), Is.EqualTo("Ada"));
    }

    [Test]
    public void WriteEditText_writesTextForFolderPicker()
    {
        var state = new PageState();
        var pump = new CocoaEventPump(state);
        var widget = new FolderPicker("Install to") { Id = "path" };
        var instance = new CocoaWidgetInstance(widget, 0, 0);

        pump.WriteEditText(instance, "/Applications");
        Assert.That(state.Text("path"), Is.EqualTo("/Applications"));
    }

    [Test]
    public void WriteDropdownSelection_writesSelectedOption()
    {
        var state = new PageState();
        var pump = new CocoaEventPump(state);
        var widget = new Dropdown("Lang", new[] { "en", "fr", "de" }) { Id = "lang" };
        var instance = new CocoaWidgetInstance(widget, 0, 0);

        pump.WriteDropdownSelection(instance, selectedIndex: 2);
        Assert.That(state.Text("lang"), Is.EqualTo("de"));
    }

    [Test]
    public void WriteDropdownSelection_outOfRange_writesNull()
    {
        var state = new PageState();
        var pump = new CocoaEventPump(state);
        var widget = new Dropdown("Lang", new[] { "en" }) { Id = "lang" };
        var instance = new CocoaWidgetInstance(widget, 0, 0);

        pump.WriteDropdownSelection(instance, -1);
        Assert.That(state.TryGet<string>("lang", out var value), Is.True);
        Assert.That(value, Is.Null);
    }

    [Test]
    public void WriteRadioSelection_writesOptionValue()
    {
        var state = new PageState();
        var pump = new CocoaEventPump(state);
        var widget = new RadioGroup("Pick",
            new[] { new RadioOption("a", "A"), new RadioOption("b", "B") }) { Id = "pick" };
        var bindings = new[] { new CocoaRadioBinding(0, "a"), new CocoaRadioBinding(0, "b") };
        var instance = new CocoaWidgetInstance(widget, 0, 0, radioOptions: bindings);

        pump.WriteRadioSelection(instance, optionIndex: 1);
        Assert.That(state.Text("pick"), Is.EqualTo("b"));
    }

    [Test]
    public void RaiseBrowseRequested_invokesSubscribers()
    {
        var state = new PageState();
        var pump = new CocoaEventPump(state);
        var widget = new FolderPicker("P") { Id = "p" };
        var instance = new CocoaWidgetInstance(widget, 0, 0);

        CocoaWidgetInstance? raised = null;
        pump.BrowseRequested += inst => raised = inst;
        pump.RaiseBrowseRequested(instance);

        Assert.That(raised, Is.SameAs(instance));
    }

    [Test]
    public void UnregisterControl_isSafeForZeroPointer()
    {
        var pump = new CocoaEventPump(new PageState());
        Assert.DoesNotThrow(() => pump.UnregisterControl(0));
    }
}
