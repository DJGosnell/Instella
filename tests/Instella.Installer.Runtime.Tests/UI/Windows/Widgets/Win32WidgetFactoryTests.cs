using System;
using System.Runtime.InteropServices;
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
public sealed class Win32WidgetFactoryTests
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
    public void Create_heading_producesSingleStaticWithRightText()
    {
        var factory = new Win32WidgetFactory(_parent, _hInstance, 0, 0);
        var slot = new Win32LayoutSlot(new Heading("Hi there"), 0, 0, 200, 40);
        using var instance = factory.Create(slot.Widget, slot, new PageState());

        Assert.That(instance.AllHwnds.Count, Is.EqualTo(1));
        Assert.That(instance.PrimaryHwnd, Is.Not.EqualTo((nint)0));
        Assert.That(Win32.GetWindowText(instance.PrimaryHwnd), Is.EqualTo("Hi there"));
    }

    [SupportedOSPlatform("windows")]
    [Test]
    public void Create_textInput_producesLabelAndEdit()
    {
        var factory = new Win32WidgetFactory(_parent, _hInstance, 0, 0);
        var widget = new TextInput("Name", Default: "Alice") { Id = "name" };
        var slot = new Win32LayoutSlot(widget, 0, 0, 300, 48);
        var state = new PageState();

        using var instance = factory.Create(widget, slot, state);

        Assert.That(instance.AllHwnds.Count, Is.EqualTo(2));
        Assert.That(Win32.GetWindowText(instance.PrimaryHwnd), Is.EqualTo("Alice"));
        Assert.That(state.Text("name"), Is.EqualTo("Alice"));
    }

    [SupportedOSPlatform("windows")]
    [Test]
    public void Create_checkBox_writesDefaultToState()
    {
        var factory = new Win32WidgetFactory(_parent, _hInstance, 0, 0);
        var widget = new CheckBox("Accept", Default: true) { Id = "accept" };
        var slot = new Win32LayoutSlot(widget, 0, 0, 200, 22);
        var state = new PageState();

        using var instance = factory.Create(widget, slot, state);

        Assert.That(state.Bool("accept"), Is.True);
        var checkState = (int)Win32.SendMessageW(instance.PrimaryHwnd, BM.GETCHECK, 0, 0);
        Assert.That(checkState, Is.EqualTo(BST.CHECKED));
    }

    [SupportedOSPlatform("windows")]
    [Test]
    public void Create_radioGroup_producesLabelPlusOptionButtons()
    {
        var factory = new Win32WidgetFactory(_parent, _hInstance, 0, 0);
        var widget = new RadioGroup(
            "Flavour",
            new[] { new RadioOption("a", "Vanilla"), new RadioOption("b", "Chocolate") },
            Default: "b")
        { Id = "flavour" };
        var slot = new Win32LayoutSlot(widget, 0, 0, 300, 66);
        var state = new PageState();

        using var instance = factory.Create(widget, slot, state);

        Assert.That(instance.RadioOptions.Count, Is.EqualTo(2));
        Assert.That(instance.AllHwnds.Count, Is.EqualTo(3)); // label + 2 radios
        Assert.That(state.Text("flavour"), Is.EqualTo("b"));

        // Second radio should be checked per default.
        var checkedState = (int)Win32.SendMessageW(instance.RadioOptions[1].Hwnd, BM.GETCHECK, 0, 0);
        Assert.That(checkedState, Is.EqualTo(BST.CHECKED));
    }

    [SupportedOSPlatform("windows")]
    [Test]
    public void Create_dropdown_selectsDefaultOption()
    {
        var factory = new Win32WidgetFactory(_parent, _hInstance, 0, 0);
        var widget = new Dropdown("Lang", new[] { "en", "fr", "de" }, Default: "fr") { Id = "lang" };
        var slot = new Win32LayoutSlot(widget, 0, 0, 300, 48);
        var state = new PageState();

        using var instance = factory.Create(widget, slot, state);

        var selected = (int)Win32.SendMessageW(instance.PrimaryHwnd, CB.GETCURSEL, 0, 0);
        Assert.That(selected, Is.EqualTo(1));
        Assert.That(state.Text("lang"), Is.EqualTo("fr"));
    }

    [SupportedOSPlatform("windows")]
    [Test]
    public void Create_folderPicker_allocatesBrowseButton()
    {
        var factory = new Win32WidgetFactory(_parent, _hInstance, 0, 0);
        var widget = new FolderPicker("Install to", Default: @"C:\App") { Id = "path" };
        var slot = new Win32LayoutSlot(widget, 0, 0, 400, 50);
        var state = new PageState();

        using var instance = factory.Create(widget, slot, state);

        Assert.That(instance.BrowseButtonControlId, Is.Not.Null);
        Assert.That(instance.AllHwnds.Count, Is.EqualTo(3)); // label + edit + browse
        Assert.That(Win32.GetWindowText(instance.PrimaryHwnd), Is.EqualTo(@"C:\App"));
        Assert.That(state.Text("path"), Is.EqualTo(@"C:\App"));
    }

    [SupportedOSPlatform("windows")]
    [Test]
    public void Create_progress_producesProgressBar()
    {
        var factory = new Win32WidgetFactory(_parent, _hInstance, 0, 0);
        var widget = new Progress();
        var slot = new Win32LayoutSlot(widget, 0, 0, 300, 22);

        using var instance = factory.Create(widget, slot, new PageState());

        Assert.That(instance.PrimaryHwnd, Is.Not.EqualTo((nint)0));
    }

    [SupportedOSPlatform("windows")]
    [Test]
    public void Create_allocatesSequentialControlIds()
    {
        var factory = new Win32WidgetFactory(_parent, _hInstance, 0, 0, firstControlId: 2000);
        var heading = factory.Create(new Heading("A"), new Win32LayoutSlot(new Heading("A"), 0, 0, 100, 30), new PageState());
        var textInput = factory.Create(new TextInput("B"), new Win32LayoutSlot(new TextInput("B"), 0, 50, 100, 48), new PageState());

        try
        {
            Assert.That(heading.PrimaryControlId, Is.EqualTo(2000));
            // TextInput consumes two IDs (label at 2001, edit at 2002).
            Assert.That(textInput.PrimaryControlId, Is.EqualTo(2002));
            Assert.That(factory.PeekNextControlId, Is.EqualTo(2003));
        }
        finally
        {
            heading.Dispose();
            textInput.Dispose();
        }
    }
}
