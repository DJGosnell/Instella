using System;
using System.IO;
using System.Runtime.InteropServices;
using Instella.Installer.Runtime.UI.Widgets;

namespace Instella.Installer.Runtime.UI.Windows.Widgets;

/// <summary>
/// Decodes an <see cref="ImageSource"/> into a Win32 HBITMAP that can
/// be handed to a <c>STATIC</c> control with <see cref="WS.SS_BITMAP"/>.
/// Owns the GDI+ lifetime via a token ref-count so repeated loads
/// don't re-startup the library and disposal happens when no loader
/// is in flight.
/// </summary>
/// <remarks>
/// Callers receive an <see cref="Win32Bitmap"/> that bundles the
/// HBITMAP with its pixel dimensions. Dispose it when the control
/// that owns it is destroyed — the underlying HBITMAP is a GDI
/// resource, not a managed object, and it leaks if not released.
/// </remarks>
internal static class GdiPlusImageLoader
{
    private static readonly object s_gate = new();
    private static nint s_token;
    private static int s_refCount;

    /// <summary>
    /// Load <paramref name="source"/> into an HBITMAP at native pixel
    /// dimensions. Returns <see langword="null"/> on any decode error —
    /// image load failures never propagate because missing brand art
    /// should not fail an install.
    /// </summary>
    /// <param name="source">The image.</param>
    /// <param name="maxWidth">When positive (with <paramref name="maxHeight"/>), a larger image is
    /// scaled down, keeping its aspect ratio, to fit this box: a STATIC control only crops.</param>
    /// <param name="maxHeight">See <paramref name="maxWidth"/>.</param>
    /// <param name="background">ARGB colour transparent pixels are blended onto: a STATIC control
    /// ignores alpha, so 0 (the default) shows them black.</param>
    internal static Win32Bitmap? Load(ImageSource source, int maxWidth = 0, int maxHeight = 0, uint background = 0)
    {
        if (!OperatingSystem.IsWindows()) return null;

        if (!StartupGdiPlus()) return null;
        try
        {
            var imagePtr = LoadImagePtr(source);
            if (imagePtr == 0) return null;

            try
            {
                if (Win32GdiPlus.GdipGetImageWidth(imagePtr, out var w) != 0) return null;
                if (Win32GdiPlus.GdipGetImageHeight(imagePtr, out var h) != 0) return null;
                var (width, height) = FitWithin((int)w, (int)h, maxWidth, maxHeight);
                var bitmapPtr = imagePtr;
                if ((width, height) != ((int)w, (int)h))
                {
                    bitmapPtr = Scale(imagePtr, width, height);
                    if (bitmapPtr == 0) return null;
                }
                try
                {
                    if (Win32GdiPlus.GdipCreateHBITMAPFromBitmap(bitmapPtr, out var hbmp, background) != 0) return null;
                    if (hbmp == 0) return null;
                    return new Win32Bitmap(hbmp, width, height);
                }
                finally
                {
                    if (bitmapPtr != imagePtr) Win32GdiPlus.GdipDisposeImage(bitmapPtr);
                }
            }
            finally
            {
                Win32GdiPlus.GdipDisposeImage(imagePtr);
            }
        }
        finally
        {
            ShutdownGdiPlus();
        }
    }

    private static nint LoadImagePtr(ImageSource source) => source switch
    {
        FileImageSource f => LoadFromFile(f.Path),
        ResourceImageSource r => LoadFromResource(r),
        BytesImageSource b => LoadFromBytes(b.Bytes),
        _ => 0,
    };

    private static nint LoadFromFile(string path)
    {
        if (!File.Exists(path)) return 0;
        return Win32GdiPlus.GdipLoadImageFromFile(path, out var image) == 0 ? image : 0;
    }

    /// <summary>The size of a <paramref name="width"/> x <paramref name="height"/> image scaled down,
    /// never up, to fit the box; unchanged when the box is not given.</summary>
    internal static (int Width, int Height) FitWithin(int width, int height, int maxWidth, int maxHeight)
    {
        if (maxWidth <= 0 || maxHeight <= 0 || width <= 0 || height <= 0
            || (width <= maxWidth && height <= maxHeight))
            return (width, height);
        var scale = Math.Min((double)maxWidth / width, (double)maxHeight / height);
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    // A new 32bpp ARGB bitmap holding the image drawn at the given size (bicubic).
    private static nint Scale(nint image, int width, int height)
    {
        if (Win32GdiPlus.GdipCreateBitmapFromScan0(width, height, 0, Win32GdiPlus.PixelFormat32bppARGB, 0, out var scaled) != 0)
            return 0;
        if (Win32GdiPlus.GdipGetImageGraphicsContext(scaled, out var graphics) != 0)
        {
            Win32GdiPlus.GdipDisposeImage(scaled);
            return 0;
        }
        try
        {
            Win32GdiPlus.GdipSetInterpolationMode(graphics, Win32GdiPlus.InterpolationModeHighQualityBicubic);
            Win32GdiPlus.GdipSetPixelOffsetMode(graphics, Win32GdiPlus.PixelOffsetModeHighQuality);
            if (Win32GdiPlus.GdipDrawImageRectI(graphics, image, 0, 0, width, height) == 0)
                return scaled;
        }
        finally
        {
            Win32GdiPlus.GdipDeleteGraphics(graphics);
        }
        Win32GdiPlus.GdipDisposeImage(scaled);
        return 0;
    }

    private static nint LoadFromResource(ResourceImageSource source)
    {
        using var stream = source.Assembly.GetManifestResourceStream(source.Name);
        if (stream is null) return 0;

        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return LoadFromBytes(ms.ToArray());
    }

    private static nint LoadFromBytes(byte[] bytes)
    {
        if (bytes.Length == 0) return 0;

        var hGlobal = Win32GdiPlus.GlobalAlloc(Win32GdiPlus.GMEM_MOVEABLE, (nuint)bytes.Length);
        if (hGlobal == 0) return 0;

        var locked = Win32GdiPlus.GlobalLock(hGlobal);
        if (locked == 0) { Win32GdiPlus.GlobalFree(hGlobal); return 0; }
        Marshal.Copy(bytes, 0, locked, bytes.Length);
        Win32GdiPlus.GlobalUnlock(hGlobal);

        // fDeleteOnRelease=1 transfers ownership of hGlobal to the stream.
        if (Win32GdiPlus.CreateStreamOnHGlobal(hGlobal, 1, out var stream) != 0)
        {
            Win32GdiPlus.GlobalFree(hGlobal);
            return 0;
        }

        try
        {
            return Win32GdiPlus.GdipLoadImageFromStream(stream, out var image) == 0 ? image : 0;
        }
        finally
        {
            Win32GdiPlus.ReleaseIStream(stream);
        }
    }

    private static bool StartupGdiPlus()
    {
        lock (s_gate)
        {
            if (s_refCount > 0) { s_refCount++; return true; }

            var input = new Win32GdiPlus.GdiplusStartupInput
            {
                GdiplusVersion = 1,
                DebugEventCallback = 0,
                SuppressBackgroundThread = 0,
                SuppressExternalCodecs = 0,
            };
            if (Win32GdiPlus.GdiplusStartup(out s_token, in input, out _) != 0)
                return false;
            s_refCount = 1;
            return true;
        }
    }

    private static void ShutdownGdiPlus()
    {
        lock (s_gate)
        {
            if (s_refCount == 0) return;
            s_refCount--;
            if (s_refCount == 0 && s_token != 0)
            {
                Win32GdiPlus.GdiplusShutdown(s_token);
                s_token = 0;
            }
        }
    }
}

/// <summary>
/// An HBITMAP plus its pixel dimensions. Implements
/// <see cref="IDisposable"/> so <c>using</c>-scoped lifetime is
/// natural in render loops — but most callers hold the bitmap for as
/// long as the owning control exists and dispose on control destroy.
/// </summary>
internal sealed class Win32Bitmap : IDisposable
{
    internal nint Handle { get; private set; }
    internal int Width { get; }
    internal int Height { get; }

    internal Win32Bitmap(nint handle, int width, int height)
    {
        Handle = handle;
        Width = width;
        Height = height;
    }

    public void Dispose()
    {
        if (Handle != 0)
        {
            Win32GdiPlus.DeleteObject(Handle);
            Handle = 0;
        }
    }
}
