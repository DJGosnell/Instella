using Instella.Installer.Build.Tasks;
using NUnit.Framework;

namespace Instella.Installer.Build.Tests;

[TestFixture]
public sealed class PayloadAppenderTests
{
    private string _tempDir = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"instella-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Test]
    public void AppendOfflinePayload_AppendsDataCorrectly()
    {
        // Arrange
        var exePath = Path.Combine(_tempDir, "test.exe");
        var manifestPath = Path.Combine(_tempDir, "instella.json");
        var archivePath = Path.Combine(_tempDir, "archive.zip");

        var originalExeContent = new byte[] { 0x4D, 0x5A, 0x90, 0x00 }; // MZ header
        var manifestContent = """{"appName":"Test","appId":"com.test"}""";
        var archiveContent = new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x01, 0x02, 0x03 }; // PK header + data

        File.WriteAllBytes(exePath, originalExeContent);
        File.WriteAllText(manifestPath, manifestContent);
        File.WriteAllBytes(archivePath, archiveContent);

        // Act
        PayloadAppender.AppendOfflinePayload(exePath, manifestPath, archivePath);

        // Assert
        Assert.That(PayloadAppender.HasPayload(exePath), Is.True);

        // Verify the file structure
        var finalContent = File.ReadAllBytes(exePath);
        var manifestBytes = System.Text.Encoding.UTF8.GetBytes(manifestContent);
        var unpadded = originalExeContent.Length + manifestBytes.Length + archiveContent.Length + PayloadAppender.FooterSize;
        Assert.That(finalContent.Length, Is.EqualTo(RoundUpTo8(unpadded)), "padded so signtool adds no padding (11.1)");

        // Verify original exe content is preserved
        Assert.That(finalContent.Take(originalExeContent.Length).ToArray(), Is.EqualTo(originalExeContent));
    }

    [Test]
    public void AppendLitePayload_AppendsDataCorrectly()
    {
        // Arrange
        var exePath = Path.Combine(_tempDir, "test.exe");
        var manifestPath = Path.Combine(_tempDir, "instella.json");
        var configPath = Path.Combine(_tempDir, "instella.config");

        var originalExeContent = new byte[] { 0x4D, 0x5A, 0x90, 0x00 };
        var manifestContent = """{"appName":"Test","appId":"com.test"}""";
        var configContent = """{"serverUrl":"https://example.com","packageId":"com.test"}""";

        File.WriteAllBytes(exePath, originalExeContent);
        File.WriteAllText(manifestPath, manifestContent);
        File.WriteAllText(configPath, configContent);

        // Act
        PayloadAppender.AppendLitePayload(exePath, manifestPath, configPath);

        // Assert
        Assert.That(PayloadAppender.HasPayload(exePath), Is.True);

        var finalContent = File.ReadAllBytes(exePath);
        var manifestBytes = System.Text.Encoding.UTF8.GetBytes(manifestContent);
        var configBytes = System.Text.Encoding.UTF8.GetBytes(configContent);
        var unpadded = originalExeContent.Length + manifestBytes.Length + configBytes.Length + PayloadAppender.FooterSize;
        Assert.That(finalContent.Length, Is.EqualTo(RoundUpTo8(unpadded)));
    }

    [Test]
    public void HasPayload_ReturnsFalse_ForFileWithoutPayload()
    {
        // Arrange
        var exePath = Path.Combine(_tempDir, "plain.exe");
        File.WriteAllBytes(exePath, new byte[] { 0x4D, 0x5A, 0x90, 0x00 });

        // Act & Assert
        Assert.That(PayloadAppender.HasPayload(exePath), Is.False);
    }

    [Test]
    public void HasPayload_ReturnsFalse_ForNonexistentFile()
    {
        // Arrange
        var nonexistent = Path.Combine(_tempDir, "nonexistent.exe");

        // Act & Assert
        Assert.That(PayloadAppender.HasPayload(nonexistent), Is.False);
    }

    [Test]
    public void RemovePayload_RemovesAppendedData()
    {
        // Arrange
        var exePath = Path.Combine(_tempDir, "test.exe");
        var manifestPath = Path.Combine(_tempDir, "instella.json");
        var archivePath = Path.Combine(_tempDir, "archive.zip");

        var originalContent = new byte[] { 0x4D, 0x5A, 0x90, 0x00 };

        File.WriteAllBytes(exePath, originalContent);
        File.WriteAllText(manifestPath, """{"appName":"Test"}""");
        File.WriteAllBytes(archivePath, new byte[] { 0x50, 0x4B, 0x03, 0x04 });
        PayloadAppender.AppendOfflinePayload(exePath, manifestPath, archivePath);

        // Act
        var result = PayloadAppender.RemovePayload(exePath);

        // Assert
        Assert.That(result, Is.True);
        Assert.That(PayloadAppender.HasPayload(exePath), Is.False);

        var restoredContent = File.ReadAllBytes(exePath);
        Assert.That(restoredContent, Is.EqualTo(originalContent));
    }

    [Test]
    public void RemovePayload_ReturnsFalse_WhenNoPayload()
    {
        // Arrange
        var exePath = Path.Combine(_tempDir, "plain.exe");
        File.WriteAllBytes(exePath, new byte[] { 0x4D, 0x5A, 0x90, 0x00 });

        // Act & Assert
        Assert.That(PayloadAppender.RemovePayload(exePath), Is.False);
    }

    [Test]
    public void AppendOfflinePayload_ThrowsForNonexistentExe()
    {
        // Arrange
        var nonexistent = Path.Combine(_tempDir, "nonexistent.exe");
        var manifestPath = Path.Combine(_tempDir, "instella.json");
        var archivePath = Path.Combine(_tempDir, "archive.zip");
        File.WriteAllText(manifestPath, "{}");
        File.WriteAllBytes(archivePath, new byte[] { 0x50, 0x4B });

        // Act & Assert
        Assert.Throws<FileNotFoundException>(() =>
            PayloadAppender.AppendOfflinePayload(nonexistent, manifestPath, archivePath));
    }

    [Test]
    public void AppendOfflinePayload_ThrowsForNonexistentManifest()
    {
        // Arrange
        var exePath = Path.Combine(_tempDir, "test.exe");
        var nonexistent = Path.Combine(_tempDir, "nonexistent.json");
        var archivePath = Path.Combine(_tempDir, "archive.zip");
        File.WriteAllBytes(exePath, new byte[] { 0x4D, 0x5A });
        File.WriteAllBytes(archivePath, new byte[] { 0x50, 0x4B });

        // Act & Assert
        Assert.Throws<FileNotFoundException>(() =>
            PayloadAppender.AppendOfflinePayload(exePath, nonexistent, archivePath));
    }

    [Test]
    public void AppendOfflinePayload_ThrowsForNonexistentArchive()
    {
        // Arrange
        var exePath = Path.Combine(_tempDir, "test.exe");
        var manifestPath = Path.Combine(_tempDir, "instella.json");
        var nonexistent = Path.Combine(_tempDir, "nonexistent.zip");
        File.WriteAllBytes(exePath, new byte[] { 0x4D, 0x5A });
        File.WriteAllText(manifestPath, "{}");

        // Act & Assert
        Assert.Throws<FileNotFoundException>(() =>
            PayloadAppender.AppendOfflinePayload(exePath, manifestPath, nonexistent));
    }

    [Test]
    public void FooterRoundTrip_VerifiesV3Layout()
    {
        // Arrange
        var exePath = Path.Combine(_tempDir, "test.exe");
        var manifestPath = Path.Combine(_tempDir, "instella.json");
        var archivePath = Path.Combine(_tempDir, "archive.zip");

        var exeContent = new byte[100];
        var manifestContent = "manifest-test-data";
        var archiveContent = new byte[200];
        new Random(42).NextBytes(archiveContent);

        File.WriteAllBytes(exePath, exeContent);
        File.WriteAllText(manifestPath, manifestContent);
        File.WriteAllBytes(archivePath, archiveContent);

        // Act
        PayloadAppender.AppendOfflinePayload(exePath, manifestPath, archivePath);

        // Assert - read the v3 footer manually (80 bytes)
        var allBytes = File.ReadAllBytes(exePath);
        var footer = allBytes.AsSpan(allBytes.Length - PayloadAppender.FooterSize);

        var manifestOffset = BitConverter.ToInt64(footer[..8]);
        var manifestLength = BitConverter.ToInt32(footer[8..12]);
        var configLength = BitConverter.ToInt32(footer[12..16]);
        var archiveOffset = BitConverter.ToInt64(footer[16..24]);
        var archiveLength = BitConverter.ToInt64(footer[24..32]);
        var payloadHash = footer[32..64].ToArray();
        var formatVersion = BitConverter.ToInt32(footer[64..68]);
        var flags = BitConverter.ToInt32(footer[68..72]);
        var magic = System.Text.Encoding.ASCII.GetString(footer[72..80]);

        Assert.That(manifestOffset, Is.EqualTo(exeContent.Length));
        Assert.That(manifestLength, Is.EqualTo(System.Text.Encoding.UTF8.GetBytes(manifestContent).Length));
        Assert.That(configLength, Is.EqualTo(0));
        Assert.That(archiveOffset, Is.EqualTo(exeContent.Length + manifestLength));
        Assert.That(archiveLength, Is.EqualTo(archiveContent.Length));
        Assert.That(formatVersion, Is.EqualTo(3));
        Assert.That(flags, Is.EqualTo(1), "has archive, no config");
        Assert.That(magic, Is.EqualTo("INSTELLA"));
        Assert.That(allBytes.Length % 8, Is.Zero);

        // SHA256 hash should be non-zero (actually computed)
        Assert.That(payloadHash, Is.Not.All.EqualTo((byte)0));
    }

    private static int RoundUpTo8(int length) => (length + 7) / 8 * 8;
}
