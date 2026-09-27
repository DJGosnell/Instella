using System.Runtime.InteropServices;

namespace Instella.Installer.Build.Tests;

/// <summary>Small PE and icon fixtures for the build-task tests.</summary>
internal static class PeFixtures
{
    private const int OptionalHeader = 0x98;
    private const int SecurityDirectory = OptionalHeader + 144;   // PE32+

    /// <summary>A minimal unsigned PE32+.</summary>
    public static byte[] UnsignedPe()
    {
        var pe = SignedPe()[..512];
        Array.Clear(pe, SecurityDirectory, 8);
        return pe;
    }

    /// <summary>What signtool does: CheckSum, security directory, certificate table appended.</summary>
    public static byte[] SimulateSigning(byte[] file)
    {
        var signed = new byte[file.Length + 256];
        file.CopyTo(signed, 0);
        new Random(5).NextBytes(signed.AsSpan(file.Length));
        BitConverter.GetBytes(0xC0FFEEu).CopyTo(signed, OptionalHeader + 64);
        BitConverter.GetBytes((uint)file.Length).CopyTo(signed, SecurityDirectory);
        BitConverter.GetBytes(256u).CopyTo(signed, SecurityDirectory + 4);
        return signed;
    }

    /// <summary>A minimal PE32+ whose security directory points at a certificate table.</summary>
    public static byte[] SignedPe()
    {
        var pe = new byte[640];
        pe[0] = 0x4D; pe[1] = 0x5A;
        BitConverter.GetBytes(0x80).CopyTo(pe, 0x3C);
        pe[0x80] = 0x50; pe[0x81] = 0x45;
        BitConverter.GetBytes((ushort)0x8664).CopyTo(pe, 0x84);
        BitConverter.GetBytes((ushort)240).CopyTo(pe, 0x94);
        BitConverter.GetBytes((ushort)0x20b).CopyTo(pe, OptionalHeader);
        BitConverter.GetBytes(512u).CopyTo(pe, SecurityDirectory);
        BitConverter.GetBytes(128u).CopyTo(pe, SecurityDirectory + 4);
        return pe;
    }

    /// <summary>Removes an Authenticode signature: zero the security directory, drop the table.</summary>
    public static void StripSignature(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var peOffset = BitConverter.ToInt32(bytes, 0x3C);
        var optionalHeader = peOffset + 24;
        var security = optionalHeader + (BitConverter.ToUInt16(bytes, optionalHeader) == 0x20b ? 144 : 128);
        var tableOffset = BitConverter.ToUInt32(bytes, security);
        if (tableOffset == 0)
            return;
        Array.Clear(bytes, security, 8);
        File.WriteAllBytes(path, bytes.AsSpan(0, (int)tableOffset).ToArray());
    }

    /// <summary>A valid .ico holding one 16x16 PNG image.</summary>
    public static byte[] PngIcon()
    {
        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAYAAAAf8/9hAAAAGklEQVR4nGNgoBAw/v//n4ESwESRZ1ANGAVkAgCUxAMRE0Ot6gAAAABJRU5ErkJggg==");
        var ico = new byte[6 + 16 + png.Length];
        BitConverter.GetBytes((ushort)0).CopyTo(ico, 0);
        BitConverter.GetBytes((ushort)1).CopyTo(ico, 2);
        BitConverter.GetBytes((ushort)1).CopyTo(ico, 4);
        ico[6] = 16; ico[7] = 16;
        BitConverter.GetBytes((ushort)1).CopyTo(ico, 10);
        BitConverter.GetBytes((ushort)32).CopyTo(ico, 12);
        BitConverter.GetBytes((uint)png.Length).CopyTo(ico, 14);
        BitConverter.GetBytes(22u).CopyTo(ico, 18);
        png.CopyTo(ico, 22);
        return ico;
    }

    /// <summary>True when the PE at <paramref name="path"/> has an RT_GROUP_ICON resource with id <paramref name="id"/>.</summary>
    public static bool HasGroupIcon(string path, int id)
    {
        var module = LoadLibraryExW(path, IntPtr.Zero, LoadLibraryAsDatafile | LoadLibraryAsImageResource);
        if (module == IntPtr.Zero)
            return false;
        try
        {
            return FindResourceW(module, id, 14) != IntPtr.Zero;
        }
        finally
        {
            FreeLibrary(module);
        }
    }

    private const uint LoadLibraryAsDatafile = 0x2;
    private const uint LoadLibraryAsImageResource = 0x20;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr FindResourceW(IntPtr module, nint name, nint type);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(IntPtr module);
}
