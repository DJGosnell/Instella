using System;
using System.Buffers.Binary;

namespace Instella.Installer.Runtime.UI.Windows;

/// <summary>
/// Win32 helpers for Instella's own branding: the fallback window icon for an installer exe
/// that carries no icon (no <c>ApplicationIcon</c> and no <c>WithIcon</c> .ico to stamp), and
/// the dialog colour images are blended onto.
/// </summary>
internal static class Win32Branding
{
    /// <summary>
    /// An icon of about <paramref name="size"/> pixels made from the embedded Instella .ico, or 0.
    /// The caller owns it (<see cref="Win32.DestroyIcon"/>).
    /// </summary>
    internal static unsafe nint CreateFallbackIcon(int size)
    {
        if (!OperatingSystem.IsWindows()) return 0;
        var ico = InstellaBranding.ReadIcon();
        if (ico is null || SelectImage(ico, size) is not { } image) return 0;
        fixed (byte* bits = &ico[image.Offset])
            return Win32.CreateIconFromResourceEx(bits, (uint)image.Length, true, 0x00030000, size, size, 0);
    }

    /// <summary>
    /// The image in an .ico file to draw at <paramref name="size"/> pixels: the smallest that is
    /// at least that big, else the biggest. Null for a malformed file.
    /// </summary>
    internal static (int Offset, int Length)? SelectImage(ReadOnlySpan<byte> ico, int size)
    {
        // ICONDIR: reserved, type (1 = icon), count; then 16-byte ICONDIRENTRYs.
        if (ico.Length < 6 || BinaryPrimitives.ReadUInt16LittleEndian(ico[2..]) != 1) return null;
        var count = BinaryPrimitives.ReadUInt16LittleEndian(ico[4..]);
        if (count == 0 || ico.Length < 6 + (16 * count)) return null;

        (int Offset, int Length, int Size)? best = null;
        for (var i = 0; i < count; i++)
        {
            var entry = ico.Slice(6 + (16 * i), 16);
            var width = entry[0] == 0 ? 256 : entry[0];   // 0 means 256
            var length = BinaryPrimitives.ReadInt32LittleEndian(entry[8..]);
            var offset = BinaryPrimitives.ReadInt32LittleEndian(entry[12..]);
            if (length <= 0 || offset < 0 || (long)offset + length > ico.Length) return null;

            var better = best is not { } b
                || (width >= size ? b.Size < size || width < b.Size : b.Size < size && width > b.Size);
            if (better) best = (offset, length, width);
        }
        return best is { } chosen ? (chosen.Offset, chosen.Length) : null;
    }

    /// <summary>COLOR_BTNFACE, the wizard's background, as opaque ARGB for GDI+.</summary>
    internal static uint DialogBackgroundArgb()
    {
        if (!OperatingSystem.IsWindows()) return 0xFFF0F0F0;
        var bgr = Win32.GetSysColor(COLOR.BTNFACE);
        return 0xFF000000 | ((bgr & 0xFF) << 16) | (bgr & 0xFF00) | ((bgr >> 16) & 0xFF);
    }
}
