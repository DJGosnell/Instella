using System.Security.Cryptography;

namespace Instella.Core.Internal;

/// <summary>
/// Shared reader for the Instella appended-payload footer (format v3, 80 bytes).
/// Used by both the installer and the build task to locate and verify the embedded payload.
/// </summary>
/// <remarks>
/// Footer v3 layout (80 bytes, little-endian):
///   [0..8)   manifest_offset  (long)
///   [8..12)  manifest_length  (int)
///   [12..16) config_length    (int)   - 0 if none
///   [16..24) archive_offset   (long)  - 0 if none
///   [24..32) archive_length   (long)  - 0 if none
///   [32..64) payload_sha256   (32 bytes) - see <see cref="ComputePayloadHash"/>
///   [64..68) format_version   (int)   - 3
///   [68..72) flags            (int)   - bit 0 has archive, bit 1 has config; others reserved
///   [72..80) magic "INSTELLA" (8 bytes ASCII)
///
/// The writer pads the payload so the finished file length is a multiple of 8: <c>signtool</c>
/// then adds no padding and the certificate table starts exactly at the footer's end. The hash
/// treats the PE <c>CheckSum</c> and security directory entry as zeros, the same fields
/// Authenticode excludes, so it is identical before and after signing.
/// </remarks>
internal static class PayloadFooterReader
{
    public const int FooterSize = 80;
    public const int FormatVersion = 3;

    public const int FlagHasArchive = 1;
    public const int FlagHasConfig = 2;
    private const int KnownFlags = FlagHasArchive | FlagHasConfig;
    private const int MaxPlausibleFormatVersion = 255;

    /// <summary>Most zero bytes a signing tool may have padded after the footer.</summary>
    private const int MaxTrailingPadding = 7;

    /// <summary>
    /// Sanity cap on the manifest length. A corrupt footer must not be able to make the
    /// reader allocate gigabytes before the integrity check has even run.
    /// </summary>
    public const int MaxManifestLength = 16 * 1024 * 1024;
    internal static readonly byte[] MagicBytes = "INSTELLA"u8.ToArray();

    /// <summary>
    /// The logical end of the payload: the Authenticode certificate table's offset when the
    /// file is a signed PE (the table sits after the footer), otherwise the file length.
    /// </summary>
    public static long GetPayloadEndOffset(Stream stream)
    {
        var (certificateTable, _) = PeLayout.ReadCertificateTable(stream);
        return certificateTable > 0 ? certificateTable : stream.Length;
    }

    /// <summary>
    /// Reads the footer that ends at <paramref name="payloadEnd"/> (or up to
    /// <see cref="MaxTrailingPadding"/> zero bytes before it).
    /// </summary>
    /// <returns>
    /// The footer; its <see cref="PayloadFooter.Status"/> tells a file with no footer (a lite
    /// installer's stub, a dev build) from a damaged one and from one written by a newer Instella.
    /// </returns>
    public static PayloadFooter ReadFooter(Stream stream, long payloadEnd)
    {
        var footerStart = LocateFooter(stream, payloadEnd);
        if (footerStart < 0)
            return default;

        Span<byte> buffer = stackalloc byte[FooterSize];
        stream.Seek(footerStart, SeekOrigin.Begin);
        if (stream.ReadAtLeast(buffer, FooterSize, throwOnEndOfStream: false) != FooterSize)
            return default;

        var formatVersion = BitConverter.ToInt32(buffer[64..68]);
        var flags = BitConverter.ToInt32(buffer[68..72]);
        // A plausible later version is "newer"; anything else (a v2 footer puts hash bytes here) is damage.
        if (formatVersion is > FormatVersion and <= MaxPlausibleFormatVersion
            || formatVersion == FormatVersion && (flags & ~KnownFlags) != 0)
            return new PayloadFooter { FooterStart = footerStart, Status = FooterStatus.NewerFormat };
        if (formatVersion != FormatVersion)
            return new PayloadFooter { FooterStart = footerStart, Status = FooterStatus.Malformed };

        var footer = new PayloadFooter
        {
            ManifestOffset = BitConverter.ToInt64(buffer[..8]),
            ManifestLength = BitConverter.ToInt32(buffer[8..12]),
            ConfigLength = BitConverter.ToInt32(buffer[12..16]),
            ArchiveOffset = BitConverter.ToInt64(buffer[16..24]),
            ArchiveLength = BitConverter.ToInt64(buffer[24..32]),
            PayloadSha256 = buffer[32..64].ToArray(),
            FooterStart = footerStart,
            Status = FooterStatus.Valid,
        };

        var flagsMatch = ((flags & FlagHasArchive) != 0) == (footer.ArchiveLength > 0)
                         && ((flags & FlagHasConfig) != 0) == (footer.ConfigLength > 0);
        if (!flagsMatch || !IsLayoutValid(footerStart, footer.ManifestOffset, footer.ManifestLength,
                footer.ConfigLength, footer.ArchiveOffset, footer.ArchiveLength))
            return footer with { Status = FooterStatus.Malformed };

        return footer;
    }

    /// <summary>True when an Instella footer's magic ends at (or just before) <paramref name="payloadEnd"/>.</summary>
    public static bool HasFooterMagic(Stream stream, long payloadEnd) => LocateFooter(stream, payloadEnd) >= 0;

    /// <summary>
    /// The footer's start: the magic must end at <paramref name="payloadEnd"/>, or, for files
    /// signed by tools that pad anyway, at most <see cref="MaxTrailingPadding"/> zero bytes
    /// earlier. -1 when there is no footer.
    /// </summary>
    private static long LocateFooter(Stream stream, long payloadEnd)
    {
        var window = (int)Math.Min(payloadEnd, MaxTrailingPadding + MagicBytes.Length);
        if (window < MagicBytes.Length || payloadEnd < FooterSize)
            return -1;

        Span<byte> tail = stackalloc byte[MaxTrailingPadding + 8];
        tail = tail[..window];
        stream.Seek(payloadEnd - window, SeekOrigin.Begin);
        if (stream.ReadAtLeast(tail, window, throwOnEndOfStream: false) != window)
            return -1;

        for (var padding = 0; padding <= window - MagicBytes.Length; padding++)
        {
            var magicEnd = window - padding;
            if (tail[(magicEnd - MagicBytes.Length)..magicEnd].SequenceEqual(MagicBytes))
            {
                var footerStart = payloadEnd - padding - FooterSize;
                return footerStart >= 0 ? footerStart : -1;
            }
            if (tail[magicEnd - 1] != 0)
                return -1;   // only zero bytes may follow the footer
        }
        return -1;
    }

    /// <summary>
    /// Checks that the footer's regions are well-formed and lie, in order, inside
    /// <c>[0, footerStart)</c>: <c>manifest</c>, then <c>config</c>, then (optionally)
    /// <c>archive</c>. Arithmetic is checked, so a corrupt footer cannot overflow into a
    /// plausible-looking range.
    /// </summary>
    internal static bool IsLayoutValid(
        long footerStart, long manifestOffset, int manifestLength, int configLength, long archiveOffset, long archiveLength)
    {
        if (manifestOffset < 0 || manifestLength <= 0 || manifestLength > MaxManifestLength || configLength < 0)
            return false;
        if (archiveLength < 0)
            return false;

        try
        {
            var manifestAndConfigEnd = checked(manifestOffset + manifestLength + configLength);
            if (manifestAndConfigEnd > footerStart)
                return false;

            if (archiveLength == 0)
                return true;

            if (archiveOffset < manifestAndConfigEnd)
                return false;
            return checked(archiveOffset + archiveLength) <= footerStart;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    /// <summary>
    /// SHA-256 of <c>[0, footerStart)</c> with the PE <c>CheckSum</c> and security directory
    /// entry read as zeros. Writer and reader both use this, so signing does not change it.
    /// </summary>
    internal static byte[] ComputePayloadHash(Stream stream, long footerStart)
    {
        var fields = PeLayout.TryLocateMutableFields(stream);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        stream.Seek(0, SeekOrigin.Begin);
        long position = 0;
        while (position < footerStart)
        {
            var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, footerStart - position));
            if (read == 0)
                throw new EndOfStreamException("The payload ended before its footer.");
            ZeroRange(buffer, position, read, fields.ChecksumOffset, 4);
            ZeroRange(buffer, position, read, fields.SecurityDirectoryOffset, 8);
            hasher.AppendData(buffer, 0, read);
            position += read;
        }
        return hasher.GetHashAndReset();
    }

    /// <summary>Zeros the part of <c>[fieldOffset, fieldOffset + fieldLength)</c> inside this buffer.</summary>
    private static void ZeroRange(byte[] buffer, long bufferStart, int count, long fieldOffset, int fieldLength)
    {
        if (fieldOffset < 0)
            return;
        var from = Math.Max(fieldOffset, bufferStart);
        var to = Math.Min(fieldOffset + fieldLength, bufferStart + count);
        if (from < to)
            buffer.AsSpan((int)(from - bufferStart), (int)(to - from)).Clear();
    }

    /// <summary>Verifies the footer's payload hash.</summary>
    /// <exception cref="FooterIntegrityException">The hash does not match.</exception>
    public static void VerifyIntegrity(Stream stream, PayloadFooter footer)
    {
        if (!footer.IsValid)
            throw new InvalidOperationException("Cannot verify integrity of an invalid footer.");

        byte[] actual;
        try
        {
            actual = ComputePayloadHash(stream, footer.FooterStart);
        }
        catch (EndOfStreamException)
        {
            throw new FooterIntegrityException(
                "Unexpected end of stream during integrity verification.", footer.PayloadSha256, []);
        }

        if (!actual.AsSpan().SequenceEqual(footer.PayloadSha256))
            throw new FooterIntegrityException(
                "Installer integrity check failed. The file may be corrupt or tampered with. Please re-download from the original source.",
                footer.PayloadSha256, actual);
    }
}

/// <summary>What <see cref="PayloadFooterReader.ReadFooter"/> found.</summary>
internal enum FooterStatus
{
    /// <summary>No footer: a stub without a payload.</summary>
    Missing,

    /// <summary>A footer is there but its fields are inconsistent: the file is damaged.</summary>
    Malformed,

    /// <summary>A footer of a later format version, or with flags this reader does not know.</summary>
    NewerFormat,

    /// <summary>A well-formed v3 footer (its hash is not yet checked).</summary>
    Valid,
}

/// <summary>
/// Represents the parsed payload footer.
/// </summary>
internal readonly record struct PayloadFooter
{
    public long ManifestOffset { get; init; }
    public int ManifestLength { get; init; }
    public int ConfigLength { get; init; }
    public long ArchiveOffset { get; init; }
    public long ArchiveLength { get; init; }
    public byte[] PayloadSha256 { get; init; }

    /// <summary>Where the 80-byte footer starts; the payload hash covers everything before it.</summary>
    public long FooterStart { get; init; }

    public FooterStatus Status { get; init; }

    public bool IsValid => Status == FooterStatus.Valid;
}
