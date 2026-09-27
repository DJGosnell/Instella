using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Instella.Installer.Runtime.UI.Windows;

/// <summary>
/// Describes what action the user picked from <see cref="Win32ManagerUI"/>.
/// </summary>
internal enum ManagerAction
{
    None = 0,
    Uninstall = 1,
    Update = 2,
    Repair = 3,
}

/// <summary>
/// Simple Win32 window shown when the installed <c>instella.exe</c> is
/// launched with no arguments. Displays application info and offers three
/// actions (Uninstall / Check for Updates / Repair). Reuses the same visual
/// polish as the wizard's <c>Win32WidgetHost</c> — Segoe UI Variable, Explorer
/// common-control theme, DWM rounded corners + dark titlebar on Win11.
/// </summary>
internal sealed class Win32ManagerUI
{
    private const int WindowWidth = 520;
    private const int WindowHeight = 420;
    private const string WindowClassName = "InstellaManagerClass";

    // Child control IDs
    private const int IdBtnUninstall = 101;
    private const int IdBtnUpdate = 102;
    private const int IdBtnRepair = 103;
    private const int IdBtnClose = 104;

    private readonly string _appName;
    private readonly string _version;
    private readonly string _installPath;
    private readonly string? _serverUrl;
    private readonly bool _updateAvailable;
    private readonly bool _repairAvailable;

    private nint _hwnd;
    private nint _hInstance;
    private nint _fontBody;
    private nint _fontHeader;
    private nint _fontSubheader;
    private ManagerAction _result = ManagerAction.None;

    private static Win32ManagerUI? s_instance;

    public Win32ManagerUI(
        string appName,
        string version,
        string installPath,
        string? serverUrl,
        bool updateAvailable,
        bool repairAvailable)
    {
        _appName = appName;
        _version = version;
        _installPath = installPath;
        _serverUrl = serverUrl;
        _updateAvailable = updateAvailable;
        _repairAvailable = repairAvailable;
    }

    public ManagerAction Run()
    {
        s_instance = this;
        _hInstance = Win32.GetModuleHandleW(null);

        // Common controls init — harmless if already done by an earlier UI.
        var icc = new INITCOMMONCONTROLSEX
        {
            dwSize = (uint)Marshal.SizeOf<INITCOMMONCONTROLSEX>(),
            dwICC = ICC.PROGRESS_CLASS,
        };
        Win32.InitCommonControlsEx(in icc);

        CreateFonts();
        RegisterWindowClass();
        CreateMainWindow();

        Win32.ShowWindow(_hwnd, SW.SHOW);
        Win32.UpdateWindow(_hwnd);

        RunMessageLoop();

        if (_fontBody != 0) Win32.DeleteObject(_fontBody);
        if (_fontHeader != 0) Win32.DeleteObject(_fontHeader);
        if (_fontSubheader != 0) Win32.DeleteObject(_fontSubheader);
        s_instance = null;

        return _result;
    }

    private void CreateFonts()
    {
        _fontBody = CreateFont("Segoe UI Variable Text", -13, FW.NORMAL);
        _fontSubheader = CreateFont("Segoe UI Variable Text", -12, FW.NORMAL);
        _fontHeader = CreateFont("Segoe UI Variable Display", -28, FW.SEMIBOLD);
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
        for (var i = 0; i < faceName.Length && i < 31; i++)
            lf.lfFaceName[i] = faceName[i];
        return Win32.CreateFontIndirectW(in lf);
    }

    private unsafe void RegisterWindowClass()
    {
        var hIconBig = Win32.LoadImageW(_hInstance, (nint)1, IMAGE.ICON,
            Win32.GetSystemMetrics(SM.CXICON), Win32.GetSystemMetrics(SM.CYICON),
            LR.DEFAULTCOLOR | LR.SHARED);
        var hIconSmall = Win32.LoadImageW(_hInstance, (nint)1, IMAGE.ICON,
            Win32.GetSystemMetrics(SM.CXSMICON), Win32.GetSystemMetrics(SM.CYSMICON),
            LR.DEFAULTCOLOR | LR.SHARED);
        // An exe without an icon shows the Instella logo. The class keeps these for the
        // process's lifetime, so they are never destroyed.
        if (hIconBig == 0) hIconBig = Win32Branding.CreateFallbackIcon(Win32.GetSystemMetrics(SM.CXICON));
        if (hIconSmall == 0) hIconSmall = Win32Branding.CreateFallbackIcon(Win32.GetSystemMetrics(SM.CXSMICON));

        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)sizeof(WNDCLASSEXW),
            style = 0x0003, // CS_HREDRAW | CS_VREDRAW
            lpfnWndProc = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint>)&WndProc,
            hInstance = _hInstance,
            hIcon = hIconBig,
            hIconSm = hIconSmall,
            hCursor = Win32.LoadCursorW(0, IDC.ARROW),
            hbrBackground = (nint)(COLOR.BTNFACE + 1),
            lpszClassName = (nint)Marshal.StringToHGlobalUni(WindowClassName),
        };

        Win32.RegisterClassExW(in wc);
        Marshal.FreeHGlobal(wc.lpszClassName);
    }

    private void CreateMainWindow()
    {
        // WindowWidth x WindowHeight is the client area the controls are laid out in.
        var (width, height) = Widgets.Win32DpiAware.OuterSizeForClient(
            WindowWidth, WindowHeight, WS.INSTALLER_WINDOW, WS_EX.CONTROLPARENT, Widgets.Win32DpiAware.BaseDpi);
        var screenW = Win32.GetSystemMetrics(SM.CXSCREEN);
        var screenH = Win32.GetSystemMetrics(SM.CYSCREEN);
        var x = (screenW - width) / 2;
        var y = (screenH - height) / 2;

        _hwnd = Win32.CreateWindowExW(
            WS_EX.CONTROLPARENT, WindowClassName, $"Manage {_appName}",
            WS.INSTALLER_WINDOW,
            x, y, width, height,
            0, 0, _hInstance, 0);

        ApplyWindowsElevenPolish(_hwnd);

        // Header: app name (large)
        CreateStatic(_appName, 24, 18, 460, 36, _fontHeader);

        // Version + install path
        CreateStatic($"Version {_version}", 24, 58, 460, 18, _fontSubheader);
        CreateStatic($"Installed in: {_installPath}", 24, 78, 460, 18, _fontSubheader);
        if (!string.IsNullOrEmpty(_serverUrl))
            CreateStatic($"Update server: {_serverUrl}", 24, 98, 460, 18, _fontSubheader);

        // Separator line (static with sunken edge)
        CreateStatic("", 24, 128, 460, 1, _fontBody, extraStyle: WS.BORDER);

        // Action buttons — stacked vertically, full width, command-link-ish
        var btnY = 144;
        const int btnH = 44;
        const int btnSpacing = 10;

        CreateButton("&Uninstall", 24, btnY, 460, btnH, IdBtnUninstall, enabled: true);
        btnY += btnH + btnSpacing;

        // Update and Repair both need the update server; without one they are hidden.
        if (_updateAvailable)
        {
            CreateButton("Check for &Updates", 24, btnY, 460, btnH, IdBtnUpdate, enabled: true);
            btnY += btnH + btnSpacing;
        }

        if (_repairAvailable)
        {
            CreateButton("&Repair", 24, btnY, 460, btnH, IdBtnRepair, enabled: true);
            btnY += btnH + btnSpacing;
        }
        btnY += 6;

        // Footer Close on its own row, full width, so it never overlaps
        // the last action button.
        CreateButton("&Close", 24, btnY, 460, 32, IdBtnClose, enabled: true);
        Win32.SetFocus(GetControlFallback(IdBtnUninstall));
    }

    private nint CreateStatic(string text, int x, int y, int w, int h, nint font, uint extraStyle = 0)
    {
        var hwnd = Win32.CreateWindowExW(
            0, "STATIC", text,
            WS.CHILD | WS.VISIBLE | WS.SS_LEFT | extraStyle,
            x, y, w, h,
            _hwnd, 0, _hInstance, 0);
        if (font != 0) Win32.SendMessageW(hwnd, WM.SETFONT, (nuint)font, 1);
        _ = Win32.SetWindowTheme(hwnd, "Explorer", null);
        return hwnd;
    }

    private nint CreateButton(string text, int x, int y, int w, int h, int id, bool enabled)
    {
        var hwnd = Win32.CreateWindowExW(
            0, "BUTTON", text,
            WS.CHILD | WS.VISIBLE | WS.TABSTOP | WS.BS_PUSHBUTTON,
            x, y, w, h,
            _hwnd, (nint)id, _hInstance, 0);
        if (_fontBody != 0) Win32.SendMessageW(hwnd, WM.SETFONT, (nuint)_fontBody, 1);
        _ = Win32.SetWindowTheme(hwnd, "Explorer", null);
        if (!enabled) Win32.EnableWindow(hwnd, 0);
        return hwnd;
    }

    private nint GetControlFallback(int id) => Win32.GetDlgItem(_hwnd, id);

    private static void ApplyWindowsElevenPolish(nint hwnd)
    {
        if (hwnd == 0) return;
        if (!OperatingSystem.IsWindows()) return;

        var dark = IsSystemDarkMode() ? 1 : 0;
        _ = Win32.DwmSetWindowAttribute(hwnd, DWMWA.USE_IMMERSIVE_DARK_MODE, dark, sizeof(int));

        var corner = DWMWCP.ROUND;
        _ = Win32.DwmSetWindowAttribute(hwnd, DWMWA.WINDOW_CORNER_PREFERENCE, corner, sizeof(int));
    }

    [SupportedOSPlatform("windows")]
    private static bool IsSystemDarkMode()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int light)
                return light == 0;
        }
        catch { }
        return false;
    }

    private void RunMessageLoop()
    {
        MSG msg = default;
        while (Win32.GetMessageW(out msg, 0, 0, 0) > 0)
        {
            if (Win32.IsDialogMessageW(_hwnd, in msg)) continue;
            Win32.TranslateMessage(in msg);
            Win32.DispatchMessageW(in msg);
        }
    }

    private void HandleCommand(int controlId)
    {
        _result = controlId switch
        {
            IdBtnUninstall => ManagerAction.Uninstall,
            IdBtnUpdate => ManagerAction.Update,
            IdBtnRepair => ManagerAction.Repair,
            IdBtnClose => ManagerAction.None,
            _ => _result,
        };

        if (_result != ManagerAction.None || controlId == IdBtnClose)
            Win32.DestroyWindow(_hwnd);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WndProc(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        switch (msg)
        {
            case WM.COMMAND:
                var controlId = (int)(wParam & 0xFFFF);
                var notif = (int)((wParam >> 16) & 0xFFFF);
                if (notif == BN.CLICKED)
                    s_instance?.HandleCommand(controlId);
                return 0;

            case WM.CLOSE:
                Win32.DestroyWindow(hwnd);
                return 0;

            case WM.DESTROY:
                Win32.PostQuitMessage(0);
                return 0;

            default:
                return Win32.DefWindowProcW(hwnd, msg, wParam, lParam);
        }
    }
}
