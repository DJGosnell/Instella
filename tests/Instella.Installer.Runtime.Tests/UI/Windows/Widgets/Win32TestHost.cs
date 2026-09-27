using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Instella.Installer.Runtime.UI.Windows;

namespace Instella.Installer.Runtime.Tests.UI.Windows.Widgets;

/// <summary>
/// Reusable helpers for Win32 widget tests. Produces an off-screen
/// top-level window suitable as a parent for child controls, and
/// tears it down on fixture exit. Every method no-ops on non-Windows
/// platforms so the test fixture itself compiles everywhere — tests
/// must still gate execution with <c>[Platform("Win")]</c> or
/// <c>OperatingSystem.IsWindows()</c>.
/// </summary>
internal static class Win32TestHost
{
    private const string ClassName = "InstellaWidgetTestHost";
    private static readonly object s_gate = new();
    private static bool s_classRegistered;

    [SupportedOSPlatform("windows")]
    internal static (nint parent, nint hInstance) CreateOffscreenParent()
    {
        var hInstance = Win32.GetModuleHandleW(null);
        EnsureClass(hInstance);

        // Off-screen popup so the test host window never steals focus from
        // the developer's desktop. WS_POPUP avoids a caption bar; not
        // showing the window means no paint messages fire.
        var hwnd = Win32.CreateWindowExW(
            0, ClassName, "instella-widget-test",
            WS.POPUP,
            -32000, -32000, 400, 300,
            0, 0, hInstance, 0);
        return (hwnd, hInstance);
    }

    internal static void DestroyParent(nint hwnd)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (hwnd != 0) Win32.DestroyWindow(hwnd);
    }

    [SupportedOSPlatform("windows")]
    private static unsafe void EnsureClass(nint hInstance)
    {
        lock (s_gate)
        {
            if (s_classRegistered) return;

            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                style = 0,
                lpfnWndProc = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint>)&WndProc,
                hInstance = hInstance,
                hIcon = 0,
                hCursor = Win32.LoadCursorW(0, IDC.ARROW),
                hbrBackground = (nint)(COLOR.BTNFACE + 1),
                lpszClassName = (nint)Marshal.StringToHGlobalUni(ClassName),
            };

            try { _ = Win32.RegisterClassExW(in wc); }
            finally { Marshal.FreeHGlobal(wc.lpszClassName); }

            s_classRegistered = true;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WndProc(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        // Test host is passive: route everything through DefWindowProc.
        return Win32.DefWindowProcW(hwnd, msg, wParam, lParam);
    }
}
