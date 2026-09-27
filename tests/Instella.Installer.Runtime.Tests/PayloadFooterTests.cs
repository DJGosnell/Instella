using Instella.Installer.Build.Tasks;
using Instella.Core.Internal;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests;

[TestFixture]
public sealed class PayloadFooterTests
{
    private string _tempDir = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"instella-footer-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Test]
    public void ReadFooter_RoundTrip_OfflinePayload()
    {
        var (exePath, manifestContent) = CreateOfflinePayload();

        using var stream = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var footer = PayloadFooterReader.ReadFooter(stream, stream.Length);

        Assert.That(footer.IsValid, Is.True);
        Assert.That(footer.ManifestLength, Is.EqualTo(System.Text.Encoding.UTF8.GetBytes(manifestContent).Length));
        Assert.That(footer.ConfigLength, Is.EqualTo(0));
        Assert.That(footer.ArchiveLength, Is.GreaterThan(0));
    }

    [Test]
    public void ReadFooter_RoundTrip_LitePayload()
    {
        var exePath = Path.Combine(_tempDir, "lite.exe");
        var manifestPath = Path.Combine(_tempDir, "instella.json");
        var configPath = Path.Combine(_tempDir, "instella.config");

        File.WriteAllBytes(exePath, new byte[64]);
        File.WriteAllText(manifestPath, """{"appName":"Test","appId":"com.test"}""");
        File.WriteAllText(configPath, """{"serverUrl":"https://example.com"}""");

        PayloadAppender.AppendLitePayload(exePath, manifestPath, configPath);

        using var stream = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var footer = PayloadFooterReader.ReadFooter(stream, stream.Length);

        Assert.That(footer.IsValid, Is.True);
        Assert.That(footer.ConfigLength, Is.GreaterThan(0));
        Assert.That(footer.ArchiveLength, Is.EqualTo(0));
        Assert.That(footer.ArchiveOffset, Is.EqualTo(0));
    }

    [Test]
    public void ReadFooter_RejectsFileSmallerThanFooter()
    {
        using var stream = new MemoryStream(new byte[50]); // less than 72 bytes
        var footer = PayloadFooterReader.ReadFooter(stream, stream.Length);
        Assert.That(footer.IsValid, Is.False);
    }

    [Test]
    public void ReadFooter_RejectsMangledMagic()
    {
        var (exePath, _) = CreateOfflinePayload();
        var bytes = File.ReadAllBytes(exePath);

        // Corrupt the magic bytes at offset -8 from end
        bytes[^3] = 0xFF;
        File.WriteAllBytes(exePath, bytes);

        using var stream = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var footer = PayloadFooterReader.ReadFooter(stream, stream.Length);
        Assert.That(footer.IsValid, Is.False);
    }

    [Test]
    public void ReadFooter_RejectsManifestOffsetPastEof()
    {
        var (exePath, _) = CreateOfflinePayload();
        var bytes = File.ReadAllBytes(exePath);

        // Overwrite manifest_offset with a value past EOF
        var hugeOffset = BitConverter.GetBytes((long)(bytes.Length * 2));
        hugeOffset.CopyTo(bytes, bytes.Length - PayloadFooterReader.FooterSize);
        File.WriteAllBytes(exePath, bytes);

        using var stream = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var footer = PayloadFooterReader.ReadFooter(stream, stream.Length);
        Assert.That(footer.IsValid, Is.False);
    }

    [Test]
    public void ReadFooter_RejectsArchiveLengthExceedingStreamLength()
    {
        var (exePath, _) = CreateOfflinePayload();
        var bytes = File.ReadAllBytes(exePath);

        // Overwrite archive_length with a huge value
        var hugeLength = BitConverter.GetBytes((long)(bytes.Length * 10));
        hugeLength.CopyTo(bytes, bytes.Length - PayloadFooterReader.FooterSize + 24);
        File.WriteAllBytes(exePath, bytes);

        using var stream = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var footer = PayloadFooterReader.ReadFooter(stream, stream.Length);
        Assert.That(footer.IsValid, Is.False);
    }

    [TestCase(0, 100, 0, 0L, 0L, true, TestName = "lite: manifest only")]
    [TestCase(0, 100, 20, 120L, 50L, true, TestName = "offline: manifest, config, archive in order")]
    [TestCase(-1, 100, 0, 0L, 0L, false, TestName = "negative manifest offset")]
    [TestCase(0, 0, 0, 0L, 0L, false, TestName = "empty manifest")]
    [TestCase(0, 100, -1, 0L, 0L, false, TestName = "negative config length")]
    [TestCase(0, 100, 0, 50L, 10L, false, TestName = "archive overlaps manifest")]
    [TestCase(0, 100, 0, 150L, 60L, false, TestName = "archive runs into footer")]
    [TestCase(0, 100, 0, 150L, -1L, false, TestName = "negative archive length")]
    [TestCase(150, 100, 0, 0L, 0L, false, TestName = "manifest runs into footer")]
    [TestCase(0, 100, 0, long.MaxValue, 10L, false, TestName = "archive end overflows")]
    [TestCase(long.MaxValue - 10, 100, 0, 0L, 0L, false, TestName = "manifest end overflows")]
    public void IsLayoutValid_Table(long manifestOffset, int manifestLength, int configLength,
        long archiveOffset, long archiveLength, bool expected)
    {
        Assert.That(PayloadFooterReader.IsLayoutValid(200, manifestOffset, manifestLength, configLength, archiveOffset, archiveLength),
            Is.EqualTo(expected));
    }

    [Test]
    public void IsLayoutValid_RejectsManifestOverSanityCap()
    {
        Assert.That(PayloadFooterReader.IsLayoutValid(long.MaxValue / 2, 0, PayloadFooterReader.MaxManifestLength + 1, 0, 0, 0),
            Is.False);
    }

    private (string exePath, string manifestContent) CreateOfflinePayload()
    {
        var exePath = Path.Combine(_tempDir, $"test-{Guid.NewGuid()}.exe");
        var manifestPath = Path.Combine(_tempDir, $"manifest-{Guid.NewGuid()}.json");
        var archivePath = Path.Combine(_tempDir, $"archive-{Guid.NewGuid()}.zip");

        var manifestContent = """{"appName":"Test","appId":"com.test"}""";
        File.WriteAllBytes(exePath, new byte[64]);
        File.WriteAllText(manifestPath, manifestContent);
        File.WriteAllBytes(archivePath, new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x01, 0x02 });

        PayloadAppender.AppendOfflinePayload(exePath, manifestPath, archivePath);
        return (exePath, manifestContent);
    }
}
