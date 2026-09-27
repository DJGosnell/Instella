using Instella.Installer.Runtime.UI.Linux.Widgets;
using Instella.Installer.Runtime.UI.Widgets;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.UI.Linux.Widgets;

/// <summary>
/// Exercises the pure-managed state-write helpers on
/// <see cref="GtkEventPump"/> without calling into libgtk. The
/// <c>Wire</c> path that actually connects GTK signals is covered by
/// integration tests on Linux CI only.
/// </summary>
[TestFixture]
public sealed class GtkEventPumpTests
{
    [Test]
    public void WriteCheckBox_writesBoolToState()
    {
        var state = new PageState();
        var pump = new GtkEventPump(state);
        var widget = new CheckBox("Accept") { Id = "accept" };
        var instance = new GtkWidgetInstance(widget, primaryWidget: 0, rootWidget: 0);

        pump.WriteCheckBox(instance, isActive: true);
        Assert.That(state.Bool("accept"), Is.True);

        pump.WriteCheckBox(instance, isActive: false);
        Assert.That(state.Bool("accept"), Is.False);
    }

    [Test]
    public void WriteCheckBox_onWidgetWithoutId_isNoOp()
    {
        var state = new PageState();
        var pump = new GtkEventPump(state);
        var widget = new CheckBox("Accept"); // no Id
        var instance = new GtkWidgetInstance(widget, 0, 0);

        Assert.DoesNotThrow(() => pump.WriteCheckBox(instance, isActive: true));
        Assert.That(state.Snapshot().Count, Is.EqualTo(0));
    }

    [Test]
    public void WriteEntryText_writesStringToState_forTextInput()
    {
        var state = new PageState();
        var pump = new GtkEventPump(state);
        var widget = new TextInput("Name") { Id = "name" };
        var instance = new GtkWidgetInstance(widget, 0, 0);

        pump.WriteEntryText(instance, "Bob");
        Assert.That(state.Text("name"), Is.EqualTo("Bob"));
    }

    [Test]
    public void WriteEntryText_writesToState_forFolderPicker()
    {
        var state = new PageState();
        var pump = new GtkEventPump(state);
        var widget = new FolderPicker("Install to") { Id = "path" };
        var instance = new GtkWidgetInstance(widget, 0, 0);

        pump.WriteEntryText(instance, "/opt/app");
        Assert.That(state.Text("path"), Is.EqualTo("/opt/app"));
    }

    [Test]
    public void WriteDropdownSelection_writesSelectedOption()
    {
        var state = new PageState();
        var pump = new GtkEventPump(state);
        var widget = new Dropdown("Lang", new[] { "en", "fr", "de" }) { Id = "lang" };
        var instance = new GtkWidgetInstance(widget, 0, 0);

        pump.WriteDropdownSelection(instance, selectedIndex: 2);
        Assert.That(state.Text("lang"), Is.EqualTo("de"));
    }

    [Test]
    public void WriteDropdownSelection_outOfRange_writesNull()
    {
        var state = new PageState();
        var pump = new GtkEventPump(state);
        var widget = new Dropdown("Lang", new[] { "en" }) { Id = "lang" };
        var instance = new GtkWidgetInstance(widget, 0, 0);

        pump.WriteDropdownSelection(instance, selectedIndex: -1);
        // Per PageState contract: stored null is observable via TryGet<T>
        // for reference/nullable T — the key is present, the value is null.
        Assert.That(state.TryGet<string>("lang", out var value), Is.True);
        Assert.That(value, Is.Null);
    }

    [Test]
    public void WriteRadioSelection_writesOptionValue()
    {
        var state = new PageState();
        var pump = new GtkEventPump(state);
        var widget = new RadioGroup("Flavour",
            new[] { new RadioOption("a", "A"), new RadioOption("b", "B") }) { Id = "flavour" };
        var bindings = new[] { new GtkRadioBinding(0, "a"), new GtkRadioBinding(0, "b") };
        var instance = new GtkWidgetInstance(widget, 0, 0, radioOptions: bindings);

        pump.WriteRadioSelection(instance, optionIndex: 1);
        Assert.That(state.Text("flavour"), Is.EqualTo("b"));
    }

    [Test]
    public void WriteRadioSelection_outOfRange_isNoOp()
    {
        var state = new PageState();
        var pump = new GtkEventPump(state);
        var widget = new RadioGroup("X", new[] { new RadioOption("a", "A") }) { Id = "x" };
        var bindings = new[] { new GtkRadioBinding(0, "a") };
        var instance = new GtkWidgetInstance(widget, 0, 0, radioOptions: bindings);

        Assert.DoesNotThrow(() => pump.WriteRadioSelection(instance, optionIndex: 99));
        // State should remain as whatever the default was (unset here).
        Assert.That(state.TryGet<string>("x", out _), Is.False);
    }

    [Test]
    public void RaiseBrowseRequested_invokesSubscribers()
    {
        var state = new PageState();
        var pump = new GtkEventPump(state);
        var widget = new FolderPicker("Install") { Id = "path" };
        var instance = new GtkWidgetInstance(widget, 0, 0);

        GtkWidgetInstance? raised = null;
        pump.BrowseRequested += inst => raised = inst;
        pump.RaiseBrowseRequested(instance);

        Assert.That(raised, Is.SameAs(instance));
    }
}
