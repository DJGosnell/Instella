using Instella.Core.Internal;

namespace Instella.Installer.Build.Tasks;

/// <summary>
/// Appends manifest, config, and archive payloads to native EXE stubs using footer format v3.
/// </summary>
/// <remarks>
/// Payload format (appended after the stub's bytes):
///   [Manifest JSON][Config JSON][Archive][zero padding][Footer (80 bytes)]
///
/// The footer layout, the hash and the reader live in <see cref="PayloadFooterReader"/>. The
/// padding makes the finished file length a multiple of 8, so <c>signtool</c> appends its
/// certificate table directly after the footer, and the hash skips the PE fields signing
/// rewrites: a signed installer still verifies.
///
/// Config offset is derived: manifest_offset + manifest_length (when config_length > 0).
/// An offline installer has an archive; a lite installer has a config and no archive.
/// </remarks>
public static class PayloadAppender
{
    public const int FooterSize = PayloadFooterReader.FooterSize;

    private static readonly byte[] MagicBytes = PayloadFooterReader.MagicBytes;

    /// <summary>
    /// Makes <paramref name="exePath"/> ready to receive a payload: an existing
    /// Instella payload is stripped, so republishing without a clean build never stacks
    /// payloads. A signed exe that carries an Instella payload is Instella's own earlier
    /// (signed) output: its signature goes with the payload. A signed exe without one was
    /// signed before Instella appended anything and is refused, because appending would
    /// invalidate that signature.
    /// </summary>
    /// <returns>True when an existing payload was stripped.</returns>
    /// <exception cref="InstellaBuildException">INSTELLA0201 (signed input) or INSTELLA0202
    /// (an Instella footer this version cannot read).</exception>
    public static bool PrepareExe(string exePath)
    {
        using var stream = new FileStream(exePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var (certificateTable, _) = PeLayout.ReadCertificateTable(stream);
        var footer = PayloadFooterReader.ReadFooter(stream, PayloadFooterReader.GetPayloadEndOffset(stream));

        if (certificateTable > 0 && footer.Status != FooterStatus.Valid)
            throw new InstellaBuildException("INSTELLA0201",
                $"the installer '{exePath}' is already signed; sign after Instella appends its payload (see docs/distribution-and-signing.md)");

        switch (footer.Status)
        {
            case FooterStatus.Missing:
                return false;
            case FooterStatus.Valid:
                stream.SetLength(footer.ManifestOffset);
                if (certificateTable > 0)
                    ClearSignatureFields(stream);
                return true;
            default:
                throw new InstellaBuildException("INSTELLA0202",
                    $"'{exePath}' ends with an Instella footer this version cannot read ({footer.Status}); delete the publish output and publish again");
        }
    }

    /// <summary>Zeros the PE <c>CheckSum</c> and security directory a signing tool wrote.</summary>
    private static void ClearSignatureFields(Stream stream)
    {
        var fields = PeLayout.TryLocateMutableFields(stream);
        if (!fields.IsPe)
            return;
        stream.Seek(fields.ChecksumOffset, SeekOrigin.Begin);
        stream.Write(new byte[4]);
        stream.Seek(fields.SecurityDirectoryOffset, SeekOrigin.Begin);
        stream.Write(new byte[8]);
    }

    /// <summary>
    /// Appends manifest + archive for an Offline installer, replacing any payload the exe
    /// already carries.
    /// </summary>
    public static void AppendOfflinePayload(string exePath, string manifestJsonPath, string archivePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(exePath);
        ArgumentException.ThrowIfNullOrEmpty(manifestJsonPath);
        ArgumentException.ThrowIfNullOrEmpty(archivePath);

        if (!File.Exists(exePath))
            throw new FileNotFoundException("Executable file not found.", exePath);
        if (!File.Exists(manifestJsonPath))
            throw new FileNotFoundException("Manifest file not found.", manifestJsonPath);
        if (!File.Exists(archivePath))
            throw new FileNotFoundException("Archive file not found.", archivePath);

        var manifestBytes = File.ReadAllBytes(manifestJsonPath);
        PrepareExe(exePath);

        using var stream = new FileStream(exePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None, bufferSize: 81920);
        stream.Seek(0, SeekOrigin.End);

        var manifestOffset = stream.Position;
        stream.Write(manifestBytes);

        // No config for offline installers
        const int configLength = 0;

        var archiveOffset = stream.Position;
        using (var archiveStream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920))
        {
            archiveStream.CopyTo(stream);
        }
        var archiveLength = stream.Position - archiveOffset;

        WriteFooterWithHash(stream, manifestOffset, manifestBytes.Length, configLength, archiveOffset, archiveLength);
    }

    /// <summary>
    /// Appends manifest + config for a Lite installer, replacing any payload the exe already
    /// carries.
    /// </summary>
    public static void AppendLitePayload(string exePath, string manifestJsonPath, string configJsonPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(exePath);
        ArgumentException.ThrowIfNullOrEmpty(manifestJsonPath);
        ArgumentException.ThrowIfNullOrEmpty(configJsonPath);

        if (!File.Exists(exePath))
            throw new FileNotFoundException("Executable file not found.", exePath);
        if (!File.Exists(manifestJsonPath))
            throw new FileNotFoundException("Manifest file not found.", manifestJsonPath);
        if (!File.Exists(configJsonPath))
            throw new FileNotFoundException("Config file not found.", configJsonPath);

        var manifestBytes = File.ReadAllBytes(manifestJsonPath);
        var configBytes = File.ReadAllBytes(configJsonPath);
        PrepareExe(exePath);

        using var stream = new FileStream(exePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None, bufferSize: 81920);
        stream.Seek(0, SeekOrigin.End);

        var manifestOffset = stream.Position;
        stream.Write(manifestBytes);
        stream.Write(configBytes);

        // No archive for lite installers
        const long archiveOffset = 0;
        const long archiveLength = 0;

        WriteFooterWithHash(stream, manifestOffset, manifestBytes.Length, configBytes.Length, archiveOffset, archiveLength);
    }

    /// <summary>
    /// Writes manifest + archive as a standalone payload resource file (for macOS .app bundles).
    /// Instead of appending to a stub, this creates a new file containing just the payload and footer.
    /// </summary>
    public static void WriteOfflinePayloadResource(string payloadPath, string manifestJsonPath, string archivePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(payloadPath);
        ArgumentException.ThrowIfNullOrEmpty(manifestJsonPath);
        ArgumentException.ThrowIfNullOrEmpty(archivePath);

        if (!File.Exists(manifestJsonPath))
            throw new FileNotFoundException("Manifest file not found.", manifestJsonPath);
        if (!File.Exists(archivePath))
            throw new FileNotFoundException("Archive file not found.", archivePath);

        var manifestBytes = File.ReadAllBytes(manifestJsonPath);

        using var stream = new FileStream(payloadPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, bufferSize: 81920);

        var manifestOffset = stream.Position; // 0
        stream.Write(manifestBytes);

        const int configLength = 0;

        var archiveOffset = stream.Position;
        using (var archiveStream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920))
        {
            archiveStream.CopyTo(stream);
        }
        var archiveLength = stream.Position - archiveOffset;

        WriteFooterWithHash(stream, manifestOffset, manifestBytes.Length, configLength, archiveOffset, archiveLength);
    }

    /// <summary>
    /// Writes manifest + config as a standalone payload resource file (for macOS .app bundles, Lite variant).
    /// </summary>
    public static void WriteLitePayloadResource(string payloadPath, string manifestJsonPath, string configJsonPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(payloadPath);
        ArgumentException.ThrowIfNullOrEmpty(manifestJsonPath);
        ArgumentException.ThrowIfNullOrEmpty(configJsonPath);

        if (!File.Exists(manifestJsonPath))
            throw new FileNotFoundException("Manifest file not found.", manifestJsonPath);
        if (!File.Exists(configJsonPath))
            throw new FileNotFoundException("Config file not found.", configJsonPath);

        var manifestBytes = File.ReadAllBytes(manifestJsonPath);
        var configBytes = File.ReadAllBytes(configJsonPath);

        using var stream = new FileStream(payloadPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, bufferSize: 81920);

        var manifestOffset = stream.Position; // 0
        stream.Write(manifestBytes);
        stream.Write(configBytes);

        const long archiveOffset = 0;
        const long archiveLength = 0;

        WriteFooterWithHash(stream, manifestOffset, manifestBytes.Length, configBytes.Length, archiveOffset, archiveLength);
    }

    /// <summary>
    /// Checks if the EXE has an Instella payload footer (signed or not).
    /// </summary>
    public static bool HasPayload(string exePath)
    {
        if (!File.Exists(exePath))
            return false;

        using var stream = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return PayloadFooterReader.HasFooterMagic(stream, PayloadFooterReader.GetPayloadEndOffset(stream));
    }

    /// <summary>
    /// Removes the appended payload (and any signature after it) by truncating the file at
    /// the manifest offset.
    /// </summary>
    public static bool RemovePayload(string exePath)
    {
        if (!File.Exists(exePath))
            return false;

        using var stream = new FileStream(exePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var footer = PayloadFooterReader.ReadFooter(stream, PayloadFooterReader.GetPayloadEndOffset(stream));
        if (!footer.IsValid)
            return false;

        stream.SetLength(footer.ManifestOffset);
        return true;
    }

    /// <summary>
    /// Pads to the 8-byte boundary, writes the v3 footer with a zeroed hash, then computes the
    /// payload hash over <c>[0, footer_start)</c> and patches it in. Requires a seekable
    /// read/write stream positioned at the end of the payload.
    /// </summary>
    private static void WriteFooterWithHash(
        Stream stream,
        long manifestOffset, int manifestLength,
        int configLength,
        long archiveOffset, long archiveLength)
    {
        var padding = (int)((8 - (stream.Position + FooterSize) % 8) % 8);
        stream.Write(new byte[padding]);
        var footerStart = stream.Position;

        var flags = (archiveLength > 0 ? PayloadFooterReader.FlagHasArchive : 0)
                    | (configLength > 0 ? PayloadFooterReader.FlagHasConfig : 0);

        Span<byte> footer = stackalloc byte[FooterSize];
        BitConverter.TryWriteBytes(footer[0..], manifestOffset);                    // [0..8)
        BitConverter.TryWriteBytes(footer[8..], manifestLength);                    // [8..12)
        BitConverter.TryWriteBytes(footer[12..], configLength);                     // [12..16)
        BitConverter.TryWriteBytes(footer[16..], archiveOffset);                    // [16..24)
        BitConverter.TryWriteBytes(footer[24..], archiveLength);                    // [24..32)
        footer[32..64].Clear();                                                      // [32..64) hash, patched below
        BitConverter.TryWriteBytes(footer[64..], PayloadFooterReader.FormatVersion); // [64..68)
        BitConverter.TryWriteBytes(footer[68..], flags);                            // [68..72)
        MagicBytes.CopyTo(footer[72..]);                                             // [72..80)
        stream.Write(footer);
        stream.Flush();

        var hash = PayloadFooterReader.ComputePayloadHash(stream, footerStart);
        stream.Seek(footerStart + 32, SeekOrigin.Begin);
        stream.Write(hash);
        stream.Flush();
    }
}
