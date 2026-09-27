namespace Instella.Core.Internal;

/// <summary>
/// The few PE header fields the appended-payload format cares about: the two that
/// <c>signtool</c> rewrites when it signs (the optional-header <c>CheckSum</c> and the
/// <c>IMAGE_DIRECTORY_ENTRY_SECURITY</c> data-directory entry), and the certificate table
/// that entry points to. Anything that is not a well-formed PE yields "not found".
/// </summary>
internal static class PeLayout
{
    /// <summary>File offsets of the PE fields Authenticode excludes from its own hash.</summary>
    /// <param name="ChecksumOffset">Offset of the 4-byte <c>CheckSum</c>, or -1 when not a PE.</param>
    /// <param name="SecurityDirectoryOffset">Offset of the 8-byte security directory entry, or -1.</param>
    internal readonly record struct MutableFields(long ChecksumOffset, long SecurityDirectoryOffset)
    {
        public static readonly MutableFields None = new(-1, -1);
        public bool IsPe => ChecksumOffset >= 0;
    }

    /// <summary>
    /// Locates <c>CheckSum</c> (optional header + 64) and the security directory entry
    /// (optional header + 128 for PE32, + 144 for PE32+).
    /// </summary>
    public static MutableFields TryLocateMutableFields(Stream stream)
    {
        if (stream.Length < 64)
            return MutableFields.None;

        Span<byte> dos = stackalloc byte[64];
        stream.Seek(0, SeekOrigin.Begin);
        if (stream.ReadAtLeast(dos, 64, throwOnEndOfStream: false) != 64 || dos[0] != 0x4D || dos[1] != 0x5A)
            return MutableFields.None;

        long peOffset = BitConverter.ToInt32(dos[0x3C..]);
        if (peOffset < 0 || peOffset + 4 + 20 + 2 > stream.Length)
            return MutableFields.None;

        Span<byte> header = stackalloc byte[4 + 20 + 2];   // signature, COFF header, optional-header magic
        stream.Seek(peOffset, SeekOrigin.Begin);
        if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) != header.Length)
            return MutableFields.None;
        if (header[0] != 0x50 || header[1] != 0x45 || header[2] != 0 || header[3] != 0)
            return MutableFields.None;

        var sizeOfOptionalHeader = BitConverter.ToUInt16(header[20..22]);
        var optionalHeader = peOffset + 4 + 20;
        long securityEntry = BitConverter.ToUInt16(header[24..26]) switch
        {
            0x10B => optionalHeader + 128,   // PE32
            0x20B => optionalHeader + 144,   // PE32+
            _ => -1,
        };
        if (securityEntry < 0 || securityEntry + 8 > optionalHeader + sizeOfOptionalHeader || securityEntry + 8 > stream.Length)
            return MutableFields.None;

        return new MutableFields(optionalHeader + 64, securityEntry);
    }

    /// <summary>
    /// The certificate table's file offset and size from the security directory entry, or
    /// (0, 0) when the file is not a PE or is unsigned.
    /// </summary>
    public static (long Offset, long Size) ReadCertificateTable(Stream stream)
    {
        var fields = TryLocateMutableFields(stream);
        if (!fields.IsPe)
            return (0, 0);

        Span<byte> entry = stackalloc byte[8];
        stream.Seek(fields.SecurityDirectoryOffset, SeekOrigin.Begin);
        if (stream.ReadAtLeast(entry, 8, throwOnEndOfStream: false) != 8)
            return (0, 0);

        // For the security directory, "VirtualAddress" is a file offset, not an RVA.
        long offset = BitConverter.ToUInt32(entry[..4]);
        long size = BitConverter.ToUInt32(entry[4..]);
        return offset > 0 && size > 0 && offset < stream.Length ? (offset, size) : (0, 0);
    }
}
