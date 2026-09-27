using System.Runtime.InteropServices;

namespace Instella.Installer.Runtime.UI.Windows;

internal static partial class Win32
{
    // Window management
    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial ushort RegisterClassExW(in WNDCLASSEXW lpwcx);

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight,
        nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [LibraryImport("user32.dll")]
    internal static partial int ShowWindow(nint hWnd, int nCmdShow);

    [LibraryImport("user32.dll")]
    internal static partial int UpdateWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    internal static partial int GetMessageW(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [LibraryImport("user32.dll")]
    internal static partial int TranslateMessage(in MSG lpMsg);

    [LibraryImport("user32.dll")]
    internal static partial nint DispatchMessageW(in MSG lpMsg);

    // Win10 1607+. The outer window size whose client area is the given rectangle.
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AdjustWindowRectExForDpi(ref RECT lpRect, uint dwStyle,
        [MarshalAs(UnmanagedType.Bool)] bool bMenu, uint dwExStyle, uint dpi);

    [LibraryImport("user32.dll")]
    internal static partial void PostQuitMessage(int nExitCode);

    [LibraryImport("user32.dll")]
    internal static partial nint DefWindowProcW(nint hWnd, uint msg, nuint wParam, nint lParam);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint ExtractIconW(nint hInst, string pszExeFileName, uint nIconIndex);

    [LibraryImport("user32.dll")]
    internal static partial int DestroyIcon(nint hIcon);

    // One image of an .ico file (BMP or PNG data); the caller owns the icon (DestroyIcon).
    [LibraryImport("user32.dll", SetLastError = true)]
    internal static unsafe partial nint CreateIconFromResourceEx(
        byte* presbits, uint dwResSize, [MarshalAs(UnmanagedType.Bool)] bool fIcon, uint dwVer,
        int cxDesired, int cyDesired, uint flags);

    // COLORREF (0x00BBGGRR) of a system colour such as COLOR_BTNFACE.
    [LibraryImport("user32.dll")]
    internal static partial uint GetSysColor(int nIndex);

    [LibraryImport("user32.dll")]
    internal static partial nint CreateAcceleratorTableW([In] ACCEL[] paccel, int cAccel);

    [LibraryImport("user32.dll")]
    internal static partial int DestroyAcceleratorTable(nint hAccel);

    [LibraryImport("user32.dll")]
    internal static partial int TranslateAcceleratorW(nint hWnd, nint hAccTable, in MSG lpMsg);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint SendMessageW(nint hWnd, uint msg, nuint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    internal static partial int PostMessageW(nint hWnd, uint msg, nuint wParam, nint lParam);

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int SetWindowTextW(nint hWnd, string lpString);

    [LibraryImport("user32.dll")]
    internal static partial nint GetDlgItem(nint hDlg, int nIDDlgItem);

    [LibraryImport("user32.dll")]
    internal static partial int EnableWindow(nint hWnd, int bEnable);

    [LibraryImport("user32.dll")]
    internal static partial int DestroyWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    internal static partial int MoveWindow(nint hWnd, int x, int y, int w, int h, int bRepaint);

    [LibraryImport("user32.dll")]
    internal static partial int GetSystemMetrics(int nIndex);

    [LibraryImport("user32.dll")]
    internal static partial nint LoadCursorW(nint hInstance, int lpCursorName);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial nint LoadIconW(nint hInstance, nint lpIconName);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial nint LoadImageW(nint hInst, nint name, uint type, int cx, int cy, uint fuLoad);

    // IsDialogMessage routes Tab/Shift+Tab, arrows, Enter, Esc, and accelerator
    // keys to the appropriate child controls. Without calling this in the
    // message loop, keyboard navigation in a non-dialog top-level window is
    // effectively dead.
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsDialogMessageW(nint hDlg, in MSG lpMsg);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int MessageBoxW(nint hWnd, string lpText, string lpCaption, uint uType);

    [LibraryImport("user32.dll")]
    internal static partial nint SetFocus(nint hWnd);

    [LibraryImport("user32.dll")]
    internal static partial int GetWindowRect(nint hWnd, out RECT lpRect);

    [LibraryImport("user32.dll")]
    internal static partial int SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int GetWindowTextW(nint hWnd, Span<char> lpString, int nMaxCount);

    [LibraryImport("user32.dll")]
    internal static partial int GetWindowTextLengthW(nint hWnd);

    [LibraryImport("user32.dll")]
    internal static partial nint SetParent(nint hWndChild, nint hWndNewParent);

    // Per-monitor-DPI-aware window metrics. Available on Win10 1607+.
    // Returns 0 on unsupported hosts; callers fall back to <see cref="GetDpiForSystem"/>.
    [LibraryImport("user32.dll")]
    internal static partial uint GetDpiForWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    internal static partial uint GetDpiForSystem();

    // Win10 1703+. PER_MONITOR_AWARE_V2 (=DPI_AWARENESS_CONTEXT value -4) gives
    // us automatic non-client area scaling and per-monitor system-metrics.
    // Returns FALSE on older hosts; callers fall back to SetProcessDpiAwareness
    // (shcore) or SetProcessDPIAware (user32).
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetProcessDpiAwarenessContext(nint dpiContext);

    // Win8.1+ fallback. Lives in shcore.dll. Value: PROCESS_PER_MONITOR_DPI_AWARE=2.
    [LibraryImport("shcore.dll")]
    internal static partial int SetProcessDpiAwareness(int value);

    // Win Vista+ final fallback. No arguments.
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetProcessDPIAware();

    // Kernel
    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint GetModuleHandleW(string? lpModuleName);

    // Common controls
    [LibraryImport("comctl32.dll")]
    internal static partial int InitCommonControlsEx(in INITCOMMONCONTROLSEX lpInitCtrls);

    // GDI
    [LibraryImport("gdi32.dll")]
    internal static partial nint CreateFontIndirectW(in LOGFONTW lplf);

    [LibraryImport("gdi32.dll")]
    internal static partial int DeleteObject(nint ho);

    // COM
    [LibraryImport("ole32.dll")]
    internal static partial int CoInitializeEx(nint pvReserved, uint dwCoInit);

    [LibraryImport("ole32.dll")]
    internal static partial void CoUninitialize();

    // Desktop Window Manager — Win10/Win11 visual polish (rounded corners,
    // dark titlebar, system backdrops). Calls are best-effort; on older
    // Windows versions the DWM silently returns E_INVALIDARG.
    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmSetWindowAttribute(nint hwnd, uint attribute, in int pvAttribute, uint cbAttribute);

    // SetWindowTheme(hwnd, "Explorer", NULL) switches Win32 controls to the
    // Explorer visual style (thinner scrollbars, modern hover colors).
    [LibraryImport("uxtheme.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int SetWindowTheme(nint hwnd, string? pszSubAppName, string? pszSubIdList);

    // Helpers
    internal static string GetWindowText(nint hWnd)
    {
        var length = GetWindowTextLengthW(hWnd);
        if (length == 0) return string.Empty;
        Span<char> buffer = stackalloc char[length + 1];
        GetWindowTextW(hWnd, buffer, length + 1);
        return new string(buffer[..length]);
    }
}
