using System.Security.Cryptography;
using System.Text;
using Instella.Server.Services;
using Instella.Server.Tests.Infrastructure;
using NUnit.Framework;

namespace Instella.Server.Tests.Services;

[TestFixture]
public class ContentStorageServiceTests
{
    private DatabaseFixture _dbFixture = null!;
    private TestStorageProvider _storage = null!;
    private TestLogger<ContentStorageService> _logger = null!;
    private ContentStorageService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _dbFixture = new DatabaseFixture();
        _storage = new TestStorageProvider();
        _logger = new TestLogger<ContentStorageService>();
        _service = new ContentStorageService(_dbFixture.Context, _storage, _logger);
    }

    [TearDown]
    public void TearDown()
    {
        _dbFixture.Dispose();
        _storage.Clear();
    }

    [Test]
    public async Task GetByHashAsync_ReturnsNull_WhenContentDoesNotExist()
    {
        // Arrange
        var hash = ComputeHash("nonexistent content");

        // Act
        var result = await _service.GetByHashAsync(hash);

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetByHashAsync_ReturnsStoredFile_WhenContentExists()
    {
        // Arrange
        var hash = ComputeHash("test content");
        await _dbFixture.SeedStoredFileAsync(hash, 1000);

        // Act
        var result = await _service.GetByHashAsync(hash);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.ContentHash, Is.EqualTo(hash));
    }

    [Test]
    public async Task StorePendingAsync_NewContent_IsUploadedWithZeroReferencesAndPending()
    {
        var content = Encoding.UTF8.GetBytes("new content");
        var hash = ComputeHashFromBytes(content);
        using var stream = new MemoryStream(content);

        var (result, deduplicated) = await _service.StorePendingAsync(hash, stream, content.Length);

        Assert.Multiple(() =>
        {
            Assert.That(deduplicated, Is.False);
            Assert.That(result.ContentHash, Is.EqualTo(hash));
            Assert.That(result.Size, Is.EqualTo(content.Length));
            Assert.That(result.ReferenceCount, Is.EqualTo(0), "references are counted when a session completes");
            Assert.That(result.PendingSince, Is.Not.Null);
            Assert.That(result.StoragePath, Is.EqualTo($"{hash[..2]}/{hash[2..4]}/{hash}"));
        });
        Assert.That(_storage.Files, Contains.Key(result.StoragePath));
    }

    [Test]
    public async Task StorePendingAsync_ExistingContent_IsReusedWithoutUploadOrCountChange()
    {
        var content = Encoding.UTF8.GetBytes("existing content");
        var hash = ComputeHashFromBytes(content);
        var seeded = await _dbFixture.SeedStoredFileAsync(hash, content.Length, referenceCount: 1);
        await _storage.UploadAsync(seeded.StoragePath, new MemoryStream(content));   // row and blob both present
        var uploadsBefore = _storage.Operations.Count(o => o.Method == "UploadAsync");
        using var stream = new MemoryStream(content);

        var (result, deduplicated) = await _service.StorePendingAsync(hash, stream, content.Length);

        Assert.That(deduplicated, Is.True);
        Assert.That(result.ReferenceCount, Is.EqualTo(1));
        Assert.That(_storage.Operations.Count(o => o.Method == "UploadAsync"), Is.EqualTo(uploadsBefore));
    }

    [Test]
    public void StorePendingAsync_Throws_WhenUploadFails()
    {
        var content = Encoding.UTF8.GetBytes("failing content");
        var hash = ComputeHashFromBytes(content);
        using var stream = new MemoryStream(content);
        _storage.ShouldFail = true;
        _storage.FailureMessage = "Storage full";

        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await _service.StorePendingAsync(hash, stream, content.Length));
        Assert.That(ex!.Message, Does.Contain("Storage full"));
    }

    [Test]
    public async Task StorePendingAsync_StorageThatCannotAnswer_FailsTheUpload_InsteadOfReuploading()
    {
        // "Cannot tell" is not "missing"; the error surfaces instead of being hidden.
        var content = Encoding.UTF8.GetBytes("existing content");
        var hash = ComputeHashFromBytes(content);
        await _dbFixture.SeedStoredFileAsync(hash, content.Length, referenceCount: 1);
        _storage.ExistsFailure = new HttpRequestException("503 Service Unavailable");

        Assert.ThrowsAsync<HttpRequestException>(() => _service.StorePendingAsync(hash, new MemoryStream(content), content.Length));
        Assert.That(_storage.Operations.Count(o => o.Method == "UploadAsync"), Is.Zero);
    }

    [Test]
    public async Task StorePendingAsync_ConcurrentInsertOfTheSameContent_ReusesTheWinner()
    {
        // Another request inserted the row after our existence check (the dedup race):
        // a second context over the same database plays that request.
        var content = Encoding.UTF8.GetBytes("raced content");
        var hash = ComputeHashFromBytes(content);
        using (var other = _dbFixture.CreateNewContext())
        {
            other.StoredFiles.Add(new Instella.Server.Data.Entities.StoredFile
            {
                ContentHash = hash, Size = content.Length, StoragePath = $"{hash[..2]}/{hash[2..4]}/{hash}", ReferenceCount = 1,
            });
            await other.SaveChangesAsync();
        }
        var racing = new RacingContentStorage(_dbFixture.Context, _storage, hash);

        var (result, deduplicated) = await racing.StorePendingAsync(hash, new MemoryStream(content), content.Length);

        Assert.That(deduplicated, Is.True);
        Assert.That(result.ReferenceCount, Is.EqualTo(1), "the winner's row is used as is");
    }

    /// <summary>Hides existing rows from the first lookup, so the insert collides.</summary>
    private sealed class RacingContentStorage(Instella.Server.Data.AppDbContext db, TestStorageProvider storage, string hash)
        : ContentStorageService(db, storage, new TestLogger<ContentStorageService>())
    {
        private bool _hidden;

        public override async Task<Instella.Server.Data.Entities.StoredFile?> GetByHashAsync(string contentHash, CancellationToken ct = default)
        {
            if (!_hidden && contentHash == hash)
            {
                _hidden = true;
                return null;
            }
            return await base.GetByHashAsync(contentHash, ct);
        }
    }

    [Test]
    public async Task RetrieveAsync_ReturnsContent_WhenExists()
    {
        // Arrange
        var content = Encoding.UTF8.GetBytes("retrievable content");
        var hash = ComputeHashFromBytes(content);
        var storedFile = await _dbFixture.SeedStoredFileAsync(hash, content.Length);

        // Seed the content in storage
        using var uploadStream = new MemoryStream(content);
        await _storage.UploadAsync(storedFile.StoragePath, uploadStream);

        // Act
        var result = await _service.RetrieveAsync(hash);

        // Assert
        Assert.That(result, Is.Not.Null);

        using var ms = new MemoryStream();
        await result!.CopyToAsync(ms);
        Assert.That(ms.ToArray(), Is.EqualTo(content));
    }

    [Test]
    public async Task RetrieveAsync_ReturnsNull_WhenContentDoesNotExist()
    {
        // Arrange
        var hash = ComputeHash("nonexistent");

        // Act
        var result = await _service.RetrieveAsync(hash);

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetPresignedUrlAsync_ReturnsUrl_WhenContentExists()
    {
        // Arrange
        var hash = ComputeHash("presigned content");
        var storedFile = await _dbFixture.SeedStoredFileAsync(hash, 1000);

        // Seed the content in storage
        using var stream = new MemoryStream([1, 2, 3]);
        await _storage.UploadAsync(storedFile.StoragePath, stream);

        // Act
        var result = await _service.GetPresignedUrlAsync(hash, TimeSpan.FromMinutes(5), "download.txt");

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result, Does.Contain("presigned"));
        Assert.That(result, Does.Contain("fileName=download.txt"));
    }

    [Test]
    public async Task GetPresignedUrlAsync_ReturnsNull_WhenContentDoesNotExist()
    {
        // Arrange
        var hash = ComputeHash("nonexistent for presigned");

        // Act
        var result = await _service.GetPresignedUrlAsync(hash, TimeSpan.FromMinutes(5));

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetStatsAsync_ReturnsCorrectStatistics()
    {
        // Arrange
        await _dbFixture.SeedStoredFileAsync(ComputeHash("file1"), 1000, referenceCount: 2);
        await _dbFixture.SeedStoredFileAsync(ComputeHash("file2"), 500, referenceCount: 1);
        await _dbFixture.SeedStoredFileAsync(ComputeHash("file3"), 2000, referenceCount: 3);

        // Act
        var stats = await _service.GetStatsAsync();

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(stats.UniqueFiles, Is.EqualTo(3));
            Assert.That(stats.TotalSize, Is.EqualTo(3500)); // 1000 + 500 + 2000
            Assert.That(stats.TotalReferences, Is.EqualTo(6)); // 2 + 1 + 3
            Assert.That(stats.DeduplicationSavings, Is.EqualTo(5000)); // 1000*1 + 500*0 + 2000*2
        });
    }

    private static string ComputeHash(string content)
    {
        return ComputeHashFromBytes(Encoding.UTF8.GetBytes(content));
    }

    private static string ComputeHashFromBytes(byte[] content)
    {
        var hashBytes = SHA256.HashData(content);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
