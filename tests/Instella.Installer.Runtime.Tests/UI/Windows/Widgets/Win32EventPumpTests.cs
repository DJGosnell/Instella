using System;
using System.Runtime.Versioning;
using System.Threading;
using Instella.Installer.Runtime.UI.Widgets;
using Instella.Installer.Runtime.UI.Windows;
using Instella.Installer.Runtime.UI.Windows.Widgets;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.UI.Windows.Widgets;

[TestFixture]
[Apartment(ApartmentState.STA)]
[Platform("Win")]
public sealed class Win32EventPumpTests
{
    private nint _parent;
    private nint _hInstance;

    [OneTimeSetUp]
    [SupportedOSPlatform("windows")]
    public void FixtureSetUp()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("Windows-only tests.");
        (_parent, _hInstance) = Win32TestHost.CreateOffscreenParent();
    }

    [OneTimeTearDown]
    public void FixtureTearDown() => Win32TestHost.DestroyParent(_parent);

    [SetUp]
    public void SetUp()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("Windows-only tests.");
    }

    [SupportedOSPlatform("windows")]
    [Test]
    public void HandleCommand_checkBoxClicked_writesBoolToState()
    {
        var factory = new Win32WidgetFactory(_parent, _hInstance, 0, 0);
        var widget = new CheckBox("Accept", Default: false) { Id = "accept" };
        var state = new PageState();
        using var instance = factory.Create(widget, new Win32LayoutSlot(widget, 0, 0, 200, 22), state);

        // Programmatically check the box, then drive BN_CLICKED.
        Win32.SendMessageW(instance.PrimaryHwnd, BM.SETCHECK, (nuint)BST.CHECKED, 0);

        var bindings = new System.Collections.Generic.Dictionary<int, Win32ControlBinding>
        {
            [instance.PrimaryControlId] = new(instance, Win32ControlRole.Primary),
        };
        var pump = new Win32EventPump(bindings, state);

        var wParam = MakeCommandWParam(instance.PrimaryControlId, BN.CLICKED);
        Assert.That(pump.HandleCommand(wParam, instance.PrimaryHwnd), Is.True);
        Assert.That(state.Bool("accept"), Is.True);
    }

    [SupportedOSPlatform("windows")]
    [Test]
    public void HandleCommand_editChange_writesTextToState()
    {
        var factory = new Win32WidgetFactory(_parent, _hInstance, 0, 0);
        var widget = new TextInput("Name") { Id = "name" };
        var state = new PageState();
        using var instance = factory.Create(widget, new Win32LayoutSlot(widget, 0, 0, 200, 48), state);

        Win32.SetWindowTextW(instance.PrimaryHwnd, "Bob");

        var bindings = new System.Collections.Generic.Dictionary<int, Win32ControlBinding>
        {
            [instance.PrimaryControlId] = new(instance, Win32ControlRole.Primary),
        };
        var pump = new Win32EventPump(bindings, state);

        var wParam = MakeCommandWParam(instance.PrimaryControlId, EN.CHANGE);
        Assert.That(pump.HandleCommand(wParam, instance.PrimaryHwnd), Is.True);
        Assert.That(state.Text("name"), Is.EqualTo("Bob"));
    }

    [SupportedOSPlatform("windows")]
    [Test]
    public void HandleCommand_dropdownSelectionChange_writesOptionText()
    {
        var factory = new Win32WidgetFactory(_parent, _hInstance, 0, 0);
        var widget = new Dropdown("Lang", new[] { "en", "fr", "de" }) { Id = "lang" };
        var state = new PageState();
        using var instance = factory.Create(widget, new Win32LayoutSlot(widget, 0, 0, 200, 48), state);

        Win32.SendMessageW(instance.PrimaryHwnd, CB.SETCURSEL, 2, 0);

        var bindings = new System.Collections.Generic.Dictionary<int, Win32ControlBinding>
        {
            [instance.PrimaryControlId] = new(instance, Win32ControlRole.Primary),
        };
        var pump = new Win32EventPump(bindings, state);

        var wParam = MakeCommandWParam(instance.PrimaryControlId, CBN.SELCHANGE);
        Assert.That(pump.HandleCommand(wParam, instance.PrimaryHwnd), Is.True);
        Assert.That(state.Text("lang"), Is.EqualTo("de"));
    }

    [SupportedOSPlatform("windows")]
    [Test]
    public void HandleCommand_radioOptionClick_writesValueToState()
    {
        var factory = new Win32WidgetFactory(_parent, _hInstance, 0, 0);
        var widget = new RadioGroup("Flavour",
            new[] { new RadioOption("a", "A"), new RadioOption("b", "B") },
            Default: "a")
        { Id = "flavour" };
        var state = new PageState();
        using var instance = factory.Create(widget, new Win32LayoutSlot(widget, 0, 0, 300, 66), state);

        var bindings = new System.Collections.Generic.Dictionary<int, Win32ControlBinding>
        {
            [instance.PrimaryControlId] = new(instance, Win32ControlRole.Primary),
            [instance.RadioOptions[0].ControlId] = new(instance, Win32ControlRole.RadioOption, 0),
            [instance.RadioOptions[1].ControlId] = new(instance, Win32ControlRole.RadioOption, 1),
        };
        var pump = new Win32EventPump(bindings, state);

        var wParam = MakeCommandWParam(instance.RadioOptions[1].ControlId, BN.CLICKED);
        Assert.That(pump.HandleCommand(wParam, instance.RadioOptions[1].Hwnd), Is.True);
        Assert.That(state.Text("flavour"), Is.EqualTo("b"));
    }

    [SupportedOSPlatform("windows")]
    [Test]
    public void HandleCommand_browseButton_raisesBrowseRequested()
    {
        var factory = new Win32WidgetFactory(_parent, _hInstance, 0, 0);
        var widget = new FolderPicker("Install to") { Id = "path" };
        var state = new PageState();
        using var instance = factory.Create(widget, new Win32LayoutSlot(widget, 0, 0, 400, 50), state);
        Assume.That(instance.BrowseButtonControlId, Is.Not.Null);

        var bindings = new System.Collections.Generic.Dictionary<int, Win32ControlBinding>
        {
            [instance.PrimaryControlId] = new(instance, Win32ControlRole.Primary),
            [instance.BrowseButtonControlId!.Value] = new(instance, Win32ControlRole.Browse),
        };
        var pump = new Win32EventPump(bindings, state);

        Win32WidgetInstance? raised = null;
        pump.BrowseRequested += inst => raised = inst;

        var wParam = MakeCommandWParam(instance.BrowseButtonControlId.Value, BN.CLICKED);
        Assert.That(pump.HandleCommand(wParam, 0), Is.True);
        Assert.That(raised, Is.SameAs(instance));
    }

    [SupportedOSPlatform("windows")]
    [Test]
    public void HandleCommand_unknownControlId_returnsFalse()
    {
        var bindings = new System.Collections.Generic.Dictionary<int, Win32ControlBinding>();
        var pump = new Win32EventPump(bindings, new PageState());

        Assert.That(pump.HandleCommand(MakeCommandWParam(99999, BN.CLICKED), 0), Is.False);
    }

    private static nuint MakeCommandWParam(int controlId, int notifCode)
        => (nuint)((notifCode << 16) | (controlId & 0xFFFF));
}
