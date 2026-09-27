using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Threading;
using Instella.Core.Installation;
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Runtime.UI.Widgets;
using Instella.Installer.Runtime.UI.Windows.Widgets;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.UI.Windows.Widgets;

[TestFixture]
[Apartment(ApartmentState.STA)]
[Platform("Win")]
public sealed class Win32WidgetHostTests
{
    [SetUp]
    public void SetUp()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("Windows-only tests.");
    }

    [SupportedOSPlatform("windows")]
    [Test]
    public void CreateHeadless_producesLiveWindow_thenDisposesCleanly()
    {
        var state = new PageState();
        var spec = MakeSpec("p1", new Heading("Welcome"), new CheckBox("Accept") { Id = "acc" });

        using var host = new Win32WidgetHost(
            title: "Instella Widget Host Test",
            pages: new[] { spec },
            pageStates: new[] { state });

        host.CreateHeadless();
        Assert.That(host.Hwnd, Is.Not.EqualTo((nint)0));
        Assert.That(host.CurrentPageIndex, Is.EqualTo(0));
    }

    [SupportedOSPlatform("windows")]
    [Test]
    public void Constructor_rejectsEmptyPageList()
    {
        Assert.Throws<ArgumentException>(() =>
            new Win32WidgetHost(
                "empty",
                pages: Array.Empty<PageSpec>(),
                pageStates: Array.Empty<PageState>()));
    }

    [SupportedOSPlatform("windows")]
    [Test]
    public void Constructor_rejectsMismatchedListLengths()
    {
        Assert.Throws<ArgumentException>(() =>
            new Win32WidgetHost(
                "mismatch",
                pages: new[] { MakeSpec("p1", new Heading("A")) },
                pageStates: Array.Empty<PageState>()));
    }

    [SupportedOSPlatform("windows")]
    [Test]
    public void SecondHostOnTheSameThread_RunsItsMessageLoop_AfterTheFirstFinished()
    {
        // Found trying the QuickNotes installer: Finish on the scope page left a WM_QUIT queued
        // (posted again by WM_DESTROY when Dispose destroyed the window after the loop ended),
        // and the wizard that followed on the same thread closed before it was ever shown.
        var first = RunAndFinish(out _);
        var second = RunAndFinish(out var secondProcessedAMessage);

        Assert.That(first, Is.EqualTo(Win32WidgetHostOutcome.Completed), "first window");
        Assert.That(secondProcessedAMessage, Is.True, "the second window's loop ran");
        Assert.That(second, Is.EqualTo(Win32WidgetHostOutcome.Completed), "second window");
    }

    [SupportedOSPlatform("windows")]
    [Test]
    public void ClientArea_IsTheDesignSize_SoTheFooterIsSymmetric()
    {
        using var host = new Win32WidgetHost("size", new[] { MakeSpec("p1", new Heading("A")) }, new[] { new PageState() });
        host.CreateHeadless();

        Assert.That(GetClientRect(host.Hwnd, out var rect), Is.True);
        var dpi = Win32DpiAware.GetDpiForWindowOrDefault(host.Hwnd);
        Assert.That(rect.Right - rect.Left, Is.EqualTo(Win32DpiAware.Scale(600, dpi)), "client width");
        Assert.That(rect.Bottom - rect.Top, Is.EqualTo(Win32DpiAware.Scale(460, dpi)), "client height");
    }

    [SupportedOSPlatform("windows")]
    [Test]
    public void ContinueWhen_Throwing_DisablesContinue_AndKeepsThePage()
    {
        // An author's ContinueWhen runs inside the window procedure, where an escaping
        // exception terminates the process.
        var spec = MakeSpec("p1", new Heading("A")) with { ContinueWhen = _ => throw new InvalidOperationException("bad predicate") };
        var next = MakeSpec("p2", new Heading("B"));
        using var host = new Win32WidgetHost("throws", new[] { spec, next }, new[] { new PageState(), new PageState() });
        host.CreateHeadless();

        host.Post(IdContinue, 0);

        Assert.That(IsWindowEnabled(GetDlgItem(host.Hwnd, IdContinue)), Is.False, "Continue is disabled");
        Assert.That(host.CurrentPageIndex, Is.EqualTo(0), "the page is kept");
    }

    [SupportedOSPlatform("windows")]
    [Test]
    public void AnExceptionInACommand_IsReported_NotFatal()
    {
        var pages = new[] { MakeSpec("p1", new Heading("A")), MakeSpec("p2", new Heading("B")) };
        using var host = new Win32WidgetHost("throws", pages, new[] { new PageState(), new PageState() });
        var shown = new List<string>();
        host.PageErrorSink = shown.Add;
        host.BeforeLeave = _ => throw new InvalidOperationException("hook failed");
        host.CreateHeadless();

        host.Post(IdContinue, 0);

        Assert.That(shown, Has.Count.EqualTo(1));
        Assert.That(shown[0], Is.EqualTo("The installer hit an error on this page: hook failed"));
        Assert.That(host.CurrentPageIndex, Is.EqualTo(0));
        Assert.That(IsWindowEnabled(GetDlgItem(host.Hwnd, IdContinue)), Is.False);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint GetDlgItem(nint hDlg, int id);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(nint hWnd);

    [SupportedOSPlatform("windows")]
    private static Win32WidgetHostOutcome RunAndFinish(out bool processedAMessage)
    {
        var ran = false;
        using var host = new Win32WidgetHost("sequence", new[] { MakeSpec("p1", new Heading("A")) }, new[] { new PageState() });
        // PageChanged fires when page 0 is shown inside Run(). Click Finish a little later from
        // another thread, like a user would: GetMessage returns posted messages before WM_QUIT,
        // so a click queued straight away would mask a stray WM_QUIT.
        host.PageChanged += _ => new Thread(() =>
        {
            Thread.Sleep(300);
            host.PostToUiThread(() =>
            {
                ran = true;
                host.Post(IdContinue, 0);
            });
        }) { IsBackground = true }.Start();
        var outcome = host.Run();
        processedAMessage = ran;
        return outcome;
    }

    private const int IdContinue = 903;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint hWnd, out Rect rect);

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
