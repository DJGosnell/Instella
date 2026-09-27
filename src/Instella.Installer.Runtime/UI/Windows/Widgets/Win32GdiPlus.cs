using System.Runtime.InteropServices;

namespace Instella.Installer.Runtime.UI.Windows.Widgets;

/// <summary>
/// Minimal GDI+ P/Invoke surface used by
/// <see cref="GdiPlusImageLoader"/>. We need just enough to decode a
/// PNG/JPG from file, resource, or memory, then produce an
/// HBITMAP the Win32 <c>STATIC</c> (SS_BITMAP) control can display.
/// </summary>
/// <remarks>
/// GDI+ is an optional component that ships with every Windows
/// version since XP — it has no redistribution burden. The flat API
/// is a thin wrapper over the GDI+ C++ classes; every call returns
/// a <c>Gdiplus::Status</c> (0 = Ok).
/// </remarks>
internal static partial class Win32GdiPlus
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct GdiplusStartupInput
    {
        public uint GdiplusVersion;
        public nint DebugEventCallback;
        public int SuppressBackgroundThread;
        public int SuppressExternalCodecs;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GdiplusStartupOutput
    {
        public nint NotificationHook;
        public nint NotificationUnhook;
    }

    [LibraryImport("gdiplus.dll")]
    internal static partial int GdiplusStartup(
        out nint token,
        in GdiplusStartupInput input,
        out GdiplusStartupOutput output);

    [LibraryImport("gdiplus.dll")]
    internal static partial void GdiplusShutdown(nint token);

    [LibraryImport("gdiplus.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int GdipLoadImageFromFile(string filename, out nint image);

    [LibraryImport("gdiplus.dll")]
    internal static partial int GdipLoadImageFromStream(nint stream, out nint image);

    [LibraryImport("gdiplus.dll")]
    internal static partial int GdipCreateHBITMAPFromBitmap(nint bitmap, out nint hbmReturn, uint background);

    [LibraryImport("gdiplus.dll")]
    internal static partial int GdipDisposeImage(nint image);

    internal const int PixelFormat32bppARGB = 0x0026200A;
    internal const int InterpolationModeHighQualityBicubic = 7;
    internal const int PixelOffsetModeHighQuality = 2;

    [LibraryImport("gdiplus.dll")]
    internal static partial int GdipCreateBitmapFromScan0(int width, int height, int stride, int format, nint scan0, out nint bitmap);

    [LibraryImport("gdiplus.dll")]
    internal static partial int GdipGetImageGraphicsContext(nint image, out nint graphics);

    [LibraryImport("gdiplus.dll")]
    internal static partial int GdipSetInterpolationMode(nint graphics, int mode);

    [LibraryImport("gdiplus.dll")]
    internal static partial int GdipSetPixelOffsetMode(nint graphics, int mode);

    [LibraryImport("gdiplus.dll")]
    internal static partial int GdipDrawImageRectI(nint graphics, nint image, int x, int y, int width, int height);

    [LibraryImport("gdiplus.dll")]
    internal static partial int GdipDeleteGraphics(nint graphics);

    [LibraryImport("gdiplus.dll")]
    internal static partial int GdipGetImageWidth(nint image, out uint width);

    [LibraryImport("gdiplus.dll")]
    internal static partial int GdipGetImageHeight(nint image, out uint height);

    // CreateStreamOnHGlobal produces an IStream over a copy of a GlobalAlloc'd
    // block. GDI+ accepts any IStream; we use this to wrap in-memory byte[]
    // and embedded-resource buffers without touching disk.
    [LibraryImport("ole32.dll")]
    internal static partial int CreateStreamOnHGlobal(nint hGlobal, int fDeleteOnRelease, out nint ppstm);

    [LibraryImport("kernel32.dll")]
    internal static partial nint GlobalAlloc(uint uFlags, nuint dwBytes);

    [LibraryImport("kernel32.dll")]
    internal static partial nint GlobalFree(nint hMem);

    [LibraryImport("kernel32.dll")]
    internal static partial nint GlobalLock(nint hMem);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GlobalUnlock(nint hMem);

    internal const uint GMEM_MOVEABLE = 0x0002;

    [LibraryImport("gdi32.dll")]
    internal static partial int DeleteObject(nint hObject);

    // IStream Release — we hand the stream to GDI+ which AddRefs it, then
    // Release our own reference. Manual vtable call because we don't want
    // a full IStream interop definition for a single method.
    internal static unsafe void ReleaseIStream(nint pStream)
    {
        if (pStream == 0) return;
        // IUnknown::Release is slot 2 in the vtable (QueryInterface=0, AddRef=1, Release=2).
        var vtbl = *(nint**)pStream;
        ((delegate* unmanaged[Stdcall]<nint, uint>)vtbl[2])(pStream);
    }
}
