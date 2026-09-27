using System.Security.Cryptography;
using System.Text;
using Instella.Server.Models;
using Instella.Server.Data.Entities;
using Instella.Server.Services;
using Instella.Server.Tests.Infrastructure;
using NUnit.Framework;

namespace Instella.Server.Tests.Services;

[TestFixture]
public class UploadServiceTests
{
    private DatabaseFixture _dbFixture = null!;
    private TestStorageProvider _storage = null!;
    private TestLogger<UploadService> _uploadLogger = null!;
    private TestLogger<ContentStorageService> _storageLogger = null!;
    private ContentStorageService _contentStorage = null!;
    private UploadService _service = null!;
    private Package _testPackage = null!;

    [SetUp]
    public async Task SetUp()
    {
        _dbFixture = new DatabaseFixture();
        _storage = new TestStorageProvider();
        _uploadLogger = new TestLogger<UploadService>();
        _storageLogger = new TestLogger<ContentStorageService>();

        _contentStorage = new ContentStorageService(_dbFixture.Context, _storage, _storageLogger);
        _service = new UploadService(_dbFixture.Context, _contentStorage, _uploadLogger);

        _testPackage = await _dbFixture.SeedPackageAsync("com.test.app", "Test App");
    }

    [TearDown]
    public void TearDown()
    {
        _dbFixture.Dispose();
        _storage.Clear();
    }

    [Test]
    public async Task StartSessionAsync_CreatesValidSession()
    {
        // Act
        var session = await _service.StartSessionAsync(
            "com.test.app", "1.0.0", "stable", TargetOS.Windows, Architecture.X64);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(session.Id, Is.Not.EqualTo(Guid.Empty));
            Assert.That(session.PackageId, Is.EqualTo("com.test.app"));
            Assert.That(session.Version, Is.EqualTo("1.0.0"));
            Assert.That(session.Channel, Is.EqualTo("stable"));
            Assert.That(session.OS, Is.EqualTo(TargetOS.Windows));
            Assert.That(session.Architecture, Is.EqualTo(Architecture.X64));
            Assert.That(session.Files, Is.Empty);
        });
    }

    [Test]
    public async Task StartSessionAsync_ThrowsException_WhenPackageNotFound()
    {
        // Act & Assert
        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await _service.StartSessionAsync(
                "nonexistent.package", "1.0.0", "stable", TargetOS.Windows, Architecture.X64));

        Assert.That(ex!.Message, Does.Contain("not found"));
    }

    [Test]
    public async Task StartSessionAsync_ThrowsException_WhenVersionAlreadyExists()
    {
        // Arrange
        var version = await _dbFixture.SeedVersionAsync(_testPackage, "1.0.0");
        await _dbFixture.SeedBuildAsync(version, TargetOS.Windows, Architecture.X64);

        // Act & Assert
        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await _service.StartSessionAsync(
                "com.test.app", "1.0.0", "stable", TargetOS.Windows, Architecture.X64));

        Assert.That(ex!.Message, Does.Contain("already exists"));
    }

    [Test]
    public async Task StartSessionAsync_AllowsSameVersionDifferentPlatform()
    {
        // Arrange
        var version = await _dbFixture.SeedVersionAsync(_testPackage, "1.0.0");
        await _dbFixture.SeedBuildAsync(version, TargetOS.Windows, Architecture.X64);

        // Act - should succeed for different OS
        var session = await _service.StartSessionAsync(
            "com.test.app", "1.0.0", "stable", TargetOS.Linux, Architecture.X64);

        // Assert
        Assert.That(session, Is.Not.Null);
    }

    [Test]
    public async Task GetSession_ReturnsSession_WhenExists()
    {
        // Arrange
        var session = await _service.StartSessionAsync(
            "com.test.app", "1.0.0", "stable", TargetOS.Windows, Architecture.X64);

        // Act
        var retrieved = _service.GetSession(session.Id);

        // Assert
        Assert.That(retrieved, Is.Not.Null);
        Assert.That(retrieved!.Id, Is.EqualTo(session.Id));
    }

    [Test]
    public void GetSession_ReturnsNull_WhenNotExists()
    {
        // Act
        var result = _service.GetSession(Guid.NewGuid());

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task UploadFileAsync_StoresContent()
    {
        // Arrange
        var session = await _service.StartSessionAsync(
            "com.test.app", "1.0.0", "stable", TargetOS.Windows, Architecture.X64);

        var content = Encoding.UTF8.GetBytes("test file content");
        var hash = ComputeHashFromBytes(content);
        using var stream = new MemoryStream(content);

        // Act
        var result = await _service.UploadFileAsync(session.Id, "lib/test.dll", hash, stream);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(result.RelativePath, Is.EqualTo("lib/test.dll"));
            Assert.That(result.ContentHash, Is.EqualTo(hash));
            Assert.That(result.Size, Is.EqualTo(content.Length));
            Assert.That(result.Deduplicated, Is.False);
        });

        // Verify file is in session
        var updatedSession = _service.GetSession(session.Id);
        Assert.That(updatedSession!.Files, Contains.Key("lib/test.dll"));
    }

    [Test]
    public async Task UploadFileAsync_LeadingSeparator_IsRejectedAsAbsolute()
    {
        // Arrange
        var session = await _service.StartSessionAsync(
            "com.test.app", "1.0.0", "stable", TargetOS.Windows, Architecture.X64);

        var content = Encoding.UTF8.GetBytes("test");
        var hash = ComputeHashFromBytes(content);
        using var stream = new MemoryStream(content);

        // Act / Assert - a rooted path is not silently rewritten into a relative one;
        // SafePath rejects it (UploadFile_BackslashPath_IsStoredCanonically covers '\').
        Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.UploadFileAsync(session.Id, @"\lib\test.dll", hash, stream));
    }

    [Test]
    public async Task UploadFileAsync_ThrowsException_WhenHashMismatch()
    {
        // Arrange
        var session = await _service.StartSessionAsync(
            "com.test.app", "1.0.0", "stable", TargetOS.Windows, Architecture.X64);

        var content = Encoding.UTF8.GetBytes("test file content");
        var wrongHash = ComputeHashFromBytes(Encoding.UTF8.GetBytes("different content"));
        using var stream = new MemoryStream(content);

        // Act & Assert
        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await _service.UploadFileAsync(session.Id, "test.dll", wrongHash, stream));

        Assert.That(ex!.Message, Does.Contain("Hash mismatch"));
    }

    [Test]
    public async Task UploadFileAsync_ThrowsException_WhenSessionNotFound()
    {
        // Arrange
        var content = Encoding.UTF8.GetBytes("test");
        var hash = ComputeHashFromBytes(content);
        using var stream = new MemoryStream(content);

        // Act & Assert
        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await _service.UploadFileAsync(Guid.NewGuid(), "test.dll", hash, stream));

        Assert.That(ex!.Message, Does.Contain("not found"));
    }

    [Test]
    public async Task UploadFileAsync_DeduplicatesExistingContent()
    {
        // Arrange
        var session = await _service.StartSessionAsync(
            "com.test.app", "1.0.0", "stable", TargetOS.Windows, Architecture.X64);

        var content = Encoding.UTF8.GetBytes("shared content");
        var hash = ComputeHashFromBytes(content);

        // Pre-seed the content
        await _dbFixture.SeedStoredFileAsync(hash, content.Length, referenceCount: 1);

        using var stream = new MemoryStream(content);

        // Act
        var result = await _service.UploadFileAsync(session.Id, "test.dll", hash, stream);

        // Assert
        Assert.That(result.Deduplicated, Is.True);
    }

    [Test]
    public async Task CompleteSessionAsync_CreatesVersionAndBuild()
    {
        // Arrange
        var session = await _service.StartSessionAsync(
            "com.test.app", "1.0.0", "stable", TargetOS.Windows, Architecture.X64);

        var content = Encoding.UTF8.GetBytes("test content");
        var hash = ComputeHashFromBytes(content);
        using var stream = new MemoryStream(content);
        await _service.UploadFileAsync(session.Id, "test.dll", hash, stream);

        // Act
        var result = await _service.CompleteSessionAsync(session.Id, "Initial release");

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True);
            Assert.That(result.FileCount, Is.EqualTo(1));
            Assert.That(result.TotalSize, Is.EqualTo(content.Length));
        });

        // Verify entities created
        using var db = _dbFixture.CreateNewContext();
        var version = db.PackageVersions.FirstOrDefault(v => v.VersionString == "1.0.0");
        Assert.That(version, Is.Not.Null);
        Assert.That(version!.Changelog, Is.EqualTo("Initial release"));

        var build = db.VersionBuilds.FirstOrDefault(b => b.VersionId == version.Id);
        Assert.That(build, Is.Not.Null);
        Assert.That(build!.TotalSize, Is.EqualTo(content.Length));

        var files = db.BuildFiles.Where(f => f.BuildId == build.Id).ToList();
        Assert.That(files, Has.Count.EqualTo(1));
        Assert.That(files[0].RelativePath, Is.EqualTo("test.dll"));
    }

    [Test]
    public async Task CompleteSessionAsync_ThrowsException_WhenNoFiles()
    {
        // Arrange
        var session = await _service.StartSessionAsync(
            "com.test.app", "1.0.0", "stable", TargetOS.Windows, Architecture.X64);

        // Act & Assert - no files uploaded
        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await _service.CompleteSessionAsync(session.Id, null));

        Assert.That(ex!.Message, Does.Contain("no files"));
    }

    [Test]
    public async Task CompleteSessionAsync_ThrowsException_WhenSessionNotFound()
    {
        // Act & Assert
        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await _service.CompleteSessionAsync(Guid.NewGuid(), null));

        Assert.That(ex!.Message, Does.Contain("not found"));
    }

    [Test]
    public async Task CompleteSessionAsync_ReusesExistingVersion()
    {
        // Arrange - pre-create version for a different platform
        var existingVersion = await _dbFixture.SeedVersionAsync(_testPackage, "1.0.0");

        var session = await _service.StartSessionAsync(
            "com.test.app", "1.0.0", "stable", TargetOS.Linux, Architecture.X64);

        var content = Encoding.UTF8.GetBytes("test");
        var hash = ComputeHashFromBytes(content);
        using var stream = new MemoryStream(content);
        await _service.UploadFileAsync(session.Id, "test.dll", hash, stream);

        // Act
        var result = await _service.CompleteSessionAsync(session.Id, "Linux version");

        // Assert - should use existing version ID
        Assert.That(result.VersionId, Is.EqualTo(existingVersion.Id));
    }

    [Test]
    public async Task CompleteSessionAsync_ReportsDeduplicatedCount()
    {
        // Arrange
        var session = await _service.StartSessionAsync(
            "com.test.app", "1.0.0", "stable", TargetOS.Windows, Architecture.X64);

        // Upload two files, one of which is deduplicated
        var content1 = Encoding.UTF8.GetBytes("unique content");
        var hash1 = ComputeHashFromBytes(content1);

        var content2 = Encoding.UTF8.GetBytes("shared content");
        var hash2 = ComputeHashFromBytes(content2);
        await _dbFixture.SeedStoredFileAsync(hash2, content2.Length);

        using (var stream1 = new MemoryStream(content1))
            await _service.UploadFileAsync(session.Id, "unique.dll", hash1, stream1);

        using (var stream2 = new MemoryStream(content2))
            await _service.UploadFileAsync(session.Id, "shared.dll", hash2, stream2);

        // Act
        var result = await _service.CompleteSessionAsync(session.Id, null);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(result.FileCount, Is.EqualTo(2));
            Assert.That(result.DeduplicatedCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task CancelSessionAsync_RemovesSession()
    {
        // Arrange
        var session = await _service.StartSessionAsync(
            "com.test.app", "1.0.0", "stable", TargetOS.Windows, Architecture.X64);

        // Act
        await _service.CancelSessionAsync(session.Id);

        // Assert
        var retrieved = _service.GetSession(session.Id);
        Assert.That(retrieved, Is.Null);
    }

    [Test]
    public async Task CancelSessionAsync_LeavesUncountedContentForTheSweeper()
    {
        // Arrange
        var session = await _service.StartSessionAsync(
            "com.test.app", "1.0.0", "stable", TargetOS.Windows, Architecture.X64);

        var content = Encoding.UTF8.GetBytes("test content for cancel");
        var hash = ComputeHashFromBytes(content);
        using var stream = new MemoryStream(content);
        await _service.UploadFileAsync(session.Id, "test.dll", hash, stream);

        // Content uploaded into a session is counted only when the session completes.
        var storedBefore = await _contentStorage.GetByHashAsync(hash);
        Assert.That(storedBefore!.ReferenceCount, Is.Zero);
        Assert.That(storedBefore.PendingSince, Is.Not.Null);

        // Act
        await _service.CancelSessionAsync(session.Id);

        // Assert - the session is gone; the pending content waits for the orphan sweeper
        Assert.That(_service.GetSession(session.Id), Is.Null);
        Assert.That(await _contentStorage.GetByHashAsync(hash), Is.Not.Null);
    }

    [Test]
    public async Task CancelSessionAsync_DoesNotThrow_WhenSessionNotFound()
    {
        // Act & Assert - should not throw
        await _service.CancelSessionAsync(Guid.NewGuid());
    }

    [Test]
    public async Task MultipleFilesUpload_ComputesCorrectTotalSize()
    {
        // Arrange
        var session = await _service.StartSessionAsync(
            "com.test.app", "1.0.0", "stable", TargetOS.Windows, Architecture.X64);

        var files = new[]
        {
            ("file1.dll", Encoding.UTF8.GetBytes("content one")),
            ("file2.dll", Encoding.UTF8.GetBytes("content two longer")),
            ("file3.exe", Encoding.UTF8.GetBytes("exe content"))
        };

        foreach (var (path, content) in files)
        {
            var hash = ComputeHashFromBytes(content);
            using var stream = new MemoryStream(content);
            await _service.UploadFileAsync(session.Id, path, hash, stream);
        }

        // Act
        var result = await _service.CompleteSessionAsync(session.Id, null);

        // Assert
        var expectedSize = files.Sum(f => f.Item2.Length);
        Assert.That(result.TotalSize, Is.EqualTo(expectedSize));
        Assert.That(result.FileCount, Is.EqualTo(3));
    }

    [Test]
    public async Task UploadFileAsync_OverwritesSamePathInSession()
    {
        // Arrange
        var session = await _service.StartSessionAsync(
            "com.test.app", "1.0.0", "stable", TargetOS.Windows, Architecture.X64);

        var content1 = Encoding.UTF8.GetBytes("first version");
        var hash1 = ComputeHashFromBytes(content1);
        using var stream1 = new MemoryStream(content1);
        await _service.UploadFileAsync(session.Id, "test.dll", hash1, stream1);

        var content2 = Encoding.UTF8.GetBytes("second version updated");
        var hash2 = ComputeHashFromBytes(content2);
        using var stream2 = new MemoryStream(content2);

        // Act
        await _service.UploadFileAsync(session.Id, "test.dll", hash2, stream2);

        // Assert
        var updatedSession = _service.GetSession(session.Id);
        Assert.That(updatedSession!.Files["test.dll"].ContentHash, Is.EqualTo(hash2));
        Assert.That(updatedSession.Files.Count, Is.EqualTo(1));
    }

    // ---- channels are free names ---------------------------------------------

    [Test]
    public async Task Channel_IsNormalised_AndCreatedByTheFirstUpload()
    {
        var session = await _service.StartSessionAsync("com.test.app", "1.0.0", "Beta", TargetOS.Windows, Architecture.X64);
        await UploadOneAsync(session.Id);

        await _service.CompleteSessionAsync(session.Id, null);

        Assert.That(session.Channel, Is.EqualTo("beta"));
        using var db = _dbFixture.CreateNewContext();
        var version = db.PackageVersions.Single(v => v.VersionString == "1.0.0");
        Assert.That(version.Channel, Is.EqualTo("beta"));
        Assert.That(version.VersionKey, Is.EqualTo("0000000001.0000000000.0000000000.0000000000"));
        Assert.That(db.PackageChannels.Select(c => c.Name), Is.EqualTo(new[] { "beta" }));
    }

    [Test]
    public async Task ACustomChannel_RoundTrips()
    {
        var session = await _service.StartSessionAsync("com.test.app", "2.0.0", "rc", TargetOS.Windows, Architecture.X64);
        await UploadOneAsync(session.Id);
        await _service.CompleteSessionAsync(session.Id, null);

        using var db = _dbFixture.CreateNewContext();
        Assert.That(db.PackageVersions.Single().Channel, Is.EqualTo("rc"));
    }

    [TestCase("beta_2")]
    [TestCase("-beta")]
    [TestCase("")]
    [TestCase("abcdefghijabcdefghijabcdefghijabc")]   // 33 characters
    public void InvalidChannel_IsRefused(string channel)
    {
        var ex = Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.StartSessionAsync("com.test.app", "1.0.0", channel, TargetOS.Windows, Architecture.X64));

        Assert.That(ex!.Message, Does.Contain(Instella.Core.Wire.ChannelNames.Rule));
    }

    // ---- upload validation ----------------------------------------------------

    [Test]
    public async Task Version_IsStoredCanonically()
    {
        var session = await _service.StartSessionAsync("com.test.app", "1.2", "stable", TargetOS.Windows, Architecture.X64);
        await UploadOneAsync(session.Id);
        await _service.CompleteSessionAsync(session.Id, null);

        Assert.That(session.Version, Is.EqualTo("1.2.0"));
        using var db = _dbFixture.CreateNewContext();
        Assert.That(db.PackageVersions.Single().VersionString, Is.EqualTo("1.2.0"));
    }

    [TestCase("latest")]
    [TestCase("1.2-beta")]
    [TestCase("v1.2.3")]
    [TestCase("")]
    public void InvalidVersion_IsRefused(string version)
    {
        var ex = Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.StartSessionAsync("com.test.app", version, "stable", TargetOS.Windows, Architecture.X64));

        Assert.That(ex!.Message, Does.StartWith($"invalid version '{version}'"));
    }

    [Test]
    public async Task AVersionOnAnotherChannel_IsAConflict_AtStart()
    {
        var first = await _service.StartSessionAsync("com.test.app", "1.3.0", "beta", TargetOS.Windows, Architecture.X64);
        await UploadOneAsync(first.Id);
        await _service.CompleteSessionAsync(first.Id, null);

        var ex = Assert.ThrowsAsync<UploadConflictException>(() =>
            _service.StartSessionAsync("com.test.app", "1.3", "stable", TargetOS.Linux, Architecture.X64));

        Assert.That(ex!.Message, Is.EqualTo("version 1.3.0 is on channel 'beta'; a version belongs to one channel"));
    }

    [Test]
    public async Task AVersionCreatedOnAnotherChannelMeanwhile_IsAConflict_AtCompletion()
    {
        var session = await _service.StartSessionAsync("com.test.app", "1.3.0", "stable", TargetOS.Windows, Architecture.X64);
        await UploadOneAsync(session.Id);
        // A concurrent session published 1.3.0 on beta between this one's start and completion.
        _dbFixture.Context.PackageVersions.Add(new PackageVersion { PackageId = _testPackage.Id, VersionString = "1.3.0", Channel = "beta" });
        await _dbFixture.Context.SaveChangesAsync();

        Assert.ThrowsAsync<UploadConflictException>(() => _service.CompleteSessionAsync(session.Id, null));
        using var db = _dbFixture.CreateNewContext();
        Assert.That(db.VersionBuilds.Count(), Is.Zero, "nothing was stored");
    }

    private async Task UploadOneAsync(Guid sessionId)
    {
        var content = Encoding.UTF8.GetBytes("app " + sessionId);
        using var stream = new MemoryStream(content);
        await _service.UploadFileAsync(sessionId, "app.exe", ComputeHashFromBytes(content), stream);
    }

    private static string ComputeHashFromBytes(byte[] content)
    {
        var hashBytes = SHA256.HashData(content);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    [TestCase("../escape.dll")]
    [TestCase("a/../../escape.dll")]
    [TestCase(@"C:\escape.dll")]
    [TestCase(@"\\?\C:\escape.dll")]
    [TestCase("CON")]
    [TestCase("a:b")]
    public async Task UploadFile_UnsafePath_IsRejectedBeforeStorage(string path)
    {
        var session = await _service.StartSessionAsync("com.test.app", "1.0.0", "stable", TargetOS.Windows, Architecture.X64);
        var content = Encoding.UTF8.GetBytes("payload");
        var hash = Convert.ToHexStringLower(SHA256.HashData(content));

        var ex = Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.UploadFileAsync(session.Id, path, hash, new MemoryStream(content)));
        Assert.That(ex!.Message, Does.Contain("Invalid file path"));
    }

    [Test]
    public async Task UploadFile_BackslashPath_IsStoredCanonically()
    {
        var session = await _service.StartSessionAsync("com.test.app", "1.0.0", "stable", TargetOS.Windows, Architecture.X64);
        var content = Encoding.UTF8.GetBytes("payload");
        var hash = Convert.ToHexStringLower(SHA256.HashData(content));

        var result = await _service.UploadFileAsync(session.Id, @"lib\a.dll", hash, new MemoryStream(content));
        Assert.That(result.RelativePath, Is.EqualTo("lib/a.dll"));
    }
}
