using Instella.Installer.Build.Tasks;
using Instella.Core.Internal;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests;

/// <summary>
/// Tests for PE-aware footer reading. Validates that GetPayloadEndOffset correctly
/// identifies the logical payload end in both unsigned and signed PE files.
/// </summary>
[TestFixture]
public sealed class PeAwareFooterTests
{
    private string _tempDir = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"instella-pe-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Test]
    public void GetPayloadEndOffset_NonPeFile_ReturnsFileLength()
    {
        var path = Path.Combine(_tempDir, "plain.bin");
        var content = new byte[256];
        content[0] = 0xEF; // Not MZ
        File.WriteAllBytes(path, content);

        using var stream = File.OpenRead(path);
        var result = PayloadFooterReader.GetPayloadEndOffset(stream);
        Assert.That(result, Is.EqualTo(256));
    }

    [Test]
    public void GetPayloadEndOffset_UnsignedPe_ReturnsFileLength()
    {
        var path = Path.Combine(_tempDir, "unsigned.exe");
        var pe = BuildMinimalPe(certTableRva: 0, certTableSize: 0);
        File.WriteAllBytes(path, pe);

        using var stream = File.OpenRead(path);
        var result = PayloadFooterReader.GetPayloadEndOffset(stream);
        Assert.That(result, Is.EqualTo(pe.Length));
    }

    [Test]
    public void GetPayloadEndOffset_SignedPe_ReturnsCertTableOffset()
    {
        var certOffset = 512u;
        var certSize = 128u;
        var path = Path.Combine(_tempDir, "signed.exe");
        var pe = BuildMinimalPe(certTableRva: certOffset, certTableSize: certSize);
        // Append fake cert blob to reach the cert offset
        var full = new byte[certOffset + certSize];
        Array.Copy(pe, full, pe.Length);
        File.WriteAllBytes(path, full);

        using var stream = File.OpenRead(path);
        var result = PayloadFooterReader.GetPayloadEndOffset(stream);
        Assert.That(result, Is.EqualTo(certOffset));
    }

    [Test]
    public void GetPayloadEndOffset_TinyFile_ReturnsFileLength()
    {
        var path = Path.Combine(_tempDir, "tiny.bin");
        File.WriteAllBytes(path, new byte[10]);

        using var stream = File.OpenRead(path);
        var result = PayloadFooterReader.GetPayloadEndOffset(stream);
        Assert.That(result, Is.EqualTo(10));
    }

    [Test]
    public void GetPayloadEndOffset_MalformedPeOffset_ReturnsFileLength()
    {
        var path = Path.Combine(_tempDir, "badpe.exe");
        var pe = new byte[128];
        pe[0] = 0x4D; pe[1] = 0x5A; // MZ
        // e_lfanew pointing past EOF
        BitConverter.GetBytes(99999).CopyTo(pe, 0x3C);
        File.WriteAllBytes(path, pe);

        using var stream = File.OpenRead(path);
        var result = PayloadFooterReader.GetPayloadEndOffset(stream);
        Assert.That(result, Is.EqualTo(128));
    }

    [Test]
    public void FooterRoundTrip_WithSimulatedAuthenticode_IntegrityPasses()
    {
        // Create a payload file, then append a fake cert blob AFTER the footer.
        // The PE-aware reader should find the footer before the cert blob,
        // and the integrity check should pass because the hash covers [0, footerStart).
        var exePath = Path.Combine(_tempDir, "signed-payload.exe");
        var manifestPath = Path.Combine(_tempDir, "manifest.json");
        var archivePath = Path.Combine(_tempDir, "archive.zip");

        File.WriteAllBytes(exePath, new byte[64]);
        File.WriteAllText(manifestPath, """{"appName":"Test","appId":"com.test"}""");
        File.WriteAllBytes(archivePath, new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x01 });

        PayloadAppender.AppendOfflinePayload(exePath, manifestPath, archivePath);

        // Read the file and note the footer position
        var originalBytes = File.ReadAllBytes(exePath);
        var footerStart = originalBytes.Length - PayloadFooterReader.FooterSize;

        // Append a fake 128-byte "cert blob" after the footer
        var fakeCertBlob = new byte[128];
        new Random(42).NextBytes(fakeCertBlob);
        var extended = new byte[originalBytes.Length + fakeCertBlob.Length];
        Array.Copy(originalBytes, extended, originalBytes.Length);
        Array.Copy(fakeCertBlob, 0, extended, originalBytes.Length, fakeCertBlob.Length);
        File.WriteAllBytes(exePath, extended);

        // Read footer using the correct payloadEnd (before cert blob)
        using var stream = File.OpenRead(exePath);
        var payloadEnd = (long)originalBytes.Length; // Simulated cert table offset
        var footer = PayloadFooterReader.ReadFooter(stream, payloadEnd);

        Assert.That(footer.IsValid, Is.True);
        Assert.DoesNotThrow(() => PayloadFooterReader.VerifyIntegrity(stream, footer));
    }

    [Test]
    public void FooterRoundTrip_WithSimulatedAuthenticode_UsingStreamLength_Fails()
    {
        // Same setup as above, but use stream.Length (physical EOF) as payloadEnd.
        // This should fail because the footer would be sought at the wrong offset.
        var exePath = Path.Combine(_tempDir, "signed-bad.exe");
        var manifestPath = Path.Combine(_tempDir, "manifest2.json");
        var archivePath = Path.Combine(_tempDir, "archive2.zip");

        File.WriteAllBytes(exePath, new byte[64]);
        File.WriteAllText(manifestPath, """{"appName":"Test","appId":"com.test"}""");
        File.WriteAllBytes(archivePath, new byte[] { 0x50, 0x4B, 0x03, 0x04 });

        PayloadAppender.AppendOfflinePayload(exePath, manifestPath, archivePath);

        var originalBytes = File.ReadAllBytes(exePath);
        var fakeCertBlob = new byte[128];
        var extended = new byte[originalBytes.Length + fakeCertBlob.Length];
        Array.Copy(originalBytes, extended, originalBytes.Length);
        File.WriteAllBytes(exePath, extended);

        // Using stream.Length as payloadEnd (wrong — it includes the cert blob)
        using var stream = File.OpenRead(exePath);
        var footer = PayloadFooterReader.ReadFooter(stream, stream.Length);

        // Footer won't be found because magic isn't at the right offset
        Assert.That(footer.IsValid, Is.False);
    }

    [TestCase(0)]
    [TestCase(3)]
    [TestCase(7)]
    public void SignedPe_ChecksumSecurityDirectoryAndCertificate_StillVerify(int signerPadding)
    {
        // What signtool does to a finished installer: rewrite CheckSum, point the
        // security directory at the certificate table, and append that table after the footer
        // (after optional zero padding, which the v3 writer makes unnecessary).
        var exePath = WritePeInstaller("signed-v3.exe");
        var unsigned = File.ReadAllBytes(exePath);
        Assert.That(unsigned.Length % 8, Is.Zero, "the writer pads to 8 bytes");

        var certificateOffset = unsigned.Length + signerPadding;
        var signed = new byte[certificateOffset + 256];
        unsigned.CopyTo(signed, 0);
        new Random(7).NextBytes(signed.AsSpan(certificateOffset));
        BitConverter.GetBytes(0xDEADBEEFu).CopyTo(signed, ChecksumOffset);
        BitConverter.GetBytes((uint)certificateOffset).CopyTo(signed, SecurityDirectoryOffset);
        BitConverter.GetBytes(256u).CopyTo(signed, SecurityDirectoryOffset + 4);
        File.WriteAllBytes(exePath, signed);

        using var stream = File.OpenRead(exePath);
        var payloadEnd = PayloadFooterReader.GetPayloadEndOffset(stream);
        var footer = PayloadFooterReader.ReadFooter(stream, payloadEnd);

        Assert.That(payloadEnd, Is.EqualTo(certificateOffset));
        Assert.That(footer.Status, Is.EqualTo(FooterStatus.Valid));
        Assert.DoesNotThrow(() => PayloadFooterReader.VerifyIntegrity(stream, footer));
    }

    [Test]
    public void SignedPe_TamperingOutsideTheExcludedFields_IsDetected()
    {
        var exePath = WritePeInstaller("tampered-v3.exe");
        var bytes = File.ReadAllBytes(exePath);
        bytes[ChecksumOffset + 4] ^= 0xFF;   // the field right after CheckSum is covered
        File.WriteAllBytes(exePath, bytes);

        using var stream = File.OpenRead(exePath);
        var footer = PayloadFooterReader.ReadFooter(stream, stream.Length);

        Assert.Throws<FooterIntegrityException>(() => PayloadFooterReader.VerifyIntegrity(stream, footer));
    }

    [TestCase(4, 1, "NewerFormat")]
    [TestCase(3, 1 | 4, "NewerFormat")]
    [TestCase(2, 1, "Malformed")]
    [TestCase(3, 0, "Malformed")]   // flags disagree with the archive length
    public void FormatVersionAndFlags_AreChecked(int formatVersion, int flags, string expected)
    {
        var exePath = WritePeInstaller("versioned.exe");
        var bytes = File.ReadAllBytes(exePath);
        var footerStart = bytes.Length - PayloadFooterReader.FooterSize;
        BitConverter.GetBytes(formatVersion).CopyTo(bytes, footerStart + 64);
        BitConverter.GetBytes(flags).CopyTo(bytes, footerStart + 68);
        File.WriteAllBytes(exePath, bytes);

        using var stream = File.OpenRead(exePath);
        Assert.That(PayloadFooterReader.ReadFooter(stream, stream.Length).Status.ToString(), Is.EqualTo(expected));
    }

    [Test]
    public void NewerFormat_IsReportedAsBuiltByANewerInstella()
    {
        var exePath = WritePeInstaller("newer.exe");
        var bytes = File.ReadAllBytes(exePath);
        BitConverter.GetBytes(4).CopyTo(bytes, bytes.Length - PayloadFooterReader.FooterSize + 64);
        File.WriteAllBytes(exePath, bytes);

        var ex = Assert.Throws<FooterIntegrityException>(() => Instella.Installer.Runtime.Core.EmbeddedResources.ReadVerifiedFooter(exePath));
        Assert.That(ex!.Message, Does.Contain("newer Instella"));
    }

    private const int ChecksumOffset = 0x98 + 64;
    private const int SecurityDirectoryOffset = 0x128;

    /// <summary>A minimal unsigned PE32+ stub with an offline payload appended.</summary>
    private string WritePeInstaller(string name)
    {
        var exePath = Path.Combine(_tempDir, name);
        var manifestPath = Path.Combine(_tempDir, name + ".json");
        var archivePath = Path.Combine(_tempDir, name + ".zip");
        File.WriteAllBytes(exePath, BuildMinimalPe(certTableRva: 0, certTableSize: 0));
        File.WriteAllText(manifestPath, """{"appName":"Test","appId":"com.test"}""");
        File.WriteAllBytes(archivePath, [0x50, 0x4B, 0x03, 0x04, 0x01, 0x02, 0x03]);
        PayloadAppender.AppendOfflinePayload(exePath, manifestPath, archivePath);
        return exePath;
    }

    /// <summary>
    /// Builds a minimal valid PE32+ file with configurable cert table directory entry.
    /// </summary>
    private static byte[] BuildMinimalPe(uint certTableRva, uint certTableSize)
    {
        var pe = new byte[512]; // Minimum size

        // DOS header
        pe[0] = 0x4D; pe[1] = 0x5A; // MZ magic
        BitConverter.GetBytes(0x80).CopyTo(pe, 0x3C); // e_lfanew

        // PE signature at 0x80
        pe[0x80] = 0x50; pe[0x81] = 0x45; pe[0x82] = 0x00; pe[0x83] = 0x00; // PE\0\0

        // COFF header (20 bytes at 0x84)
        BitConverter.GetBytes((ushort)0x8664).CopyTo(pe, 0x84); // Machine: AMD64
        BitConverter.GetBytes((ushort)0).CopyTo(pe, 0x86); // NumberOfSections: 0
        BitConverter.GetBytes((ushort)240).CopyTo(pe, 0x94); // SizeOfOptionalHeader: 240 bytes (PE32+)

        // Optional header at 0x98
        BitConverter.GetBytes((ushort)0x20b).CopyTo(pe, 0x98); // PE32+ magic

        // Data directories start at 0x98 + 112 = 0x108
        // IMAGE_DIRECTORY_ENTRY_SECURITY is index 4 (each 8 bytes)
        // So at offset 0x108 + 4*8 = 0x108 + 32 = 0x128
        var certEntryOffset = 0x128;
        BitConverter.GetBytes(certTableRva).CopyTo(pe, certEntryOffset);
        BitConverter.GetBytes(certTableSize).CopyTo(pe, certEntryOffset + 4);

        return pe;
    }
}
