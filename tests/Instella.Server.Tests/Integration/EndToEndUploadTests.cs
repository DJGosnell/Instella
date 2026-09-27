using System.Security.Cryptography;
using System.Text;
using Instella.Server.Models;
using Instella.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace Instella.Server.Tests.Integration;

[TestFixture]
public class EndToEndUploadTests
{
    private ServerTestFixture _fixture = null!;

    [SetUp]
    public async Task SetUp()
    {
        _fixture = new ServerTestFixture();
        await _fixture.SeedPackageAsync("com.test.app", "Test App");
    }

    [TearDown]
    public void TearDown()
    {
        _fixture.Dispose();
    }

    [Test]
    public async Task StartSession_ChannelsAreFreeNames_ButFollowTheRule()
    {
        // Any name that follows ChannelNames is a channel ("canary", even "1"); others are refused.
        foreach (var channel in new[] { "", "beta_2", "-rc", "nightly-" })
            Assert.ThrowsAsync<InvalidOperationException>(() => _fixture.UploadService.StartSessionAsync(
                "com.test.app", "1.0.0", channel, TargetOS.Windows, Architecture.X64), channel);
        var session = await _fixture.UploadService.StartSessionAsync(
            "com.test.app", "1.0.0", "canary", TargetOS.Windows, Architecture.X64);
        Assert.That(session.Channel, Is.EqualTo("canary"));
    }

    [Test]
    public async Task StartSession_StoresTheCanonicalChannelName()
    {
        var session = await _fixture.UploadService.StartSessionAsync(
            "com.test.app", "7.0.0", "Beta", TargetOS.Windows, Architecture.X64);
        Assert.That(session.Channel, Is.EqualTo("beta"));
    }

    [Test]
    public async Task FullUploadWorkflow_CreatesVersionBuildAndFiles()
    {
        // Arrange - files to upload
        var files = new Dictionary<string, byte[]>
        {
            ["MyApp.exe"] = Encoding.UTF8.GetBytes("exe content here"),
            ["MyApp.dll"] = Encoding.UTF8.GetBytes("dll content here"),
            ["lib/dependency.dll"] = Encoding.UTF8.GetBytes("dependency content")
        };

        // Act - Start session
        var session = await _fixture.UploadService.StartSessionAsync(
            "com.test.app", "1.0.0", "stable", TargetOS.Windows, Architecture.X64);

        // Upload each file
        foreach (var (path, content) in files)
        {
            var hash = ComputeHash(content);
            using var stream = new MemoryStream(content);
            await _fixture.UploadService.UploadFileAsync(session.Id, path, hash, stream);
        }

        // Complete session
        var result = await _fixture.UploadService.CompleteSessionAsync(session.Id, "Initial release");

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True);
            Assert.That(result.FileCount, Is.EqualTo(3));
            Assert.That(result.TotalSize, Is.EqualTo(files.Values.Sum(f => f.Length)));
        });

        // Verify database state
        var version = await _fixture.Db.PackageVersions
            .Include(v => v.Builds)
            .ThenInclude(b => b.Files)
            .FirstOrDefaultAsync(v => v.VersionString == "1.0.0");

        Assert.That(version, Is.Not.Null);
        Assert.That(version!.Changelog, Is.EqualTo("Initial release"));
        Assert.That(version.Builds, Has.Count.EqualTo(1));

        var build = version.Builds.First();
        Assert.That(build.Files, Has.Count.EqualTo(3));

        // Verify storage has files
        foreach (var (path, content) in files)
        {
            var hash = ComputeHash(content);
            var storedFile = await _fixture.Db.StoredFiles.FindAsync(hash);
            Assert.That(storedFile, Is.Not.Null);
            Assert.That(_fixture.Storage.Files.ContainsKey(storedFile!.StoragePath), Is.True);
        }
    }

    [Test]
    public async Task UploadWithDeduplication_SharesContent()
    {
        // Arrange - upload v1.0.0 first
        var sharedContent = Encoding.UTF8.GetBytes("shared library content");
        var sharedHash = ComputeHash(sharedContent);

        var v1Files = new Dictionary<string, byte[]>
        {
            ["MyApp.exe"] = Encoding.UTF8.GetBytes("exe v1"),
            ["shared.dll"] = sharedContent
        };

        var session1 = await _fixture.UploadService.StartSessionAsync(
            "com.test.app", "1.0.0", "stable", TargetOS.Windows, Architecture.X64);

        foreach (var (path, content) in v1Files)
        {
            using var stream = new MemoryStream(content);
            await _fixture.UploadService.UploadFileAsync(session1.Id, path, ComputeHash(content), stream);
        }

        await _fixture.UploadService.CompleteSessionAsync(session1.Id, null);

        // Act - upload v2.0.0 with same shared.dll
        var v2Files = new Dictionary<string, byte[]>
        {
            ["MyApp.exe"] = Encoding.UTF8.GetBytes("exe v2 updated"),
            ["shared.dll"] = sharedContent // Same content
        };

        var session2 = await _fixture.UploadService.StartSessionAsync(
            "com.test.app", "2.0.0", "stable", TargetOS.Windows, Architecture.X64);

        var deduplicatedCount = 0;
        foreach (var (path, content) in v2Files)
        {
            using var stream = new MemoryStream(content);
            var result = await _fixture.UploadService.UploadFileAsync(
                session2.Id, path, ComputeHash(content), stream);
            if (result.Deduplicated)
                deduplicatedCount++;
        }

        var completeResult = await _fixture.UploadService.CompleteSessionAsync(session2.Id, null);

        // Assert
        Assert.That(deduplicatedCount, Is.EqualTo(1)); // shared.dll was deduplicated
        Assert.That(completeResult.DeduplicatedCount, Is.EqualTo(1));

        // Verify shared file has reference count of 2
        var storedFile = await _fixture.Db.StoredFiles.FindAsync(sharedHash);
        Assert.That(storedFile!.ReferenceCount, Is.EqualTo(2));

        // Verify storage only has one copy
        var storageFileCount = _fixture.Storage.Files.Count(f =>
            f.Value.Data.SequenceEqual(sharedContent));
        Assert.That(storageFileCount, Is.EqualTo(1));
    }

    [Test]
    public async Task UploadMultiplePlatforms_CreatesSeparateBuilds()
    {
        // Arrange
        var files = new Dictionary<string, byte[]>
        {
            ["app"] = Encoding.UTF8.GetBytes("app content")
        };

        // Act - Upload for Windows
        var winSession = await _fixture.UploadService.StartSessionAsync(
            "com.test.app", "1.0.0", "stable", TargetOS.Windows, Architecture.X64);

        foreach (var (path, content) in files)
        {
            using var stream = new MemoryStream(content);
            await _fixture.UploadService.UploadFileAsync(winSession.Id, path, ComputeHash(content), stream);
        }

        await _fixture.UploadService.CompleteSessionAsync(winSession.Id, "Multi-platform release");

        // Upload for Linux
        var linuxSession = await _fixture.UploadService.StartSessionAsync(
            "com.test.app", "1.0.0", "stable", TargetOS.Linux, Architecture.X64);

        foreach (var (path, content) in files)
        {
            using var stream = new MemoryStream(content);
            await _fixture.UploadService.UploadFileAsync(linuxSession.Id, path, ComputeHash(content), stream);
        }

        await _fixture.UploadService.CompleteSessionAsync(linuxSession.Id, null);

        // Assert
        var version = await _fixture.Db.PackageVersions
            .Include(v => v.Builds)
            .FirstOrDefaultAsync(v => v.VersionString == "1.0.0");

        Assert.That(version, Is.Not.Null);
        Assert.That(version!.Builds, Has.Count.EqualTo(2));
        Assert.That(version.Builds.Any(b => b.OS == TargetOS.Windows), Is.True);
        Assert.That(version.Builds.Any(b => b.OS == TargetOS.Linux), Is.True);
    }

    [Test]
    public async Task CancelledSession_CleansUpFiles()
    {
        // Arrange
        var content = Encoding.UTF8.GetBytes("content to be cancelled");
        var hash = ComputeHash(content);

        var session = await _fixture.UploadService.StartSessionAsync(
            "com.test.app", "1.0.0", "stable", TargetOS.Windows, Architecture.X64);

        using var stream = new MemoryStream(content);
        await _fixture.UploadService.UploadFileAsync(session.Id, "test.dll", hash, stream);

        // Verify file was stored
        var storedBefore = await _fixture.Db.StoredFiles.FindAsync(hash);
        Assert.That(storedBefore, Is.Not.Null);

        // Act
        await _fixture.UploadService.CancelSessionAsync(session.Id);

        // Assert - the content was never counted; the sweeper removes it after the grace period
        _fixture.Db.ChangeTracker.Clear();
        var storedAfter = await _fixture.Db.StoredFiles.FindAsync(hash);
        Assert.That(storedAfter!.ReferenceCount, Is.Zero);
        await Instella.Server.Services.OrphanSweeper.SweepOnceAsync(_fixture.Db, _fixture.Storage,
            DateTime.UtcNow + Instella.Server.Services.OrphanSweeper.Grace + TimeSpan.FromMinutes(1),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, CancellationToken.None);
        Assert.That(await _fixture.Db.StoredFiles.AnyAsync(f => f.ContentHash == hash), Is.False);
        Assert.That(_fixture.Storage.Files, Is.Empty);

        // Verify no version/build created
        var version = await _fixture.Db.PackageVersions.FirstOrDefaultAsync(v => v.VersionString == "1.0.0");
        Assert.That(version, Is.Null);
    }

    [Test]
    public async Task UploadWithHashMismatch_FailsGracefully()
    {
        // Arrange
        var session = await _fixture.UploadService.StartSessionAsync(
            "com.test.app", "1.0.0", "stable", TargetOS.Windows, Architecture.X64);

        var content = Encoding.UTF8.GetBytes("actual content");
        var wrongHash = ComputeHash(Encoding.UTF8.GetBytes("different content"));

        // Act & Assert
        using var stream = new MemoryStream(content);
        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await _fixture.UploadService.UploadFileAsync(session.Id, "test.dll", wrongHash, stream));

        Assert.That(ex!.Message, Does.Contain("Hash mismatch"));

        // Session should still be active
        var sessionStillActive = _fixture.UploadService.GetSession(session.Id);
        Assert.That(sessionStillActive, Is.Not.Null);
    }

    [Test]
    public async Task LargeUpload_HandlesMultipleFiles()
    {
        // Arrange - simulate larger upload with many files
        var files = new Dictionary<string, byte[]>();
        for (int i = 0; i < 50; i++)
        {
            files[$"lib/file{i:D3}.dll"] = Encoding.UTF8.GetBytes($"content of file {i}");
        }

        var session = await _fixture.UploadService.StartSessionAsync(
            "com.test.app", "1.0.0", "stable", TargetOS.Windows, Architecture.X64);

        // Act
        foreach (var (path, content) in files)
        {
            using var stream = new MemoryStream(content);
            await _fixture.UploadService.UploadFileAsync(session.Id, path, ComputeHash(content), stream);
        }

        var result = await _fixture.UploadService.CompleteSessionAsync(session.Id, "Large release");

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True);
            Assert.That(result.FileCount, Is.EqualTo(50));
        });

        var build = await _fixture.Db.VersionBuilds
            .Include(b => b.Files)
            .FirstOrDefaultAsync(b => b.Version.VersionString == "1.0.0");

        Assert.That(build!.Files, Has.Count.EqualTo(50));
    }

    private static string ComputeHash(byte[] content)
    {
        var hashBytes = SHA256.HashData(content);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
