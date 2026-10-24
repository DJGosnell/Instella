using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using Instella.Installer.Runtime.UI.Widgets;
using Instella.Installer.Runtime.UI.Windows;
using Instella.Installer.Runtime.UI.Windows.Widgets;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.UI.Windows.Widgets;

/// <summary>
/// <see cref="PasswordInput"/> renders as a label and a Win32 EDIT with <c>ES_PASSWORD</c>, and
/// writes nothing to <see cref="PageState"/>.
/// </summary>
[TestFixture]
[Apartment(ApartmentState.STA)]
[Platform("Win")]
public sealed partial class Win32PasswordInputTests
{
    private const int GWL_STYLE = -16;
    private const uint ES_PASSWORD = 0x0020;
    private nint _parent;
    private nint _hInstance;

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static partial int GetWindowLong(nint hWnd, int index);

    [OneTimeSetUp]
    [SupportedOSPlatform("windows")]
    public void FixtureSetUp()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("Windows-only tests.");
        (_parent, _hInstance) = Win32TestHost.CreateOffscreenParent();
    }

    [OneTimeTearDown]
    public void FixtureTearDown() => Win32TestHost.DestroyParent(_parent);

    [SupportedOSPlatform("windows")]
    [Test]
    public void Create_passwordInput_producesLabelAndMaskedEdit_AndLeavesPageStateEmpty()
    {
        var factory = new Win32WidgetFactory(_parent, _hInstance, 0, 0);
        var widget = new PasswordInput("Password:") { Id = "pw" };
        var slot = new Win32LayoutSlot(widget, 0, 0, 300, 48);
        var state = new PageState();

        using var instance = factory.Create(widget, slot, state);

        Assert.That(instance.AllHwnds.Count, Is.EqualTo(2), "a label and an EDIT");
        var style = (uint)GetWindowLong(instance.PrimaryHwnd, GWL_STYLE);
        Assert.That(style & ES_PASSWORD, Is.EqualTo(ES_PASSWORD), "the EDIT masks what is typed");
        Assert.That(state.Snapshot(), Is.Empty, "a password field writes nothing to PageState");
    }
}
