using System.Security.Cryptography;
using System.Text;
using Instella.Server.Models;
using Instella.Server.Services;
using Instella.Server.Tests.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Instella.Server.Tests.Services;

/// <summary>
/// Version deletion on real SQLite (the in-memory provider does not enforce the Restrict foreign
/// keys between builds, files and content): deleting a version with files works, reference counts
/// follow, and blobs go after the commit.
/// </summary>
[TestFixture]
public sealed class VersionDeletionTests
{
    private ServerTestFixture _fixture = null!;

    [SetUp]
    public void SetUp() => _fixture = new ServerTestFixture();

    [TearDown]
    public void TearDown() => _fixture.Dispose();

    [Test]
    public async Task DeleteVersion_WithFiles_RemovesRowsAndBlobs()
    {
        await _fixture.SeedPackageAsync();
        var versionId = await UploadAsync("1.0.0", ("app.exe", "app"), ("lib.dll", "lib"));

        Assert.That(await _fixture.PackageService.DeleteVersionAsync(versionId), Is.True);

        Assert.That(await _fixture.Db.PackageVersions.CountAsync(), Is.Zero);
        Assert.That(await _fixture.Db.BuildFiles.CountAsync(), Is.Zero);
        Assert.That(await _fixture.Db.StoredFiles.CountAsync(), Is.Zero, "content no build references is removed");
        Assert.That(_fixture.Storage.Files, Is.Empty, "and so are its blobs, after the commit");
    }

    [Test]
    public async Task DeleteVersion_KeepsContentAnotherVersionStillUses()
    {
        await _fixture.SeedPackageAsync();
        var v1 = await UploadAsync("1.0.0", ("app.exe", "v1"), ("shared.dll", "shared"));
        await UploadAsync("1.1.0", ("app.exe", "v2"), ("shared.dll", "shared"));

        await _fixture.PackageService.DeleteVersionAsync(v1);

        var shared = await _fixture.Db.StoredFiles.SingleAsync(f => f.ContentHash == Sha("shared"));
        Assert.That(shared.ReferenceCount, Is.EqualTo(1));
        Assert.That(await _fixture.Db.StoredFiles.AnyAsync(f => f.ContentHash == Sha("v1")), Is.False);
        Assert.That(_fixture.Storage.Files.Keys, Does.Contain(shared.StoragePath));
    }

    [Test]
    public async Task DeleteVersion_WhenBlobDeleteFails_CommitsAndLeavesAnOrphanTheSweeperRemoves()
    {
        await _fixture.SeedPackageAsync();
        var versionId = await UploadAsync("1.0.0", ("app.exe", "app"));
        _fixture.Storage.ShouldFail = true;

        Assert.That(await _fixture.PackageService.DeleteVersionAsync(versionId), Is.True);
        Assert.That(await _fixture.Db.StoredFiles.CountAsync(), Is.Zero, "the database is consistent even though storage failed");
        Assert.That(_fixture.Storage.Files, Is.Not.Empty, "the blob is orphaned");

        _fixture.Storage.ShouldFail = false;
        var removed = await OrphanSweeper.SweepOnceAsync(_fixture.Db, _fixture.Storage,
            DateTime.UtcNow + OrphanSweeper.Grace + TimeSpan.FromMinutes(1), NullLogger.Instance, CancellationToken.None);
        Assert.That(removed, Is.EqualTo(1));
        Assert.That(_fixture.Storage.Files, Is.Empty);
    }

    [Test]
    public async Task CompletedUpload_CountsEachFileOnce_EvenWhenAPathIsReuploaded()
    {
        await _fixture.SeedPackageAsync();
        var session = await _fixture.UploadService.StartSessionAsync("com.test.app", "1.0.0", "stable", TargetOS.Windows, Architecture.X64);
        await Upload(session.Id, "app.exe", "first");
        await Upload(session.Id, "app.exe", "second");   // replaces the first
        await Upload(session.Id, "copy.exe", "second");  // same content, second path

        await _fixture.UploadService.CompleteSessionAsync(session.Id, null);

        Assert.That((await _fixture.Db.StoredFiles.SingleAsync(f => f.ContentHash == Sha("second"))).ReferenceCount, Is.EqualTo(2));
        var first = await _fixture.Db.StoredFiles.SingleAsync(f => f.ContentHash == Sha("first"));
        Assert.That(first.ReferenceCount, Is.Zero, "the replaced upload is never counted");
        Assert.That(first.PendingSince, Is.Not.Null, "and stays pending until the sweeper removes it");
    }

    private async Task<long> UploadAsync(string version, params (string Path, string Content)[] files)
    {
        var session = await _fixture.UploadService.StartSessionAsync("com.test.app", version, "stable", TargetOS.Windows, Architecture.X64);
        foreach (var (path, content) in files)
            await Upload(session.Id, path, content);
        var result = await _fixture.UploadService.CompleteSessionAsync(session.Id, null);
        _fixture.Db.ChangeTracker.Clear();
        return result.VersionId;
    }

    private Task Upload(Guid session, string path, string content) =>
        _fixture.UploadService.UploadFileAsync(session, path, Sha(content), new MemoryStream(Encoding.UTF8.GetBytes(content)));

    private static string Sha(string content) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
}
