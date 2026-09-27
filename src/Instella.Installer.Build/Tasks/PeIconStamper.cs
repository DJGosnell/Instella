using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Instella.Installer.Build.Tasks;

/// <summary>
/// Stamps a .ico file into a PE executable's resources (RT_GROUP_ICON +
/// RT_ICON) so Windows Explorer, the taskbar, and the Alt+Tab switcher render
/// the embedded icon. Uses Win32 BeginUpdateResource / UpdateResource /
/// EndUpdateResource — Windows-host-only.
/// </summary>
internal static class PeIconStamper
{
    public static bool IsSupported => OperatingSystem.IsWindows();

    public static void Stamp(string peFilePath, string icoFilePath)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "PE icon stamping requires a Windows build host (Win32 UpdateResource APIs).");

        StampImpl(peFilePath, icoFilePath);
    }

    [SupportedOSPlatform("windows")]
    private static void StampImpl(string peFilePath, string icoFilePath)
    {
        var icoBytes = File.ReadAllBytes(icoFilePath);
        if (icoBytes.Length < 6)
            throw new InvalidDataException($"Icon file too small: {icoFilePath}");

        var reserved = BitConverter.ToUInt16(icoBytes, 0);
        var type = BitConverter.ToUInt16(icoBytes, 2);
        var count = BitConverter.ToUInt16(icoBytes, 4);
        if (reserved != 0 || type != 1 || count == 0)
            throw new InvalidDataException($"Not a valid .ico file: {icoFilePath}");

        const int iconDirEntrySize = 16;
        if (icoBytes.Length < 6 + count * iconDirEntrySize)
            throw new InvalidDataException($"Truncated .ico file: {icoFilePath}");

        var entries = new IconDirEntry[count];
        for (var i = 0; i < count; i++)
        {
            var off = 6 + i * iconDirEntrySize;
            entries[i] = new IconDirEntry
            {
                Width = icoBytes[off + 0],
                Height = icoBytes[off + 1],
                ColorCount = icoBytes[off + 2],
                Reserved = icoBytes[off + 3],
                Planes = BitConverter.ToUInt16(icoBytes, off + 4),
                BitCount = BitConverter.ToUInt16(icoBytes, off + 6),
                BytesInRes = BitConverter.ToUInt32(icoBytes, off + 8),
                ImageOffset = BitConverter.ToUInt32(icoBytes, off + 12),
            };

            if (entries[i].ImageOffset + entries[i].BytesInRes > icoBytes.Length)
                throw new InvalidDataException(
                    $"Icon entry {i} extends past end of file: {icoFilePath}");
        }

        // RT_GROUP_ICON wraps a GRPICONDIR that mirrors ICONDIR but replaces
        // the DWORD image offset with a WORD resource ID pointing at the
        // corresponding RT_ICON entry.
        const int grpIconDirEntrySize = 14;
        var groupIcon = new byte[6 + count * grpIconDirEntrySize];
        WriteUInt16(groupIcon, 0, 0);     // reserved
        WriteUInt16(groupIcon, 2, 1);     // type = icon
        WriteUInt16(groupIcon, 4, count);
        for (var i = 0; i < count; i++)
        {
            var off = 6 + i * grpIconDirEntrySize;
            var e = entries[i];
            groupIcon[off + 0] = e.Width;
            groupIcon[off + 1] = e.Height;
            groupIcon[off + 2] = e.ColorCount;
            groupIcon[off + 3] = e.Reserved;
            WriteUInt16(groupIcon, off + 4, e.Planes);
            WriteUInt16(groupIcon, off + 6, e.BitCount);
            WriteUInt32(groupIcon, off + 8, e.BytesInRes);
            WriteUInt16(groupIcon, off + 12, (ushort)(i + 1));
        }

        // bDeleteExistingResources = false: we want to preserve the stub's
        // RT_MANIFEST (app.manifest), version info, and anything else.
        var hUpdate = BeginUpdateResourceW(peFilePath, false);
        if (hUpdate == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                $"BeginUpdateResource failed for {peFilePath}");

        var committed = false;
        try
        {
            for (var i = 0; i < count; i++)
            {
                var e = entries[i];
                var imageData = new byte[e.BytesInRes];
                Array.Copy(icoBytes, e.ImageOffset, imageData, 0, e.BytesInRes);

                if (!UpdateResourceBytes(hUpdate, RT_ICON, (ushort)(i + 1), imageData))
                    throw new Win32Exception(Marshal.GetLastWin32Error(),
                        $"UpdateResource RT_ICON id={i + 1} failed");
            }

            if (!UpdateResourceBytes(hUpdate, RT_GROUP_ICON, GroupIconName, groupIcon))
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "UpdateResource RT_GROUP_ICON failed");

            if (!EndUpdateResourceW(hUpdate, fDiscard: false))
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    $"EndUpdateResource failed for {peFilePath}");
            committed = true;
        }
        finally
        {
            if (!committed)
                EndUpdateResourceW(hUpdate, fDiscard: true);
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool UpdateResourceBytes(IntPtr hUpdate, IntPtr type, ushort nameId, byte[] data)
    {
        // Pin the buffer so UpdateResourceW can memcpy it into the update
        // handle's in-memory resource table before EndUpdateResource commits.
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            return UpdateResourceW(
                hUpdate,
                type,
                (IntPtr)nameId,
                wLanguage: 0,
                handle.AddrOfPinnedObject(),
                (uint)data.Length);
        }
        finally
        {
            handle.Free();
        }
    }

    private static void WriteUInt16(byte[] buffer, int offset, ushort value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
    }

    private static void WriteUInt32(byte[] buffer, int offset, uint value)
    {
        buffer[offset + 0] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
        buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
        buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    private static readonly IntPtr RT_ICON = (IntPtr)3;
    private static readonly IntPtr RT_GROUP_ICON = (IntPtr)14;

    // Conventional resource ID for the primary icon group.
    private const ushort GroupIconName = 1;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "BeginUpdateResourceW")]
    private static extern IntPtr BeginUpdateResourceW(string pFileName, [MarshalAs(UnmanagedType.Bool)] bool bDeleteExistingResources);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "UpdateResourceW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateResourceW(IntPtr hUpdate, IntPtr lpType, IntPtr lpName, ushort wLanguage, IntPtr lpData, uint cbData);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "EndUpdateResourceW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EndUpdateResourceW(IntPtr hUpdate, [MarshalAs(UnmanagedType.Bool)] bool fDiscard);

    private struct IconDirEntry
    {
        public byte Width;
        public byte Height;
        public byte ColorCount;
        public byte Reserved;
        public ushort Planes;
        public ushort BitCount;
        public uint BytesInRes;
        public uint ImageOffset;
    }
}
