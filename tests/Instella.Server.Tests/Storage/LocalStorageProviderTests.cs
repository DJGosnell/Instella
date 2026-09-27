using System.Security.Cryptography;
using Instella.Server.Storage;
using NUnit.Framework;

namespace Instella.Server.Tests.Storage;

[TestFixture]
public sealed class LocalStorageProviderTests
{
    private string _root = null!;

    [SetUp]
    public void SetUp() => _root = Directory.CreateTempSubdirectory("instella-local-storage-").FullName;

    [TearDown]
    public void TearDown() => Directory.Delete(_root, recursive: true);

    [Test]
    public async Task ConcurrentUploadsOfOneKey_AllSucceed_AndLeaveOneCompleteBlob()
    {
        // Content-addressed keys: parallel sessions uploading the same content write the same key.
        var storage = new LocalStorageProvider(_root);
        var content = RandomNumberGenerator.GetBytes(256 * 1024);

        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ =>
            Task.Run(() => storage.UploadAsync("ab/cd/abcd", new MemoryStream(content)))));

        Assert.That(results.Select(r => r.Error), Is.All.Null);
        Assert.That(File.ReadAllBytes(Path.Combine(_root, "ab", "cd", "abcd")), Is.EqualTo(content));
        Assert.That(Directory.EnumerateFiles(_root, "*.tmp", SearchOption.AllDirectories), Is.Empty, "no temp files are left");
    }

    [Test]
    public async Task Upload_ReplacesAnExistingBlob()
    {
        var storage = new LocalStorageProvider(_root);
        await storage.UploadAsync("k", new MemoryStream([1]));

        var result = await storage.UploadAsync("k", new MemoryStream([2, 3]));

        Assert.That(result.Success, Is.True);
        Assert.That(File.ReadAllBytes(Path.Combine(_root, "k")), Is.EqualTo(new byte[] { 2, 3 }));
    }
}
