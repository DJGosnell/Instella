using Instella.Installer.Build.Tasks;
using Instella.Core.Internal;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests;

[TestFixture]
public sealed class PayloadIntegrityTests
{
    private string _tempDir = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"instella-integrity-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Test]
    public void VerifyIntegrity_ValidPayload_Passes()
    {
        var exePath = CreatePayload();

        using var stream = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var footer = PayloadFooterReader.ReadFooter(stream, stream.Length);
        Assert.That(footer.IsValid, Is.True);

        Assert.DoesNotThrow(() => PayloadFooterReader.VerifyIntegrity(stream, footer));
    }

    [Test]
    public void VerifyIntegrity_FlippedByteInStub_Fails()
    {
        var exePath = CreatePayload();
        FlipByte(exePath, 2); // Flip a byte in the stub region

        using var stream = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var footer = PayloadFooterReader.ReadFooter(stream, stream.Length);
        Assert.That(footer.IsValid, Is.True);

        Assert.Throws<FooterIntegrityException>(() =>
            PayloadFooterReader.VerifyIntegrity(stream, footer));
    }

    [Test]
    public void VerifyIntegrity_FlippedByteInManifest_Fails()
    {
        var exePath = CreatePayload();
        // Manifest starts after the 64-byte stub
        FlipByte(exePath, 65);

        using var stream = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var footer = PayloadFooterReader.ReadFooter(stream, stream.Length);
        Assert.That(footer.IsValid, Is.True);

        Assert.Throws<FooterIntegrityException>(() =>
            PayloadFooterReader.VerifyIntegrity(stream, footer));
    }

    [Test]
    public void VerifyIntegrity_FlippedByteInArchive_Fails()
    {
        var exePath = CreatePayload();
        var bytes = File.ReadAllBytes(exePath);
        // Archive is between manifest and footer. Flip a byte somewhere in the middle.
        var archiveArea = bytes.Length - PayloadFooterReader.FooterSize - 3;
        FlipByte(exePath, archiveArea);

        using var stream = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var footer = PayloadFooterReader.ReadFooter(stream, stream.Length);
        Assert.That(footer.IsValid, Is.True);

        Assert.Throws<FooterIntegrityException>(() =>
            PayloadFooterReader.VerifyIntegrity(stream, footer));
    }

    [Test]
    public void VerifyIntegrity_ZeroedHash_Fails()
    {
        var exePath = CreatePayload();
        var bytes = File.ReadAllBytes(exePath);

        // Zero out the payload_sha256 field at footer[32..64]
        var hashOffset = bytes.Length - PayloadFooterReader.FooterSize + 32;
        for (int i = 0; i < 32; i++)
            bytes[hashOffset + i] = 0;
        File.WriteAllBytes(exePath, bytes);

        using var stream = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var footer = PayloadFooterReader.ReadFooter(stream, stream.Length);
        Assert.That(footer.IsValid, Is.True);

        Assert.Throws<FooterIntegrityException>(() =>
            PayloadFooterReader.VerifyIntegrity(stream, footer));
    }

    [Test]
    public void VerifyIntegrity_HashFromDifferentFile_Fails()
    {
        // Create two different payloads
        var exePath1 = CreatePayload("stub-one");
        var exePath2 = CreatePayload("stub-two");

        var bytes1 = File.ReadAllBytes(exePath1);
        var bytes2 = File.ReadAllBytes(exePath2);

        // Copy hash from file2 into file1's footer
        var hashOffset1 = bytes1.Length - PayloadFooterReader.FooterSize + 32;
        var hashOffset2 = bytes2.Length - PayloadFooterReader.FooterSize + 32;
        Array.Copy(bytes2, hashOffset2, bytes1, hashOffset1, 32);
        File.WriteAllBytes(exePath1, bytes1);

        using var stream = new FileStream(exePath1, FileMode.Open, FileAccess.Read, FileShare.Read);
        var footer = PayloadFooterReader.ReadFooter(stream, stream.Length);
        Assert.That(footer.IsValid, Is.True);

        Assert.Throws<FooterIntegrityException>(() =>
            PayloadFooterReader.VerifyIntegrity(stream, footer));
    }

    [Test]
    public void VerifyIntegrity_LitePayload_Passes()
    {
        var exePath = CreateLitePayload();

        using var stream = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var footer = PayloadFooterReader.ReadFooter(stream, stream.Length);
        Assert.That(footer.IsValid, Is.True);

        Assert.DoesNotThrow(() => PayloadFooterReader.VerifyIntegrity(stream, footer));
    }

    private string CreatePayload(string stubContent = "stub-content-here")
    {
        var exePath = Path.Combine(_tempDir, $"test-{Guid.NewGuid()}.exe");
        var manifestPath = Path.Combine(_tempDir, $"manifest-{Guid.NewGuid()}.json");
        var archivePath = Path.Combine(_tempDir, $"archive-{Guid.NewGuid()}.zip");

        File.WriteAllBytes(exePath, System.Text.Encoding.UTF8.GetBytes(stubContent).Concat(new byte[64 - stubContent.Length > 0 ? 64 - stubContent.Length : 0]).ToArray());
        File.WriteAllText(manifestPath, """{"appName":"Test","appId":"com.test"}""");
        File.WriteAllBytes(archivePath, new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06 });

        PayloadAppender.AppendOfflinePayload(exePath, manifestPath, archivePath);
        return exePath;
    }

    [Test]
    public void VerifyIntegrity_StandalonePayloadResource_Passes()
    {
        // Test the macOS-style standalone payload file (not appended to a stub)
        var payloadPath = Path.Combine(_tempDir, $"payload-{Guid.NewGuid()}.instella");
        var manifestPath = Path.Combine(_tempDir, $"manifest-{Guid.NewGuid()}.json");
        var archivePath = Path.Combine(_tempDir, $"archive-{Guid.NewGuid()}.zip");

        File.WriteAllText(manifestPath, """{"appName":"MacTest","appId":"com.test.mac"}""");
        File.WriteAllBytes(archivePath, new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x01, 0x02, 0x03 });

        PayloadAppender.WriteOfflinePayloadResource(payloadPath, manifestPath, archivePath);

        using var stream = new FileStream(payloadPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var footer = PayloadFooterReader.ReadFooter(stream, stream.Length);
        Assert.That(footer.IsValid, Is.True);
        Assert.That(footer.ManifestOffset, Is.EqualTo(0)); // No stub prefix
        Assert.DoesNotThrow(() => PayloadFooterReader.VerifyIntegrity(stream, footer));
    }

    private string CreateLitePayload()
    {
        var exePath = Path.Combine(_tempDir, $"test-{Guid.NewGuid()}.exe");
        var manifestPath = Path.Combine(_tempDir, $"manifest-{Guid.NewGuid()}.json");
        var configPath = Path.Combine(_tempDir, $"config-{Guid.NewGuid()}.json");

        File.WriteAllBytes(exePath, new byte[64]);
        File.WriteAllText(manifestPath, """{"appName":"Test","appId":"com.test"}""");
        File.WriteAllText(configPath, """{"serverUrl":"https://example.com"}""");

        PayloadAppender.AppendLitePayload(exePath, manifestPath, configPath);
        return exePath;
    }

    // ---- A damaged installer is fatal; only a footer-less file is "lite" ----

    [Test]
    public void ReadVerifiedFooter_NoFooter_IsALiteInstaller()
    {
        var path = Path.Combine(_tempDir, "lite.exe");
        File.WriteAllBytes(path, new byte[4096]);
        Assert.That(Instella.Installer.Runtime.Core.EmbeddedResources.ReadVerifiedFooter(path).IsValid, Is.False);
    }

    [Test]
    public void ReadVerifiedFooter_ByteFlippedPayload_Throws()
    {
        var exePath = CreatePayload();
        FlipByte(exePath, 2);
        Assert.Throws<FooterIntegrityException>(() => Instella.Installer.Runtime.Core.EmbeddedResources.ReadVerifiedFooter(exePath));
    }

    [Test]
    public void ReadVerifiedFooter_MalformedFooterWithMagic_Throws()
    {
        var exePath = CreatePayload();
        var bytes = File.ReadAllBytes(exePath);
        // Corrupt the manifest offset (first field of the 72-byte footer) but keep the magic.
        BitConverter.GetBytes(long.MaxValue).CopyTo(bytes, bytes.Length - 72);
        File.WriteAllBytes(exePath, bytes);
        Assert.Throws<FooterIntegrityException>(() => Instella.Installer.Runtime.Core.EmbeddedResources.ReadVerifiedFooter(exePath));
    }

    [Test]
    public void ReadVerifiedFooter_IntactPayload_IsValid()
    {
        Assert.That(Instella.Installer.Runtime.Core.EmbeddedResources.ReadVerifiedFooter(CreatePayload()).IsValid, Is.True);
    }

    private static void FlipByte(string path, int offset)
    {
        var bytes = File.ReadAllBytes(path);
        bytes[offset] ^= 0xFF;
        File.WriteAllBytes(path, bytes);
    }
}
