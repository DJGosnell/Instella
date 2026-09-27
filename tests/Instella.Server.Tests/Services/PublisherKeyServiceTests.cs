using Instella.Core.Trust;
using Instella.Server.Services;
using Instella.Server.Tests.Infrastructure;
using NUnit.Framework;

namespace Instella.Server.Tests.Services;

[TestFixture]
public class PublisherKeyServiceTests
{
    private DatabaseFixture _dbFixture = null!;
    private TestStorageProvider _storage = null!;
    private PackageService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _dbFixture = new DatabaseFixture();
        _storage = new TestStorageProvider();
        var content = new ContentStorageService(_dbFixture.Context, _storage, new TestLogger<ContentStorageService>());
        _service = new PackageService(_dbFixture.Context, content);
    }

    [TearDown]
    public void TearDown()
    {
        _dbFixture.Dispose();
        _storage.Clear();
    }

    [Test]
    public async Task AddPublisherKey_StoresCanonicalKeyAndId()
    {
        var package = await _dbFixture.SeedPackageAsync();
        using var ecdsa = ReleaseKeys.Generate();
        var expected = ReleaseKeys.PublicKeyOf(ecdsa);

        var added = await _service.AddPublisherKeyAsync(package.Id, "  " + expected.PublicKey + "\n", " CI key ");

        Assert.That(added.KeyId, Is.EqualTo(expected.KeyId));
        Assert.That(added.PublicKey, Is.EqualTo(expected.PublicKey));
        Assert.That(added.Label, Is.EqualTo("CI key"));
        Assert.That(await _service.GetPublisherKeysAsync(package.Id), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task AddPublisherKey_RejectsGarbageAndDuplicates()
    {
        var package = await _dbFixture.SeedPackageAsync();
        using var ecdsa = ReleaseKeys.Generate();
        var key = ReleaseKeys.PublicKeyOf(ecdsa).PublicKey;

        Assert.ThrowsAsync<ArgumentException>(() => _service.AddPublisherKeyAsync(package.Id, "not-a-key", null));
        await _service.AddPublisherKeyAsync(package.Id, key, null);
        Assert.ThrowsAsync<InvalidOperationException>(() => _service.AddPublisherKeyAsync(package.Id, key, null));
    }

    [Test]
    public async Task RemovePublisherKey_DeletesOnlyThatKey()
    {
        var package = await _dbFixture.SeedPackageAsync();
        using var a = ReleaseKeys.Generate();
        using var b = ReleaseKeys.Generate();
        var keyA = await _service.AddPublisherKeyAsync(package.Id, ReleaseKeys.PublicKeyOf(a).PublicKey, "a");
        await _service.AddPublisherKeyAsync(package.Id, ReleaseKeys.PublicKeyOf(b).PublicKey, "b");

        Assert.That(await _service.RemovePublisherKeyAsync(keyA.Id), Is.True);
        var remaining = await _service.GetPublisherKeysAsync(package.Id);
        Assert.That(remaining.Select(k => k.Label), Is.EqualTo(new[] { "b" }));
        Assert.That(await _service.RemovePublisherKeyAsync(keyA.Id), Is.False);
    }
}
