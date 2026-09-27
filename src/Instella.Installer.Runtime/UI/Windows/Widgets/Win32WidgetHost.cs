using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Runtime.UI;
using Instella.Installer.Runtime.UI.Widgets;

namespace Instella.Installer.Runtime.UI.Windows.Widgets;

/// <summary>
/// Outcome of running a <see cref="Win32WidgetHost"/> message loop.
/// </summary>
internal enum Win32WidgetHostOutcome
{
    /// <summary>User navigated past the last page via the Continue button.</summary>
    Completed,
    /// <summary>User clicked Cancel or closed the window.</summary>
    Cancelled,
}

/// <summary>
/// Standalone host window that drives a multi-page wizard described
/// by <see cref="PageSpec"/> records.
/// </summary>
/// <remarks>
/// <para>The host owns a window class, fonts, a footer bar of
/// Cancel / Back / Continue buttons, and the current page's
/// <see cref="Win32PagePanel"/>. Navigation swaps the active panel
/// in-place; PageState objects persist across navigation so that the
/// user's inputs survive a Back/Forward pair.</para>
/// <para>The host knows nothing about install steps: the runners drive it
/// through pages and page state. Tests can instantiate a host, drive it via synthetic messages,
/// and verify page navigation + state transitions without needing a
/// real install pipeline.</para>
/// </remarks>
internal sealed class Win32WidgetHost : IDisposable
{
    private const string WindowClassName = "InstellaWidgetHostClass";
    private const int DesignWidth = 600;
    private const int DesignHeight = 460;

    // Panel content inset. 16 px makes content hug the left edge and 24 px skews it
    // visibly to the right; 20 px leaves a balanced margin at both 100% and 150% DPI.
    private const int PanelHorizontalInset = 20;
    private const int PanelVerticalInset = 20;
    private const int FooterButtonHeight = 30;

    // Host-internal footer control IDs. Chosen above the default widget
    // factory range (1000+) but ahead of any test-allocated base so we
    // don't collide.
    private const int IdCancel = 901;
    private const int IdBack = 902;
    private const int IdContinue = 903;
    private const int IdPreviewBanner = 904;

    /// <summary>
    /// Custom Win32 message posted by <see cref="PostToUiThread"/> to wake
    /// the host's message loop and drain the queued-action list on the UI
    /// thread. <c>WM_USER</c> is 0x0400; we use <c>+1</c> so we don't
    /// collide with any other custom message a subclass might register.
    /// </summary>
    private const uint WM_INSTELLA_INVOKE = 0x0401;
    private const uint WM_DPICHANGED = 0x02E0;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOCOPYBITS = 0x0100;

    private readonly IReadOnlyList<PageSpec> _pages;
    private readonly IReadOnlyList<PageState> _pageStates;
    private readonly string _title;
    // Mutable so WM_DPICHANGED can switch the effective DPI when the window
    // moves to a monitor with different scaling.
    private int _dpi;

    private nint _hwnd;
    private nint _hInstance;
    private nint _bodyFont;
    private nint _headerFont;
    private nint _previewBannerHwnd;
    private Win32PagePanel? _currentPanel;
    private int _currentPageIndex;
    private bool _classRegistered;
    private Win32WidgetHostOutcome _outcome = Win32WidgetHostOutcome.Cancelled;
    private bool _disposed;
    // True while Run() is inside its message loop; only then may WM_DESTROY post WM_QUIT.
    private bool _loopRunning;

    /// <summary>Accelerator-table handle owning Alt+C/B/N/F routing. 0 when not yet created or already destroyed.</summary>
    private nint _acceleratorTable;
    /// <summary>Window icon handle (ICON_BIG / ICON_SMALL) resolved from the exe's embedded .ico. 0 on platforms without an extractable icon.</summary>
    private nint _windowIcon;
    /// <summary>The ICON_SMALL of the Instella fallback icon; 0 when <see cref="_windowIcon"/> serves both.</summary>
    private nint _windowIconSmall;

    /// <summary>
    /// Queue of actions posted from background threads to execute on the
    /// UI thread. Drained when <see cref="WM_INSTELLA_INVOKE"/> reaches
    /// <see cref="WndProc"/>. Protected by <see cref="_invokeGate"/>.
    /// </summary>
    private readonly System.Collections.Generic.Queue<Action> _pendingInvokes = new();
    private readonly object _invokeGate = new();

    /// <summary>
    /// When <c>true</c>, the host's footer Back / Cancel buttons are held
    /// disabled regardless of the current page index. Set by
    /// <see cref="LockNavigation"/> once the install reaches a terminal
    /// state (success / failure / cancelled) so the user can only click
    /// Finish to exit. The Continue button remains gated by the current
    /// panel's <c>CanContinue()</c> predicate.
    /// </summary>
    private bool _navigationLocked;

    /// <summary>
    /// When <c>true</c>, the footer Back button is held disabled regardless of
    /// page index, but Cancel stays enabled. Set by
    /// <see cref="LockBackNavigation"/> while the install pipeline is running so
    /// the user can't navigate back to the Options page mid-install (which the
    /// pipeline ignores anyway and which visually resets the Progress page).
    /// Distinct from <see cref="_navigationLocked"/>, which also disables Cancel
    /// once the install reaches a terminal state.
    /// </summary>
    private bool _backNavigationLocked;

    /// <summary>
    /// Subscription to the current page's <see cref="PageState.StateChanged"/>
    /// event. Refreshes the footer-button enabled/disabled state on every
    /// state mutation so <c>CanContinue</c> and <see cref="_navigationLocked"/>
    /// updates don't wait for the user's next click to take effect. Kept
    /// in a field so the handler can be unsubscribed when the page
    /// changes or the host disposes.
    /// </summary>
    private Action? _currentPageStateHandler;

    /// <summary>
    /// When <c>true</c>, the window title gets a <c>" — PREVIEW"</c> suffix and
    /// a thin banner reading "Preview mode — no changes will be made" is
    /// packed above the page panel. Set only by <see cref="Runners.PreviewModeRunner"/>.
    /// </summary>
    internal bool IsPreview { get; init; }

    [ThreadStatic]
    private static Win32WidgetHost? s_activeOnThread;

    internal Win32WidgetHost(
        string title,
        IReadOnlyList<PageSpec> pages,
        IReadOnlyList<PageState> pageStates,
        int dpi = 0)
    {
        if (pages.Count == 0) throw new ArgumentException("At least one page is required.", nameof(pages));
        if (pages.Count != pageStates.Count)
            throw new ArgumentException("pages and pageStates must be parallel lists.", nameof(pageStates));

        _title = title;
        _pages = pages;
        _pageStates = pageStates;
        _dpi = dpi;
    }

    /// <summary>The HWND of the host window once <see cref="Run"/> has created it. 0 before creation and after dispose.</summary>
    internal nint Hwnd => _hwnd;

    /// <summary>Current page index (0-based). Stable for tests to query.</summary>
    internal int CurrentPageIndex => _currentPageIndex;

    /// <summary>HWND of the preview banner STATIC control; 0 when <see cref="IsPreview"/> is false or before window creation. Test-only.</summary>
    internal nint PreviewBannerHwnd => _previewBannerHwnd;

    /// <summary>
    /// Fires after the host navigates to a new page (including the initial
    /// page on window creation). The event receives the new page index.
    /// Consumers such as <see cref="Runners.InteractiveInstallRunner"/>
    /// subscribe to detect when the user reaches the Progress page and
    /// then kick off the step pipeline.
    /// </summary>
    internal event Action<int>? PageChanged;

    /// <summary>
    /// Create the host window, show it, and run its message loop
    /// until the user completes, cancels, or closes. Must be called on
    /// the STA thread.
    /// </summary>
    internal Win32WidgetHostOutcome Run()
    {
        Win32DpiAware.EnsureProcessDpiAwareness();
        _hInstance = Win32.GetModuleHandleW(null);

        var icc = new INITCOMMONCONTROLSEX
        {
            dwSize = (uint)Marshal.SizeOf<INITCOMMONCONTROLSEX>(),
            dwICC = ICC.PROGRESS_CLASS,
        };
        Win32.InitCommonControlsEx(in icc);

        CreateFonts();
        RegisterWindowClass();
        CreateMainWindow();

        s_activeOnThread = this;
        try
        {
            Win32.ShowWindow(_hwnd, SW.SHOW);
            Win32.UpdateWindow(_hwnd);
            _loopRunning = true;
            RunMessageLoop();
        }
        finally
        {
            _loopRunning = false;
            s_activeOnThread = null;
        }

        return _outcome;
    }

    /// <summary>
    /// Create the host window WITHOUT showing it or running a message
    /// loop — used by headless tests that drive navigation via
    /// synthetic messages on the creating thread.
    /// </summary>
    internal void CreateHeadless()
    {
        Win32DpiAware.EnsureProcessDpiAwareness();
        _hInstance = Win32.GetModuleHandleW(null);

        var icc = new INITCOMMONCONTROLSEX
        {
            dwSize = (uint)Marshal.SizeOf<INITCOMMONCONTROLSEX>(),
            dwICC = ICC.PROGRESS_CLASS,
        };
        Win32.InitCommonControlsEx(in icc);

        CreateFonts();
        RegisterWindowClass();
        CreateMainWindow();
        s_activeOnThread = this;
    }

    /// <summary>
    /// Drive a single <c>WM_COMMAND</c> into the host as if the user
    /// had clicked a control. Exposed for tests.
    /// </summary>
    internal void Post(nuint wParam, nint lParam)
    {
        if (_hwnd == 0) return;
        Win32.SendMessageW(_hwnd, WM.COMMAND, wParam, lParam);
    }

    /// <summary>
    /// Hold the Back + Cancel buttons disabled for the remainder of this
    /// host's lifetime. Called by <see cref="Runners.InteractiveInstallRunner"/>
    /// when the step pipeline reaches a terminal state — mid-install
    /// navigation is nonsensical, and Cancel post-complete would just
    /// close the window the same as Finish. Continue stays enabled
    /// via the current panel's <c>CanContinue()</c> predicate so the
    /// user can exit normally.
    /// </summary>
    internal void LockNavigation()
    {
        _navigationLocked = true;
        if (_hwnd != 0) UpdateFooterButtons();
    }

    /// <summary>
    /// Disable the footer Back button while a long-running operation (the
    /// install pipeline) is in progress, without disabling Cancel. Called by
    /// <see cref="Runners.InteractiveInstallRunner"/> when the pipeline starts
    /// so navigating back to an earlier page mid-install — which the pipeline
    /// ignores anyway, and which visually resets the Progress page — is not
    /// offered. Superseded by <see cref="LockNavigation"/> at terminal state.
    /// </summary>
    internal void LockBackNavigation()
    {
        _backNavigationLocked = true;
        if (_hwnd != 0) UpdateFooterButtons();
    }

    /// <summary>
    /// Consulted when the user clicks Cancel or closes the window. Returning <c>true</c>
    /// means the owner handled it (for example by cancelling the pipeline and showing
    /// "Rolling back…"), so the window stays open until the owner calls <see cref="Close"/>.
    /// </summary>
    internal Func<bool>? CancelInterceptor { get; set; }

    /// <summary>
    /// Consulted before leaving page <c>index</c> through Next. Returns an error message to
    /// show (navigation then stays on the page) or null to continue.
    /// </summary>
    internal Func<int, string?>? BeforeLeave { get; set; }

    /// <summary>Closes the window from any thread; <see cref="Run"/> then returns Cancelled.</summary>
    internal void Close() => PostToUiThread(() =>
    {
        _outcome = Win32WidgetHostOutcome.Cancelled;
        if (_hwnd != 0) Win32.DestroyWindow(_hwnd);
    });

    /// <summary>
    /// Enqueue <paramref name="action"/> to run on the host's UI thread.
    /// Safe to call from any thread. The action runs inside the message
    /// loop after the next <see cref="WM_INSTELLA_INVOKE"/> drain pass,
    /// which is posted to the host window immediately. Used by
    /// <see cref="Runners.InteractiveInstallRunner"/> to marshal progress
    /// callbacks from the step-executor background task onto the UI
    /// thread so <see cref="PageState.Set"/> writes + consequent Win32
    /// widget updates happen where they're safe.
    /// </summary>
    internal void PostToUiThread(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_hwnd == 0 || _disposed) return;
        lock (_invokeGate)
            _pendingInvokes.Enqueue(action);
        Win32.PostMessageW(_hwnd, WM_INSTELLA_INVOKE, 0, 0);
    }

    /// <summary>
    /// Handle <c>WM_DPICHANGED</c> (sent by Windows when the user drags the
    /// window to a monitor with a different DPI or changes the display
    /// scaling). <paramref name="wParam"/>'s high word carries the new DPI
    /// and <paramref name="lParam"/> is a <c>RECT*</c> to the OS's suggested
    /// new window rect. Resize to the suggestion, then rebuild fonts + the
    /// footer buttons + the current page at the new DPI so everything
    /// rescales instead of staying at the old pixel sizes.
    /// </summary>
    private void HandleDpiChanged(nuint wParam, nint lParam)
    {
        var newDpi = (int)((wParam >> 16) & 0xFFFFu);
        if (newDpi <= 0) return;

        _dpi = newDpi;

        // OS-suggested new rect — respecting it keeps the window under the
        // cursor during a drag between monitors.
        if (lParam != 0)
        {
            var rect = Marshal.PtrToStructure<RECT>(lParam);
            Win32.SetWindowPos(_hwnd, 0,
                rect.left, rect.top,
                rect.right - rect.left, rect.bottom - rect.top,
                SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOCOPYBITS);
        }

        // Replace the fonts at the new DPI. Old fonts must be released
        // AFTER new fonts are installed on the controls, so track both.
        var oldBody = _bodyFont;
        var oldHeader = _headerFont;
        CreateFonts();

        // Destroy + recreate the footer buttons — they were sized with
        // the old DPI and their WM_SETFONT message references the old
        // font handle. Simpler than re-measuring / re-sending SETFONT.
        var cancel = Win32.GetDlgItem(_hwnd, IdCancel);
        var back = Win32.GetDlgItem(_hwnd, IdBack);
        var cont = Win32.GetDlgItem(_hwnd, IdContinue);
        if (cancel != 0) Win32.DestroyWindow(cancel);
        if (back != 0) Win32.DestroyWindow(back);
        if (cont != 0) Win32.DestroyWindow(cont);
        CreateFooterButtons();

        // Rebuild the preview banner at the new DPI too — it was sized once at
        // creation, and ShowPage offsets the panel top by the *new*-DPI banner
        // height, so leaving the old-DPI banner would misalign the panel.
        if (IsPreview && _previewBannerHwnd != 0)
        {
            Win32.DestroyWindow(_previewBannerHwnd);
            _previewBannerHwnd = 0;
            CreatePreviewBanner();
        }

        // Rebuild the current page panel at the new DPI. The existing
        // panel's widget HWNDs and layout were computed against the old
        // DPI, so ShowPage tears them down and recreates at the new size.
        ShowPage(_currentPageIndex);
        UpdateFooterButtons();

        // Safe to release old fonts now — nothing references them anymore.
        if (oldBody != 0) Win32.DeleteObject(oldBody);
        if (oldHeader != 0) Win32.DeleteObject(oldHeader);
    }

    private void DrainPendingInvokes()
    {
        while (true)
        {
            Action? next;
            lock (_invokeGate)
            {
                if (_pendingInvokes.Count == 0) return;
                next = _pendingInvokes.Dequeue();
            }
            try { next(); }
            catch { /* best-effort: a bad callback must not kill the message loop */ }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_currentPageStateHandler is not null
            && _currentPageIndex >= 0
            && _currentPageIndex < _pageStates.Count)
        {
            _pageStates[_currentPageIndex].StateChanged -= _currentPageStateHandler;
            _currentPageStateHandler = null;
        }

        _currentPanel?.Dispose();
        _currentPanel = null;

        if (_acceleratorTable != 0)
        {
            Win32.DestroyAcceleratorTable(_acceleratorTable);
            _acceleratorTable = 0;
        }

        if (_hwnd != 0)
        {
            // DestroyWindow recursively destroys child windows (banner, footer
            // buttons) — no need to release the banner HWND separately.
            Win32.DestroyWindow(_hwnd);
            _hwnd = 0;
            _previewBannerHwnd = 0;
        }

        // ExtractIconW hands back an icon the caller owns (unlike LoadIcon on a
        // shared system resource) — destroy it so the HICON doesn't leak.
        if (_windowIcon != 0) { Win32.DestroyIcon(_windowIcon); _windowIcon = 0; }
        if (_windowIconSmall != 0) { Win32.DestroyIcon(_windowIconSmall); _windowIconSmall = 0; }
        if (_bodyFont != 0) { Win32.DeleteObject(_bodyFont); _bodyFont = 0; }
        if (_headerFont != 0) { Win32.DeleteObject(_headerFont); _headerFont = 0; }

        if (ReferenceEquals(s_activeOnThread, this)) s_activeOnThread = null;
    }

    private void CreateFonts()
    {
        _bodyFont = CreateFont("Segoe UI Variable Text", ScaleDesign(-13), FW.NORMAL);
        _headerFont = CreateFont("Segoe UI Variable Display", ScaleDesign(-24), FW.SEMIBOLD);
    }

    private static unsafe nint CreateFont(string faceName, int height, int weight)
    {
        var lf = new LOGFONTW
        {
            lfHeight = height,
            lfWeight = weight,
            lfCharSet = 1,
            lfQuality = 5,
        };
        for (int i = 0; i < faceName.Length && i < 31; i++)
            lf.lfFaceName[i] = faceName[i];

        return Win32.CreateFontIndirectW(in lf);
    }

    private unsafe void RegisterWindowClass()
    {
        if (_classRegistered) return;

        // Load the exe's embedded icon (set via <ApplicationIcon> in the
        // user's .Installer.csproj) so the window title bar and taskbar
        // show the app brand instead of the system's generic "window"
        // glyph. `(nint)1` is MAKEINTRESOURCE(1) — the resource ID the
        // .NET SDK assigns to the icon group produced from the .ico file.
        // Falls back to IDI_APPLICATION if no icon is embedded (keeps the
        // window functional even on installers that forgot to set
        // ApplicationIcon).
        var appIcon = Win32.LoadIconW(_hInstance, (nint)1);
        if (appIcon == 0) appIcon = Win32.LoadIconW(0, (nint)32512);

        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)sizeof(WNDCLASSEXW),
            style = 0x0003, // CS_HREDRAW | CS_VREDRAW
            lpfnWndProc = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint>)&WndProc,
            hInstance = _hInstance,
            hIcon = appIcon,
            hIconSm = appIcon,
            hCursor = Win32.LoadCursorW(0, IDC.ARROW),
            hbrBackground = (nint)(COLOR.BTNFACE + 1),
            lpszClassName = (nint)Marshal.StringToHGlobalUni(WindowClassName),
        };

        try { _ = Win32.RegisterClassExW(in wc); }
        finally { Marshal.FreeHGlobal(wc.lpszClassName); }

        _classRegistered = true;
    }

    private void CreateMainWindow()
    {
        // The design size is the client area: every control is laid out against it.
        var (width, height) = Win32DpiAware.OuterSizeForClient(
            ScaleDesign(DesignWidth), ScaleDesign(DesignHeight), WS.INSTALLER_WINDOW, WS_EX.CONTROLPARENT, EffectiveDpi());
        var screenW = Win32.GetSystemMetrics(SM.CXSCREEN);
        var screenH = Win32.GetSystemMetrics(SM.CYSCREEN);
        var x = Math.Max(0, (screenW - width) / 2);
        var y = Math.Max(0, (screenH - height) / 2);

        var effectiveTitle = PreviewMarker.DecorateTitle(_title, IsPreview);
        _hwnd = Win32.CreateWindowExW(
            WS_EX.CONTROLPARENT, WindowClassName, effectiveTitle,
            WS.INSTALLER_WINDOW,
            x, y, width, height,
            0, 0, _hInstance, 0);

        if (IsPreview) CreatePreviewBanner();
        CreateFooterButtons();
        ApplyWindowIcon();
        BuildAcceleratorTable();
        ShowPage(0);
    }

    /// <summary>
    /// Resolve the installer exe's embedded icon (set via <c>&lt;ApplicationIcon&gt;</c>
    /// in the .Installer.csproj) and push it to the window's ICON_BIG
    /// (Alt+Tab, taskbar) + ICON_SMALL (title bar) slots via
    /// <c>WM_SETICON</c>. The class-level hIcon set at RegisterClassEx
    /// time is unreliable on NativeAOT builds because the icon group
    /// resource ID the linker emits isn't always <c>1</c>; the more
    /// robust path is to <see cref="Win32.ExtractIconW"/> by exe path
    /// which always hits the first icon group in the PE. No-op when the
    /// process path is unavailable or extraction fails. An exe without an
    /// icon shows the Instella logo instead of the generic window glyph.
    /// </summary>
    private void ApplyWindowIcon()
    {
        const uint WM_SETICON = 0x0080;
        const nuint ICON_SMALL = 0;
        const nuint ICON_BIG = 1;

        try
        {
            // ExtractIconW returns 1 for "file exists but has no icon"
            // (a sentinel, not a handle) — treat that as no-icon.
            var exePath = Environment.ProcessPath;
            var icon = string.IsNullOrEmpty(exePath) ? 0 : Win32.ExtractIconW(_hInstance, exePath, 0);
            if (icon != 0 && icon != 1)
            {
                _windowIcon = icon;
                Win32.SendMessageW(_hwnd, WM_SETICON, ICON_BIG, icon);
                Win32.SendMessageW(_hwnd, WM_SETICON, ICON_SMALL, icon);
                return;
            }

            var dpi = Win32DpiAware.GetDpiForWindowOrDefault(_hwnd);
            _windowIcon = Win32Branding.CreateFallbackIcon(Win32DpiAware.Scale(32, dpi));
            _windowIconSmall = Win32Branding.CreateFallbackIcon(Win32DpiAware.Scale(16, dpi));
            if (_windowIcon != 0) Win32.SendMessageW(_hwnd, WM_SETICON, ICON_BIG, _windowIcon);
            if (_windowIconSmall != 0) Win32.SendMessageW(_hwnd, WM_SETICON, ICON_SMALL, _windowIconSmall);
        }
        catch
        {
            // ExtractIconW failures (missing shell32, sandboxed env) are
            // non-fatal — the user sees the default system icon instead
            // of the app icon and the installer still functions.
        }
    }

    /// <summary>
    /// Build the Alt-mnemonic accelerator table that routes Alt+C / Alt+B
    /// / Alt+N / Alt+F to the footer buttons. The ampersand in each
    /// button label provides the visible underline; this table is what
    /// actually turns a key press into a <c>WM_COMMAND</c> with the
    /// button's control id. Chosen over <see cref="Win32.IsDialogMessageW"/>
    /// alone because mnemonic routing via IsDialogMessage is unreliable
    /// for our top-level non-dialog window class — an explicit accelerator
    /// table is guaranteed by the Win32 API to reach WM_COMMAND.
    /// </summary>
    private void BuildAcceleratorTable()
    {
        var accels = BuildAcceleratorEntries();
        _acceleratorTable = Win32.CreateAcceleratorTableW(accels, accels.Length);
    }

    /// <summary>
    /// Build the Alt-mnemonic accelerator entries that route Alt+C / Alt+B /
    /// Alt+N / Alt+F to the Cancel / Back / Continue / Finish footer buttons.
    /// Finish shares the Continue control id (Alt+F on the final page clicks the
    /// same button whose label reads "Finish"). Pure; extracted for unit testing
    /// without creating a real accelerator table.
    /// </summary>
    internal static ACCEL[] BuildAcceleratorEntries()
    {
        byte altVirt = FVIRT.VIRTKEY | FVIRT.ALT;
        return new[]
        {
            new ACCEL { fVirt = altVirt, key = VK.C, cmd = (ushort)IdCancel },
            new ACCEL { fVirt = altVirt, key = VK.B, cmd = (ushort)IdBack },
            new ACCEL { fVirt = altVirt, key = VK.N, cmd = (ushort)IdContinue },
            new ACCEL { fVirt = altVirt, key = VK.F, cmd = (ushort)IdContinue },
        };
    }

    private void CreatePreviewBanner()
    {
        var bannerHeight = ScaleDesign(PreviewMarker.BannerHeight);
        var hostWidth = ScaleDesign(DesignWidth);

        _previewBannerHwnd = Win32.CreateWindowExW(
            0, "STATIC", PreviewMarker.BannerText,
            WS.CHILD | WS.VISIBLE | WS.SS_CENTERIMAGE,
            0, 0, hostWidth, bannerHeight,
            _hwnd, (nint)IdPreviewBanner, _hInstance, 0);

        if (_previewBannerHwnd != 0 && _bodyFont != 0)
            Win32.SendMessageW(_previewBannerHwnd, WM.SETFONT, (nuint)_bodyFont, 1);
    }

    private void CreateFooterButtons()
    {
        var hostWidth = ScaleDesign(DesignWidth);
        var hostHeight = ScaleDesign(DesignHeight);
        var buttonHeight = ScaleDesign(FooterButtonHeight);
        var buttonWidth = ScaleDesign(90);
        // Match the panel's horizontal inset so the buttons align with the
        // page content above them instead of hugging the chrome edge. The
        // inter-button gap (between Back and Continue/Finish) stays at 8 px.
        var margin = ScaleDesign(PanelHorizontalInset);
        var interButtonGap = ScaleDesign(8);
        // Same inset from the bottom as from the sides (the client area is the design size).
        var y = hostHeight - margin - buttonHeight;

        // `&X` in a button label marks Alt+X as the keyboard accelerator —
        // IsDialogMessageW in the message loop routes Alt-key presses to
        // the matching button. Letters chosen to match the standard
        // Windows wizard convention (Back / Next / Cancel / Finish). The
        // button's visible label renders the ampersand as an underline on
        // the following character.
        CreateFooterButton("&Cancel", WS.BS_PUSHBUTTON, margin, y, buttonWidth, buttonHeight, IdCancel);
        CreateFooterButton("< &Back", WS.BS_PUSHBUTTON,
            hostWidth - 2 * buttonWidth - margin - interButtonGap, y, buttonWidth, buttonHeight, IdBack);
        CreateFooterButton("&Next >", WS.BS_DEFPUSHBUTTON,
            hostWidth - buttonWidth - margin, y, buttonWidth, buttonHeight, IdContinue);
    }

    private void CreateFooterButton(string text, uint style, int x, int y, int w, int h, int id)
    {
        var hwnd = Win32.CreateWindowExW(
            0, "BUTTON", text,
            WS.CHILD | WS.VISIBLE | WS.TABSTOP | style,
            x, y, w, h,
            _hwnd, (nint)id, _hInstance, 0);
        if (hwnd != 0 && _bodyFont != 0)
            Win32.SendMessageW(hwnd, WM.SETFONT, (nuint)_bodyFont, 1);
    }

    private void ShowPage(int index)
    {
        if (index < 0 || index >= _pages.Count) return;

        // Detach the previous page's state subscription so we don't leak a
        // handler that updates footer buttons for a non-visible page.
        if (_currentPageStateHandler is not null
            && _currentPageIndex >= 0
            && _currentPageIndex < _pageStates.Count)
        {
            _pageStates[_currentPageIndex].StateChanged -= _currentPageStateHandler;
        }

        _currentPanel?.Dispose();
        _currentPageIndex = index;

        var panelLeft = ScaleDesign(PanelHorizontalInset);
        var bannerOffset = IsPreview ? ScaleDesign(PreviewMarker.BannerHeight) : 0;
        var panelTop = ScaleDesign(PanelVerticalInset) + bannerOffset;
        var panelWidth = ScaleDesign(DesignWidth) - 2 * panelLeft;
        // Down to the footer buttons (CreateFooterButtons), so a text box can fill the page.
        var panelHeight = ScaleDesign(DesignHeight) - ScaleDesign(PanelHorizontalInset) - ScaleDesign(FooterButtonHeight) - panelTop;

        _currentPanel = new Win32PagePanel(
            _hwnd, _pages[index], _pageStates[index],
            _hInstance, _bodyFont, _headerFont,
            panelLeft, panelTop, panelWidth,
            Win32DpiAware.GetDpiForWindowOrDefault(_hwnd),
            panelHeight: panelHeight);

        _currentPanel.BrowseRequested += OnBrowseRequested;

        // Refresh footer-button state whenever this page's PageState changes,
        // so CanContinue flips (e.g. the Progress page's CanFinish flag
        // transitioning from false → true) immediately light up / lock the
        // Continue button without waiting for a user click to trigger
        // HandleCommand.
        _currentPageStateHandler = UpdateFooterButtons;
        _pageStates[_currentPageIndex].StateChanged += _currentPageStateHandler;

        UpdateFooterButtons();
        PageChanged?.Invoke(_currentPageIndex);
    }

    // Runs on every state change, inside WndProc: an exception must not leave it.
    private void UpdateFooterButtons()
    {
        try
        {
            UpdateFooterButtonsCore();
        }
        catch (Exception ex)
        {
            ReportPageError(ex);
        }
    }

    private void UpdateFooterButtonsCore()
    {
        var cancel = Win32.GetDlgItem(_hwnd, IdCancel);
        var back = Win32.GetDlgItem(_hwnd, IdBack);
        var cont = Win32.GetDlgItem(_hwnd, IdContinue);

        // Navigation lock (terminal install state) wins over normal page-
        // index gating — even on non-first pages Back is disabled, and
        // Cancel becomes a no-op so the user can only exit via Finish.
        if (back != 0)
            Win32.EnableWindow(back, (!_navigationLocked && !_backNavigationLocked && _currentPageIndex > 0) ? 1 : 0);
        if (cancel != 0)
            Win32.EnableWindow(cancel, _navigationLocked ? 0 : 1);
        if (cont != 0)
            Win32.EnableWindow(cont, (_currentPanel?.CanContinue() ?? true) ? 1 : 0);

        var isLast = _currentPageIndex == _pages.Count - 1;
        var page = _pages[_currentPageIndex];
        var label = page.ContinueLabelFor?.Invoke(_pageStates[_currentPageIndex]) ?? page.ContinueLabel ?? (isLast ? "&Finish" : "&Next >");
        if (cont != 0) Win32.SetWindowTextW(cont, label);
    }

    // An exception from the page (an author callback, a hook) keeps the page, with
    // Continue disabled, instead of terminating the process from inside WndProc.
    private int HandleCommand(nuint wParam, nint lParam)
    {
        try
        {
            return HandleCommandCore(wParam, lParam);
        }
        catch (Exception ex)
        {
            ReportPageError(ex);
            return 0;
        }
    }

    /// <summary>Test hook: how a page error is shown; null shows a message box.</summary>
    internal Action<string>? PageErrorSink { get; set; }

    /// <summary>The last page error reported, for tests.</summary>
    internal string? LastPageError { get; private set; }

    private void ReportPageError(Exception ex)
    {
        var text = $"The installer hit an error on this page: {ex.Message}";
        LastPageError = text;
        Runners.UserCode.LastError = text;
        try
        {
            var cont = _hwnd == 0 ? 0 : Win32.GetDlgItem(_hwnd, IdContinue);
            if (cont != 0) Win32.EnableWindow(cont, 0);
            if (PageErrorSink is { } sink) sink(text);
            else if (!Runners.UserMessages.DialogsDisabled) Win32.MessageBoxW(_hwnd, text, "Error", MB.OK | MB.ICONERROR);
        }
        catch
        {
            // Nothing more can be done from inside the window procedure.
        }
    }

    private int HandleCommandCore(nuint wParam, nint lParam)
    {
        if (_currentPanel is not null && _currentPanel.HandleCommand(wParam, lParam))
        {
            UpdateFooterButtons();
            return 0;
        }

        var controlId = (int)(wParam & 0xFFFF);
        switch (controlId)
        {
            case IdCancel:
                if (_navigationLocked) return 0;
                if (CancelInterceptor?.Invoke() == true) return 0;
                _outcome = Win32WidgetHostOutcome.Cancelled;
                // Tear the window down synchronously on the UI thread (the same
                // path WM_CLOSE takes) instead of only posting WM_QUIT. On the
                // cancel-mid-install path the runner awaits the pipeline after
                // Run() returns, which resumes Dispose() on a thread-pool thread
                // where a DestroyWindow would silently fail and leak the window.
                // Destroying here guarantees teardown happens where the HWND lives.
                Win32.DestroyWindow(_hwnd);
                return 0;

            case IdBack:
                if (_currentPageIndex > 0) ShowPage(_currentPageIndex - 1);
                return 0;

            case IdContinue:
                if (_currentPanel?.CanContinue() == false) return 0;
                if (BeforeLeave?.Invoke(_currentPageIndex) is { } error)
                {
                    Win32.MessageBoxW(_hwnd, error, "Please check your input", MB.OK | MB.ICONWARNING);
                    return 0;
                }
                if (_currentPageIndex + 1 < _pages.Count)
                {
                    ShowPage(_currentPageIndex + 1);
                }
                else
                {
                    _outcome = Win32WidgetHostOutcome.Completed;
                    Win32.PostQuitMessage(0);
                }
                return 0;

            default:
                return 0;
        }
    }

    private void OnBrowseRequested(Win32WidgetInstance instance)
    {
        // FolderPicker → reuse the existing IFileOpenDialog wrapper. FilePicker has
        // no dialog yet; ignoring the request is acceptable because users can
        // still type a path into the EDIT.
        if (instance.Widget is not FolderPicker) return;
        var path = FolderPickerDialog.ShowFolderPicker(_hwnd);
        if (path is null) return;

        Win32.SetWindowTextW(instance.PrimaryHwnd, path);
        // The EN_CHANGE notification from SetWindowTextW propagates through
        // the event pump which writes the new path into PageState.
    }

    private void RunMessageLoop()
    {
        MSG msg = default;
        while (Win32.GetMessageW(out msg, 0, 0, 0) > 0)
        {
            // Try the accelerator table first so Alt+C/B/N/F reaches the
            // footer buttons before IsDialogMessage consumes the key.
            if (_acceleratorTable != 0 && Win32.TranslateAcceleratorW(_hwnd, _acceleratorTable, in msg) != 0)
                continue;
            if (Win32.IsDialogMessageW(_hwnd, in msg))
                continue;
            Win32.TranslateMessage(in msg);
            Win32.DispatchMessageW(in msg);
        }
    }

    private int ScaleDesign(int designValue) => Win32DpiAware.Scale(designValue, EffectiveDpi());

    private int EffectiveDpi()
    {
        if (_dpi > 0) return _dpi;
        return _hwnd != 0 ? Win32DpiAware.GetDpiForWindowOrDefault(_hwnd) : Win32DpiAware.BaseDpi;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WndProc(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        // An exception leaving an [UnmanagedCallersOnly] method ends the process at once.
        try
        {
            return WndProcCore(hwnd, msg, wParam, lParam);
        }
        catch (Exception ex)
        {
            s_activeOnThread?.ReportPageError(ex);
            return 0;
        }
    }

    private static nint WndProcCore(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        var host = s_activeOnThread;
        switch (msg)
        {
            case WM.COMMAND:
                if (host is not null) return host.HandleCommand(wParam, lParam);
                return 0;
            case WM_INSTELLA_INVOKE:
                host?.DrainPendingInvokes();
                return 0;
            case WM_DPICHANGED:
                host?.HandleDpiChanged(wParam, lParam);
                return 0;
            case WM.CLOSE:
                // While the pipeline rolls back the window must stay up; the owner closes it.
                if (host?.CancelInterceptor?.Invoke() == true)
                    return 0;
                if (host is not null) host._outcome = Win32WidgetHostOutcome.Cancelled;
                Win32.DestroyWindow(hwnd);
                return 0;
            case WM.DESTROY:
                // The window and its children are gone; zero the cached handles
                // so a later Dispose() doesn't attempt a cross-thread
                // DestroyWindow on a stale handle. WM_QUIT is posted only to end
                // this host's own running loop (Cancel, WM_CLOSE). The Finish
                // path ends the loop without DestroyWindow, and Dispose destroys
                // the window afterwards (as does a headless host): a WM_QUIT
                // posted then stays queued, and the NEXT window on this thread
                // (the wizard after the scope page) closed before it was shown.
                if (host is not null)
                {
                    host._hwnd = 0;
                    host._previewBannerHwnd = 0;
                    if (host._loopRunning) Win32.PostQuitMessage(0);
                }
                return 0;
            default:
                return Win32.DefWindowProcW(hwnd, msg, wParam, lParam);
        }
    }
}
