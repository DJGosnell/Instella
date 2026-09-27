using System;

namespace Instella.Installer.Runtime.UI.Windows.Widgets;

/// <summary>
/// Cascading DPI-awareness opt-in for the installer process. We want
/// per-monitor-v2 so controls scale cleanly when the user drags the
/// window between monitors with different DPI. Older hosts fall back to
/// per-monitor (Win8.1+) or system-aware (Vista+) — anything is better
/// than unaware, which would produce a blurry bitmap-scaled wizard.
/// </summary>
/// <remarks>
/// Call <see cref="EnsureProcessDpiAwareness"/> exactly once, as early
/// as practical in the installer's <c>Main</c>. Calling it after any
/// window has been created is a no-op but harmless. Test code calls it
/// idempotently from setup; the underlying Windows API rejects the
/// second call with <c>ERROR_ACCESS_DENIED</c> which we swallow.
/// </remarks>
internal static class Win32DpiAware
{
    private static int s_initialized;

    /// <summary>Base design DPI used by all layout math in this subsystem.</summary>
    internal const int BaseDpi = 96;

    /// <summary>
    /// Attempt to opt the process in to per-monitor-v2 DPI awareness.
    /// Falls back to per-monitor (shcore) or system-aware (user32) on
    /// older Windows. Safe to call multiple times.
    /// </summary>
    internal static void EnsureProcessDpiAwareness()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (System.Threading.Interlocked.Exchange(ref s_initialized, 1) == 1) return;

        try
        {
            if (Win32.SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT.PER_MONITOR_AWARE_V2))
                return;
        }
        catch (EntryPointNotFoundException) { }

        try
        {
            if (Win32.SetProcessDpiAwareness(PROCESS_DPI.PER_MONITOR_AWARE) == 0)
                return;
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }

        try { _ = Win32.SetProcessDPIAware(); }
        catch (EntryPointNotFoundException) { }
    }

    /// <summary>
    /// Returns the effective DPI for <paramref name="hwnd"/>. Falls back
    /// to the system DPI on pre-1607 Windows, then to <see cref="BaseDpi"/>
    /// if both native calls are unavailable.
    /// </summary>
    internal static int GetDpiForWindowOrDefault(nint hwnd)
    {
        if (!OperatingSystem.IsWindows()) return BaseDpi;

        try
        {
            var dpi = Win32.GetDpiForWindow(hwnd);
            if (dpi != 0) return (int)dpi;
        }
        catch (EntryPointNotFoundException) { }

        try
        {
            var dpi = Win32.GetDpiForSystem();
            if (dpi != 0) return (int)dpi;
        }
        catch (EntryPointNotFoundException) { }

        return BaseDpi;
    }

    /// <summary>
    /// Scale a 96-DPI-designed pixel value to the given DPI using
    /// banker's-safe rounding. Guarantees non-zero results for non-zero
    /// inputs so we don't collapse a 1 px border to 0 px at sub-100% DPI.
    /// </summary>
    /// <summary>
    /// The outer window size whose client area is <paramref name="clientWidth"/> by
    /// <paramref name="clientHeight"/>. Layout code places controls in client coordinates, so
    /// a window created with the client size as its outer size loses the frame and title bar
    /// from the right and bottom (the footer buttons ended up off-centre).
    /// </summary>
    internal static (int Width, int Height) OuterSizeForClient(int clientWidth, int clientHeight, uint style, uint exStyle, int dpi)
    {
        var rect = new RECT { left = 0, top = 0, right = clientWidth, bottom = clientHeight };
        if (!Win32.AdjustWindowRectExForDpi(ref rect, style, false, exStyle, (uint)(dpi > 0 ? dpi : BaseDpi)))
            return (clientWidth, clientHeight);
        return (rect.right - rect.left, rect.bottom - rect.top);
    }

    internal static int Scale(int designPixels96, int dpi)
    {
        if (designPixels96 == 0) return 0;
        if (dpi <= 0) dpi = BaseDpi;
        var scaled = (int)Math.Round(designPixels96 * (double)dpi / BaseDpi, MidpointRounding.AwayFromZero);
        return designPixels96 > 0 ? Math.Max(1, scaled) : Math.Min(-1, scaled);
    }
}
