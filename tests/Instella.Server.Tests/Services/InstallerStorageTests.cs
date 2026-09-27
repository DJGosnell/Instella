using System.Security.Cryptography;
using System.Text;
using Instella.Core.Trust;
using Instella.Core.Wire;
using Instella.Server.Models;
using Instella.Server.Services;
using Instella.Server.Tests.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Instella.Server.Tests.Services;

/// <summary>
/// Installers are stored like build files (content-addressed, one reference each) and are
/// accepted with a signed release only when the release lists exactly the uploaded ones.
/// </summary>
[TestFixture]
public sealed class InstallerStorageTests
{
    private ServerTestFixture _fixture = null!;
    private ECDsa _key = null!;

    [SetUp]
    public async Task SetUp()
    {
        _fixture = new ServerTestFixture();
        await _fixture.SeedPackageAsync();
        _key = ReleaseKeys.Generate();
    }

    [TearDown]
    public void TearDown()
    {
        _key.Dispose();
        _fixture.Dispose();
    }

    [Test]
    public async Task SignedRelease_ThatDoesNotListAnUploadedInstaller_IsRejected()
    {
        var session = await StartWithApp();
        await UploadInstaller(session, InstallerKinds.Online, "Setup.exe", "installer");

        var ex = Assert.ThrowsAsync<InvalidOperationException>(() =>
            _fixture.UploadService.CompleteSessionAsync(session, null, Sign(installers: null)));
        Assert.That(ex!.Message, Does.Contain("installer list does not match"));
        Assert.That(await _fixture.Db.VersionBuilds.CountAsync(), Is.Zero);
    }

    [Test]
    public async Task SignedRelease_ListingAnInstallerThatWasNotUploaded_IsRejected()
    {
        var session = await StartWithApp();

        var ex = Assert.ThrowsAsync<InvalidOperationException>(() => _fixture.UploadService.CompleteSessionAsync(session, null,
            Sign([new ReleaseInstaller(InstallerKinds.Offline, "Setup.exe", 9, Sha("installer"))])));
        Assert.That(ex!.Message, Does.Contain("installer list does not match"));
    }

    [Test]
    public async Task SignedRelease_WithMatchingInstallers_CreatesInstallerRowsAndCountsThem()
    {
        var session = await StartWithApp();
        await UploadInstaller(session, InstallerKinds.Online, "Setup.exe", "installer");

        await _fixture.UploadService.CompleteSessionAsync(session, null,
            Sign([new ReleaseInstaller(InstallerKinds.Online, "Setup.exe", 9, Sha("installer"))]));

        var row = await _fixture.Db.BuildInstallers.SingleAsync();
        Assert.That((row.Kind, row.FileName, row.ContentHash), Is.EqualTo((InstallerKinds.Online, "Setup.exe", Sha("installer"))));
        Assert.That((await _fixture.Db.StoredFiles.SingleAsync(f => f.ContentHash == Sha("installer"))).ReferenceCount, Is.EqualTo(1));
    }

    [Test]
    public async Task DeleteVersion_RemovesInstallerRowsAndTheirBlobs()
    {
        var session = await StartWithApp();
        await UploadInstaller(session, InstallerKinds.Offline, "Setup.exe", "offline installer");
        var result = await _fixture.UploadService.CompleteSessionAsync(session, null);
        _fixture.Db.ChangeTracker.Clear();

        Assert.That(await _fixture.PackageService.DeleteVersionAsync(result.VersionId), Is.True);

        Assert.That(await _fixture.Db.BuildInstallers.CountAsync(), Is.Zero);
        Assert.That(await _fixture.Db.StoredFiles.CountAsync(), Is.Zero, "no content is left referenced");
        Assert.That(_fixture.Storage.Files, Is.Empty);
    }

    [Test]
    public async Task InstallerOfAnOpenSession_IsNotSweptAsAnOrphan()
    {
        var session = await StartWithApp();
        await UploadInstaller(session, InstallerKinds.Online, "Setup.exe", "pending installer");
        // Pending for longer than the grace period, but the session that uploaded it is still open.
        await _fixture.Db.StoredFiles.Where(f => f.ContentHash == Sha("pending installer"))
            .ExecuteUpdateAsync(u => u.SetProperty(f => f.PendingSince, DateTime.UtcNow - 2 * OrphanSweeper.Grace));

        await OrphanSweeper.SweepOnceAsync(_fixture.Db, _fixture.Storage, DateTime.UtcNow, NullLogger.Instance, CancellationToken.None);

        Assert.That(await _fixture.Db.StoredFiles.AnyAsync(f => f.ContentHash == Sha("pending installer")), Is.True);
    }

    [TestCase("installer", "Setup.exe", "Unknown installer kind")]
    [TestCase(InstallerKinds.Online, "../Setup.exe", "Invalid installer file name")]
    [TestCase(InstallerKinds.Online, ".hidden.exe", "Invalid installer file name")]
    public async Task BadKindOrFileName_IsRefused(string kind, string fileName, string message)
    {
        var session = await StartWithApp();
        var ex = Assert.ThrowsAsync<InvalidOperationException>(() => UploadInstaller(session, kind, fileName, "x"));
        Assert.That(ex!.Message, Does.Contain(message));
    }

    private async Task<Guid> StartWithApp()
    {
        var session = await _fixture.UploadService.StartSessionAsync("com.test.app", "1.0.0", "stable", TargetOS.Windows, Architecture.X64);
        await _fixture.UploadService.UploadFileAsync(session.Id, "app.exe", Sha("app"), new MemoryStream(Encoding.UTF8.GetBytes("app")));
        return session.Id;
    }

    private Task UploadInstaller(Guid session, string kind, string fileName, string content) =>
        _fixture.UploadService.UploadInstallerAsync(session, kind, fileName, Sha(content), new MemoryStream(Encoding.UTF8.GetBytes(content)));

    private SignedRelease Sign(IReadOnlyList<ReleaseInstaller>? installers) => ReleaseSigner.Sign(new ReleaseManifest
    {
        FormatVersion = ReleaseManifest.CurrentFormatVersion,
        AppId = "com.test.app",
        Version = new Version(1, 0, 0),
        Os = "windows",
        Arch = "x64",
        Channel = "stable",
        CreatedAt = DateTimeOffset.UtcNow,
        Files = [new ReleaseFile("app.exe", 3, Sha("app"))],
        Installers = installers,
    }, _key);

    private static string Sha(string content) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
}
