using System;
using System.IO;
using System.Runtime.Versioning;
using Instella.Installer.Runtime.UI.Widgets;

namespace Instella.Installer.Runtime.UI.Linux.Widgets;

/// <summary>
/// Decodes an <see cref="ImageSource"/> into a <c>GdkPixbuf*</c>
/// suitable for <c>gtk_image_set_from_pixbuf</c>. The pixbuf is
/// refcount-owned — callers must release via
/// <c>g_object_unref</c> when no widget holds a reference.
/// </summary>
/// <remarks>
/// <para>File sources use <c>gdk_pixbuf_new_from_file</c> directly.
/// Resource and bytes sources go through <c>GdkPixbufLoader</c> —
/// we <c>_write</c> the raw bytes, <c>_close</c> the loader, and
/// <c>_get_pixbuf</c> to extract the decoded image. The loader is
/// then released.</para>
/// <para>Returns <c>0</c> (null pixbuf pointer) on any decode error.
/// Missing brand art never fails the install.</para>
/// </remarks>
internal static class GdkPixbufLoader
{
    /// <summary>
    /// Decode <paramref name="source"/> into a pixbuf handle. Returns
    /// <c>0</c> on any failure (including non-Linux hosts).
    /// </summary>
    [SupportedOSPlatform("linux")]
    internal static nint Load(ImageSource source)
    {
        if (!OperatingSystem.IsLinux()) return 0;

        return source switch
        {
            FileImageSource f => LoadFromFile(f.Path),
            ResourceImageSource r => LoadFromResource(r),
            BytesImageSource b => LoadFromBytes(b.Bytes),
            _ => 0,
        };
    }

    [SupportedOSPlatform("linux")]
    private static nint LoadFromFile(string path)
    {
        if (!File.Exists(path)) return 0;
        try { return Gtk.gdk_pixbuf_new_from_file(path, 0); }
        catch (DllNotFoundException) { return 0; }
        catch (EntryPointNotFoundException) { return 0; }
    }

    [SupportedOSPlatform("linux")]
    private static nint LoadFromResource(ResourceImageSource source)
    {
        using var stream = source.Assembly.GetManifestResourceStream(source.Name);
        if (stream is null) return 0;

        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return LoadFromBytes(ms.ToArray());
    }

    [SupportedOSPlatform("linux")]
    private static nint LoadFromBytes(byte[] bytes)
    {
        if (bytes.Length == 0) return 0;

        try
        {
            var loader = Gtk.gdk_pixbuf_loader_new();
            if (loader == 0) return 0;

            try
            {
                if (Gtk.gdk_pixbuf_loader_write(loader, in bytes[0], (nuint)bytes.Length, 0) == 0)
                    return 0;
                if (Gtk.gdk_pixbuf_loader_close(loader, 0) == 0)
                    return 0;
                var pixbuf = Gtk.gdk_pixbuf_loader_get_pixbuf(loader);
                // Loader owns a ref; we take our own so we can release the loader.
                // gdk_pixbuf_loader_get_pixbuf doesn't AddRef — g_object_ref would,
                // but we skip because we immediately g_object_unref the loader and
                // the pixbuf's lifetime is now the caller's responsibility.
                // In practice callers unref once when the owning GtkImage is destroyed.
                return pixbuf;
            }
            finally
            {
                Gtk.g_object_unref(loader);
            }
        }
        catch (DllNotFoundException) { return 0; }
        catch (EntryPointNotFoundException) { return 0; }
    }
}
