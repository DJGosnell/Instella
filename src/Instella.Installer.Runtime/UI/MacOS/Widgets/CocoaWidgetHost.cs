using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Runtime.UI;
using Instella.Installer.Runtime.UI.Widgets;

namespace Instella.Installer.Runtime.UI.MacOS.Widgets;

/// <summary>
/// Outcome of running a <see cref="CocoaWidgetHost"/> main loop.
/// </summary>
internal enum CocoaWidgetHostOutcome
{
    /// <summary>User navigated past the last page via Continue.</summary>
    Completed,
    /// <summary>User clicked Cancel or closed the window.</summary>
    Cancelled,
}

/// <summary>
/// Standalone Cocoa host window that drives a multi-page wizard
/// built from <see cref="PageSpec"/> records. Parallels the
/// <see cref="Instella.Installer.Runtime.UI.Windows.Widgets.Win32WidgetHost"/>
/// and GTK equivalents.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class CocoaWidgetHost : IDisposable
{
    private const int WindowWidth = 600;
    private const int WindowHeight = 460;
    private const int FooterHeight = 56;
    private const uint StyleMask = 1 | 2 | 4; // titled | closable | miniaturizable

    private readonly string _title;
    private readonly IReadOnlyList<PageSpec> _pages;
    private readonly IReadOnlyList<PageState> _pageStates;
    private readonly List<CocoaPagePanel> _panels = new();

    private nint _app;
    private nint _window;
    private nint _contentView;
    private nint _btnCancel, _btnBack, _btnContinue;
    private nint _previewBanner;
    private int _currentPageIndex;
    private CocoaWidgetHostOutcome _outcome = CocoaWidgetHostOutcome.Cancelled;
    private GCHandle _selfHandle;
    private bool _disposed;

    /// <summary>
    /// When <c>true</c>, the window title gets a <c>" — PREVIEW"</c> suffix
    /// and a thin banner reading "Preview mode — no changes will be made" is
    /// added to the content view above the pages. Set only by
    /// <see cref="Runners.PreviewModeRunner"/>.
    /// </summary>
    internal bool IsPreview { get; init; }

    private static nint s_navDelegateClass;
    private static readonly object s_classGate = new();
    private static CocoaWidgetHost? s_activeHost;

    internal CocoaWidgetHost(string title, IReadOnlyList<PageSpec> pages, IReadOnlyList<PageState> pageStates)
    {
        if (pages.Count == 0) throw new ArgumentException("At least one page is required.", nameof(pages));
        if (pages.Count != pageStates.Count)
            throw new ArgumentException("pages and pageStates must be parallel lists.", nameof(pageStates));

        _title = title;
        _pages = pages;
        _pageStates = pageStates;
    }

    internal nint Window => _window;
    internal int CurrentPageIndex => _currentPageIndex;

    /// <summary>NSTextField pointer for the preview banner when <see cref="IsPreview"/> is true; 0 otherwise. Test-only.</summary>
    internal nint PreviewBanner => _previewBanner;

    internal CocoaWidgetHostOutcome Run()
    {
        _app = NS.SharedApplication();
        NS.ActivateApp(_app);

        CreateWindow();
        ObjC.msgSendVoid(_window, NS.Sel("makeKeyAndOrderFront:"), 0);

        s_activeHost = this;
        try { NS.RunApp(_app); }
        finally { s_activeHost = null; }

        return _outcome;
    }

    /// <summary>
    /// Create the window without showing it or running the main loop —
    /// used by headless tests that drive navigation synchronously.
    /// </summary>
    internal void CreateHeadless()
    {
        _app = NS.SharedApplication();
        CreateWindow();
        s_activeHost = this;
    }

    internal void NavigateTo(int index)
    {
        if (index < 0 || index >= _pages.Count) return;
        NS.SetHidden(_panels[_currentPageIndex].RootView, true);
        _currentPageIndex = index;
        NS.SetHidden(_panels[_currentPageIndex].RootView, false);
        UpdateFooterButtons();
    }

    internal void SimulateContinue()
    {
        if (_panels[_currentPageIndex].CanContinue() == false) return;
        if (_currentPageIndex + 1 < _pages.Count)
            NavigateTo(_currentPageIndex + 1);
        else
        {
            _outcome = CocoaWidgetHostOutcome.Completed;
            SafeStop();
        }
    }

    internal void SimulateCancel()
    {
        _outcome = CocoaWidgetHostOutcome.Cancelled;
        SafeStop();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var panel in _panels)
        {
            panel.BrowseRequested -= OnBrowseRequested;
            _pageStates[_panels.IndexOf(panel)].StateChanged -= UpdateFooterButtons;
            panel.Dispose();
        }
        _panels.Clear();

        if (_window != 0)
        {
            try { ObjC.msgSendVoid(_window, NS.Sel("close")); }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            try { NS.Release(_window); }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            _window = 0;
        }

        if (_selfHandle.IsAllocated) _selfHandle.Free();
        if (ReferenceEquals(s_activeHost, this)) s_activeHost = null;
    }

    private unsafe void CreateWindow()
    {
        _selfHandle = GCHandle.Alloc(this);

        var frame = new NSRect(0, 0, WindowWidth, WindowHeight);
        _window = NS.CreateWindow(frame, StyleMask);

        var title = NS.StringNew(PreviewMarker.DecorateTitle(_title, IsPreview));
        ObjC.msgSendVoid(_window, NS.Sel("setTitle:"), title);
        NS.Release(title);
        ObjC.msgSendVoid(_window, NS.Sel("center"));

        _contentView = NS.ContentView(_window);

        if (IsPreview)
            _previewBanner = CreatePreviewBanner();

        var topOffset = IsPreview ? PreviewMarker.BannerHeight : 0;
        var panelHeight = WindowHeight - FooterHeight - topOffset;
        for (int i = 0; i < _pages.Count; i++)
        {
            var panel = new CocoaPagePanel(_pages[i], _pageStates[i], WindowWidth, panelHeight);
            // Position the panel at the top (Cocoa bottom-origin: y = FooterHeight).
            // setFrame: takes an NSRect — reuses our existing rect-taking overload.
            ObjC.msgSendRect(panel.RootView, NS.Sel("setFrame:"),
                new NSRect(0, FooterHeight, WindowWidth, panelHeight));

            panel.BrowseRequested += OnBrowseRequested;
            _pageStates[i].StateChanged += UpdateFooterButtons;
            _panels.Add(panel);

            NS.AddSubview(_contentView, panel.RootView);
            NS.SetHidden(panel.RootView, i != 0);
        }

        CreateFooterButtons();
        UpdateFooterButtons();
    }

    /// <summary>
    /// Create the top-of-window preview banner as an NSTextField so it can
    /// draw its own background colour. Positioned at the top of the content
    /// view (Cocoa bottom-origin: <c>y = WindowHeight - BannerHeight</c>).
    /// </summary>
    private nint CreatePreviewBanner()
    {
        var bannerFrame = new NSRect(0, WindowHeight - PreviewMarker.BannerHeight, WindowWidth, PreviewMarker.BannerHeight);
        // CreateLabel sets the read-only/no-selection/no-bezel fields we want;
        // flip drawsBackground so the banner visually pops out of the chrome.
        var field = NS.CreateLabel(PreviewMarker.BannerText, bannerFrame);
        ObjC.msgSendVoid(field, NS.Sel("setDrawsBackground:"), 1);
        NS.AddSubview(_contentView, field);
        return field;
    }

    private unsafe void CreateFooterButtons()
    {
        EnsureNavDelegateClass();
        var target = ObjC.msgSend(ObjC.msgSend(s_navDelegateClass, NS.Sel("alloc")), NS.Sel("init"));

        var buttonHeight = 30;
        var buttonWidth = 90;
        var margin = 16;
        var y = 12; // Cocoa bottom-origin: footer buttons are near the bottom.

        _btnCancel = NS.CreateButton("Cancel", new NSRect(margin, y, buttonWidth, buttonHeight));
        ObjC.msgSendVoid(_btnCancel, NS.Sel("setTarget:"), target);
        ObjC.msgSendVoid(_btnCancel, NS.Sel("setAction:"), NS.Sel("onHostCancel:"));
        NS.AddSubview(_contentView, _btnCancel);

        _btnBack = NS.CreateButton("< Back", new NSRect(WindowWidth - 2 * buttonWidth - 2 * margin, y, buttonWidth, buttonHeight));
        ObjC.msgSendVoid(_btnBack, NS.Sel("setTarget:"), target);
        ObjC.msgSendVoid(_btnBack, NS.Sel("setAction:"), NS.Sel("onHostBack:"));
        NS.AddSubview(_contentView, _btnBack);

        _btnContinue = NS.CreateButton("Continue >", new NSRect(WindowWidth - buttonWidth - margin, y, buttonWidth, buttonHeight));
        ObjC.msgSendVoid(_btnContinue, NS.Sel("setTarget:"), target);
        ObjC.msgSendVoid(_btnContinue, NS.Sel("setAction:"), NS.Sel("onHostContinue:"));
        NS.AddSubview(_contentView, _btnContinue);
    }

    private void UpdateFooterButtons()
    {
        if (_btnBack != 0) NS.SetEnabled(_btnBack, _currentPageIndex > 0);
        if (_btnContinue != 0)
        {
            var panel = _currentPageIndex < _panels.Count ? _panels[_currentPageIndex] : null;
            NS.SetEnabled(_btnContinue, panel?.CanContinue() ?? true);
            var isLast = _currentPageIndex == _pages.Count - 1;
            var title = NS.StringNew(isLast ? "Finish" : "Continue >");
            ObjC.msgSendVoid(_btnContinue, NS.Sel("setTitle:"), title);
            NS.Release(title);
        }
    }

    private void OnBrowseRequested(CocoaWidgetInstance instance)
    {
        if (instance.Widget is not FolderPicker) return;
        var panel = ObjC.msgSend(NS.Class("NSOpenPanel"), NS.Sel("openPanel"));
        ObjC.msgSendVoid(panel, NS.Sel("setCanChooseDirectories:"), 1);
        ObjC.msgSendVoid(panel, NS.Sel("setCanChooseFiles:"), 0);
        ObjC.msgSendVoid(panel, NS.Sel("setAllowsMultipleSelection:"), 0);

        var result = ObjC.msgSendInt(panel, NS.Sel("runModal"));
        if (result == 1) // NSModalResponseOK
        {
            var urls = ObjC.msgSend(panel, NS.Sel("URLs"));
            var url = ObjC.msgSend(urls, NS.Sel("objectAtIndex:"), 0);
            var pathStr = NS.StringRead(ObjC.msgSend(url, NS.Sel("path")));
            NS.SetStringValue(instance.PrimaryView, pathStr);
            // Synthesize a controlTextDidChange so PageState observes the new value.
            // NSTextField doesn't fire the notification for programmatic changes, so
            // we write directly via the pump's public helper.
            // Extract the id based on widget kind.
            var id = (instance.Widget as FolderPicker)?.Id;
            if (!string.IsNullOrEmpty(id))
                _pageStates[_currentPageIndex].Set(id, pathStr);
        }
    }

    private void SafeStop()
    {
        if (_app == 0) return;
        try { NS.StopApp(_app, 0); }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    private static unsafe void EnsureNavDelegateClass()
    {
        lock (s_classGate)
        {
            if (s_navDelegateClass != 0) return;
            s_navDelegateClass = ObjC.objc_allocateClassPair(NS.Class("NSObject"), "InstellaCocoaHostDelegate", 0);
            ObjC.class_addMethod(s_navDelegateClass, NS.Sel("onHostCancel:"),
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnCancelAction, "v@:@");
            ObjC.class_addMethod(s_navDelegateClass, NS.Sel("onHostBack:"),
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnBackAction, "v@:@");
            ObjC.class_addMethod(s_navDelegateClass, NS.Sel("onHostContinue:"),
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnContinueAction, "v@:@");
            ObjC.objc_registerClassPair(s_navDelegateClass);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnCancelAction(nint self, nint sel, nint sender) => s_activeHost?.SimulateCancel();

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnBackAction(nint self, nint sel, nint sender)
    {
        if (s_activeHost is { } h && h._currentPageIndex > 0) h.NavigateTo(h._currentPageIndex - 1);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnContinueAction(nint self, nint sel, nint sender) => s_activeHost?.SimulateContinue();
}
