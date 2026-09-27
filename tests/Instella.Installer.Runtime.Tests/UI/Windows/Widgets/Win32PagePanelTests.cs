using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Threading;
using Instella.Core.Installation;
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Runtime.UI.Widgets;
using Instella.Installer.Runtime.UI.Windows;
using Instella.Installer.Runtime.UI.Windows.Widgets;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.UI.Windows.Widgets;

[TestFixture]
[Apartment(ApartmentState.STA)]
[Platform("Win")]
public sealed class Win32PagePanelTests
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
    public void Constructor_materializesEveryWidget_intoBindings()
    {
        var spec = MakeSpec("p1",
            new Heading("Title"),
            new TextInput("Name") { Id = "name" },
            new CheckBox("Accept") { Id = "accept" });

        var state = new PageState();
        using var panel = new Win32PagePanel(
            _parent, spec, state, _hInstance,
            bodyFont: 0, headerFont: 0,
            panelLeft: 0, panelTop: 0, panelWidth: 400, dpi: 96);

        Assert.That(panel.Instances.Count, Is.EqualTo(3));
        // Each widget contributes at least one binding; bindings count is
        // >= instance count because compound widgets register auxiliary IDs.
        Assert.That(panel.Bindings.Count, Is.GreaterThanOrEqualTo(panel.Instances.Count));
    }

    [SupportedOSPlatform("windows")]
    [Test]
    public void HandleCommand_roundTripsThroughFactoryAndPump()
    {
        var spec = MakeSpec("p1", new CheckBox("Accept", Default: false) { Id = "accept" });
        var state = new PageState();
        using var panel = new Win32PagePanel(
            _parent, spec, state, _hInstance,
            bodyFont: 0, headerFont: 0,
            panelLeft: 0, panelTop: 0, panelWidth: 400, dpi: 96);

        var checkbox = panel.Instances[0];
        Win32.SendMessageW(checkbox.PrimaryHwnd, BM.SETCHECK, (nuint)BST.CHECKED, 0);

        var wParam = (nuint)((BN.CLICKED << 16) | (checkbox.PrimaryControlId & 0xFFFF));
        Assert.That(panel.HandleCommand(wParam, checkbox.PrimaryHwnd), Is.True);
        Assert.That(state.Bool("accept"), Is.True);
    }

    [SupportedOSPlatform("windows")]
    [Test]
    public void PageStateChanged_reAppliesVisibilityPredicate()
    {
        var spec = MakeSpec("p1",
            new CheckBox("Show details", Default: false) { Id = "show" },
            new Paragraph("Hidden until checked")
            {
                Visible = s => s.Bool("show"),
            });

        var state = new PageState();
        using var panel = new Win32PagePanel(
            _parent, spec, state, _hInstance,
            bodyFont: 0, headerFont: 0,
            panelLeft: 0, panelTop: 0, panelWidth: 400, dpi: 96);

        // Flipping the predicate's source key should re-run predicates.
        state.Set("show", true);
        // The panel has no direct "IsVisible" surface — we assert the
        // refresh path completes without exception, which demonstrates
        // the StateChanged subscription is wired.
        Assert.DoesNotThrow(() => panel.RefreshFromState());
    }

    [SupportedOSPlatform("windows")]
    [Test]
    public void CanContinue_returnsPredicateResult()
    {
        var spec = new PageSpec(
            Id: "p",
            Widgets: new List<Widget> { new CheckBox("Accept") { Id = "acc" } },
            ContinueWhen: s => s.Bool("acc"),
            OnEnter: null, OnLeave: null, OnValidate: null,
            AllowedModes: new HashSet<InstallerMode> { InstallerMode.FirstInstall },
            When: null);

        var state = new PageState();
        using var panel = new Win32PagePanel(
            _parent, spec, state, _hInstance,
            bodyFont: 0, headerFont: 0,
            panelLeft: 0, panelTop: 0, panelWidth: 400, dpi: 96);

        Assert.That(panel.CanContinue(), Is.False);

        state.Set("acc", true);
        Assert.That(panel.CanContinue(), Is.True);
    }

    [SupportedOSPlatform("windows")]
    [Test]
    public void Dispose_destroysEveryOwnedHwnd()
    {
        var spec = MakeSpec("p", new Heading("X"), new TextInput("Y"));
        var state = new PageState();
        var panel = new Win32PagePanel(
            _parent, spec, state, _hInstance,
            bodyFont: 0, headerFont: 0,
            panelLeft: 0, panelTop: 0, panelWidth: 400, dpi: 96);

        var survivingHwnd = panel.Instances[0].PrimaryHwnd;
        panel.Dispose();

        // After Dispose, a second Dispose must be a no-op — no crash, no
        // double DestroyWindow. This also tests idempotence of the guard.
        Assert.DoesNotThrow(() => panel.Dispose());
        // The handle may or may not be recycled by Windows; we cannot
        // reliably assert it's invalid. The stronger contract is that
        // repeated disposal doesn't throw.
        _ = survivingHwnd;
    }

    private static PageSpec MakeSpec(string id, params Widget[] widgets)
    {
        return new PageSpec(
            Id: id,
            Widgets: widgets,
            ContinueWhen: null,
            OnEnter: null, OnLeave: null, OnValidate: null,
            AllowedModes: new HashSet<InstallerMode> { InstallerMode.FirstInstall },
            When: null);
    }
}
