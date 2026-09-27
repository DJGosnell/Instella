using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Instella.Installer.Runtime.UI.Widgets;

namespace Instella.Installer.Runtime.UI.MacOS.Widgets;

/// <summary>
/// Decodes an <see cref="ImageSource"/> into an <c>NSImage*</c>
/// retained for the caller. Returns <c>0</c> on any failure.
/// </summary>
/// <remarks>
/// <para>File sources use <c>-[NSImage initWithContentsOfFile:]</c>.
/// Resource and bytes sources go through
/// <c>+[NSData dataWithBytes:length:]</c> then
/// <c>-[NSImage initWithData:]</c>. NSData is autoreleased by its
/// class factory so we don't release it explicitly; the returned
/// NSImage retains its own data copy.</para>
/// </remarks>
internal static class NSImageLoader
{
    /// <summary>
    /// Decode <paramref name="source"/> into an NSImage handle.
    /// Returns <c>0</c> on any failure (including non-macOS hosts).
    /// Callers must <c>release</c> the returned pointer when done.
    /// </summary>
    [SupportedOSPlatform("macos")]
    internal static nint Load(ImageSource source)
    {
        if (!OperatingSystem.IsMacOS()) return 0;

        return source switch
        {
            FileImageSource f => LoadFromFile(f.Path),
            ResourceImageSource r => LoadFromResource(r),
            BytesImageSource b => LoadFromBytes(b.Bytes),
            _ => 0,
        };
    }

    [SupportedOSPlatform("macos")]
    private static nint LoadFromFile(string path)
    {
        if (!File.Exists(path)) return 0;
        try
        {
            var nsPath = NS.StringNew(path);
            try
            {
                var image = ObjC.msgSend(ObjC.msgSend(NS.Class("NSImage"), NS.Sel("alloc")),
                    NS.Sel("initWithContentsOfFile:"), nsPath);
                return image;
            }
            finally { NS.Release(nsPath); }
        }
        catch (DllNotFoundException) { return 0; }
        catch (EntryPointNotFoundException) { return 0; }
    }

    [SupportedOSPlatform("macos")]
    private static nint LoadFromResource(ResourceImageSource source)
    {
        using var stream = source.Assembly.GetManifestResourceStream(source.Name);
        if (stream is null) return 0;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return LoadFromBytes(ms.ToArray());
    }

    [SupportedOSPlatform("macos")]
    private static nint LoadFromBytes(byte[] bytes)
    {
        if (bytes.Length == 0) return 0;

        try
        {
            var buffer = Marshal.AllocHGlobal(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, buffer, bytes.Length);
                var data = ObjC.msgSend(NS.Class("NSData"), NS.Sel("dataWithBytes:length:"),
                    buffer, (nint)bytes.Length);
                if (data == 0) return 0;
                var image = ObjC.msgSend(ObjC.msgSend(NS.Class("NSImage"), NS.Sel("alloc")),
                    NS.Sel("initWithData:"), data);
                return image;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        catch (DllNotFoundException) { return 0; }
        catch (EntryPointNotFoundException) { return 0; }
    }
}
