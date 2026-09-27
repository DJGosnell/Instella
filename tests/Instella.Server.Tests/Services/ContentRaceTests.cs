using System.Security.Cryptography;
using System.Text;
using Instella.Server.Data;
using Instella.Server.Data.Entities;
using Instella.Server.Models;
using Instella.Server.Services;
using Instella.Server.Tests.Infrastructure;
using Instella.Server.Tests.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Instella.Server.Tests.Services;

/// <summary>
/// Deleting and uploading the same content at the same time never loses its blob.
/// Runs over a SQLite file, so the purge and the upload use separate connections, as in production.
/// </summary>
[TestFixture]
public sealed class ContentRaceTests
{
    private string _dbPath = null!;
    private DbContextOptions<AppDbContext> _options = null!;
    private TestStorageProvider _storage = null!;

    [SetUp]
    public void SetUp()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"instella-race-{Guid.NewGuid():N}.db");
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={_dbPath};Pooling=False").Options;
        using var db = new AppDbContext(_options);
        db.Database.Migrate();
        db.Packages.Add(new Package { PackageId = "com.test.race", DisplayName = "Race" });
        db.SaveChanges();
        _storage = new TestStorageProvider();
    }

    [TearDown]
    public void TearDown()
    {
        _storage.Clear();
        File.Delete(_dbPath);
    }

    [Test]
    public async Task AnUploadDuringAPurgeOfTheSameContent_KeepsTheBlob()
    {
        var content = Encoding.UTF8.GetBytes("shared content");
        var blob = BlobPath(content);
        var versionId = await PublishAsync("1.0.0", content);
        Task? racingUpload = null;
        _storage.BeforeDelete = async key =>
        {
            if (key != blob || racingUpload is not null) return;
            // The purge has committed and is about to delete the blob: upload the same content now.
            racingUpload = Task.Run(() => PublishAsync("1.1.0", content));
            await Task.WhenAny(racingUpload, Task.Delay(500));
        };

        await using (var db = new AppDbContext(_options))
            Assert.That(await Packages(db).DeleteVersionAsync(versionId), Is.True);
        await racingUpload!;

        Assert.That(_storage.Files.Keys, Does.Contain(blob), "the new build's content is still in storage");
        await using var check = new AppDbContext(_options);
        Assert.That((await check.StoredFiles.SingleAsync()).ReferenceCount, Is.EqualTo(1));
        Assert.That(ContentLocks.Shared.ActiveCount, Is.Zero, "every lock was released");
    }

    [Test]
    public async Task AReusedRowWithAMissingBlob_PutsTheBlobBack()
    {
        var content = Encoding.UTF8.GetBytes("lost content");
        await PublishAsync("1.0.0", content);
        _storage.Clear();   // the blob is gone; the row stays

        await PublishAsync("1.1.0", content);

        Assert.That(_storage.Files.Keys, Does.Contain(BlobPath(content)));
    }

    [Test]
    public async Task TheSweeper_KeepsContentASessionStartedToUseAfterItsQuery()
    {
        var content = Encoding.UTF8.GetBytes("pending content");
        var hash = Sha(content);
        await using (var db = new AppDbContext(_options))
        {
            db.StoredFiles.Add(new StoredFile
            {
                ContentHash = hash, Size = content.Length, StoragePath = BlobPath(content), ReferenceCount = 0,
                PendingSince = DateTime.UtcNow.AddDays(-2), FirstUploadedAt = DateTime.UtcNow.AddDays(-2),
            });
            await db.SaveChangesAsync();
        }
        await _storage.UploadAsync(BlobPath(content), new MemoryStream(content));

        await using (var db = new AppDbContext(_options))
        {
            var removed = await OrphanSweeper.SweepOnceAsync(db, _storage, DateTime.UtcNow, NullLogger.Instance, CancellationToken.None,
                afterCandidateQuery: async () =>
                {
                    // An upload session starts to reuse the content between the query and the delete.
                    await using var other = new AppDbContext(_options);
                    var upload = Uploads(other);
                    var session = await upload.StartSessionAsync("com.test.race", "2.0.0", "stable", TargetOS.Windows, Architecture.X64);
                    await upload.UploadFileAsync(session.Id, "app.exe", hash, new MemoryStream(content));
                });

            Assert.That(removed, Is.Zero);
        }
        Assert.That(_storage.Files.Keys, Does.Contain(BlobPath(content)));
        await using var check = new AppDbContext(_options);
        Assert.That(await check.StoredFiles.AnyAsync(f => f.ContentHash == hash), Is.True);
    }

    [Test]
    public async Task Locks_OnSeveralHashes_AreTakenInOrder_AndReleased()
    {
        var locks = new ContentLocks();
        var first = await locks.AcquireAsync(["b", "a"]);
        var second = locks.AcquireAsync(["a", "c"]);
        await Task.Delay(50);
        Assert.That(second.IsCompleted, Is.False, "'a' is held");

        await first.DisposeAsync();
        await (await second).DisposeAsync();

        Assert.That(locks.ActiveCount, Is.Zero);
    }

    private async Task<long> PublishAsync(string version, byte[] content)
    {
        await using var db = new AppDbContext(_options);
        var upload = Uploads(db);
        var session = await upload.StartSessionAsync("com.test.race", version, "stable", TargetOS.Windows, Architecture.X64);
        await upload.UploadFileAsync(session.Id, "app.exe", Sha(content), new MemoryStream(content));
        var result = await upload.CompleteSessionAsync(session.Id, null);
        return result.VersionId;
    }

    private UploadService Uploads(AppDbContext db) =>
        new(db, new ContentStorageService(db, _storage, NullLogger<ContentStorageService>.Instance), NullLogger<UploadService>.Instance);

    private PackageService Packages(AppDbContext db) =>
        new(db, new ContentStorageService(db, _storage, NullLogger<ContentStorageService>.Instance));

    private static string BlobPath(byte[] content)
    {
        var hash = Sha(content);
        return $"{hash[..2]}/{hash[2..4]}/{hash}";
    }

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
