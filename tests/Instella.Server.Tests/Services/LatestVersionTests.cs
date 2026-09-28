using System.Security.Cryptography;
using System.Text;
using Instella.Server.Data.Entities;
using Instella.Server.Models;
using Instella.Server.Services;
using Instella.Server.Tests.Integration;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace Instella.Server.Tests.Services;

/// <summary>
/// "Latest" is the highest version on a channel, not the last uploaded one; a pin
/// caps it; the patch source is the highest version below, whatever was uploaded last.
/// </summary>
[TestFixture]
public sealed class LatestVersionTests
{
    private ServerTestFixture _fixture = null!;
    private Package _package = null!;

    [SetUp]
    public async Task SetUp()
    {
        _fixture = new ServerTestFixture();
        _package = await _fixture.SeedPackageAsync("com.test.app", "Test App");
    }

    [TearDown]
    public void TearDown() => _fixture.Dispose();

    [Test]
    public async Task AHotfixUploadedAfterANewerVersion_IsNotLatest()
    {
        await Publish("1.3.0");
        await Publish("1.2.5");

        Assert.That(await Latest(), Is.EqualTo("1.3.0"));
    }

    [Test]
    public async Task ADraftPublishedLater_DoesNotBecomeLatest_UnlessItIsHigher()
    {
        await Publish("1.5.0");
        await Publish("1.4.0");
        await SetDraft("1.4.0", true);
        Assert.That(await Latest(), Is.EqualTo("1.5.0"), "a draft never counts");

        await SetDraft("1.4.0", false);
        Assert.That(await Latest(), Is.EqualTo("1.5.0"), "published last, but lower");
    }

    [Test]
    public async Task APin_CapsLatest()
    {
        await Publish("1.3.0");
        await Publish("1.4.0");
        await Publish("1.5.0");

        await _fixture.PackageService.SetChannelPinAsync(_package.Id, "stable", "1.4.0");

        Assert.That(await Latest(), Is.EqualTo("1.4.0"));
        var security = await _fixture.Db.SecurityEvents.SingleAsync();
        Assert.That(security.EventType, Is.EqualTo(SecurityEventType.ChannelPinChanged));
        Assert.That(security.Details, Does.Contain("1.4.0"));
    }

    [Test]
    public async Task APinWithoutABuildForAPlatform_FallsBackToTheHighestLowerVersionThatHasOne()
    {
        await Publish("1.3.0", os: TargetOS.Linux);
        await Publish("1.3.0");
        await Publish("1.4.0");                          // Windows only
        await Publish("1.5.0", os: TargetOS.Linux);
        await _fixture.PackageService.SetChannelPinAsync(_package.Id, "stable", "1.4.0");

        Assert.That(await Latest(TargetOS.Linux), Is.EqualTo("1.3.0"));
        Assert.That(await Latest(TargetOS.Windows), Is.EqualTo("1.4.0"));
    }

    [Test]
    public async Task ADeprecatedHighest_GivesTheNextOne()
    {
        await Publish("1.3.0");
        await Publish("1.4.0");
        await _fixture.Db.PackageVersions.Where(v => v.VersionString == "1.4.0")
            .ExecuteUpdateAsync(u => u.SetProperty(v => v.IsDeprecated, true));

        Assert.That(await Latest(), Is.EqualTo("1.3.0"));
    }

    [Test]
    public async Task Channels_AreIsolated()
    {
        await Publish("1.3.0");
        await Publish("2.0.0", channel: "beta");

        Assert.That(await Latest(), Is.EqualTo("1.3.0"));
        Assert.That(await Latest(channel: "beta"), Is.EqualTo("2.0.0"));
        Assert.That(await Latest(channel: "rc"), Is.Null, "a channel the package doesn't have");
    }

    [Test]
    public async Task DeletingThePinnedVersion_ClearsThePin()
    {
        await Publish("1.3.0");
        await Publish("1.4.0");
        await Publish("1.5.0");
        await _fixture.PackageService.SetChannelPinAsync(_package.Id, "stable", "1.4.0");
        var pinned = await _fixture.Db.PackageVersions.SingleAsync(v => v.VersionString == "1.4.0");

        await _fixture.PackageService.DeleteVersionAsync(pinned.Id);
        _fixture.Db.ChangeTracker.Clear();

        Assert.That(await _fixture.Db.PackageChannels.Select(c => c.PinnedVersionId).SingleAsync(), Is.Null);
        Assert.That(await Latest(), Is.EqualTo("1.5.0"));
    }

    [Test]
    public void APin_MustBeAVersionOnTheChannel()
    {
        Assert.ThrowsAsync<InvalidOperationException>(() => _fixture.PackageService.SetChannelPinAsync(_package.Id, "stable", "9.9.9"));
    }

    [Test]
    public async Task InstallerLatest_FollowsThePin()
    {
        await Publish("1.3.0", installer: true);
        await Publish("1.4.0", installer: true);
        await _fixture.PackageService.SetChannelPinAsync(_package.Id, "stable", "1.3.0");

        var installer = await _fixture.PackageService.GetInstallerAsync(
            "com.test.app", "latest", TargetOS.Windows, Architecture.X64, "online", "stable");

        Assert.That(installer!.Build.Version.VersionString, Is.EqualTo("1.3.0"));
    }

    [Test]
    public async Task AHotfix_PatchesFromItsOwnPredecessor_NotFromTheNewerLine()
    {
        await Publish("1.2.4", content: Content(1));
        await Publish("1.3.0", content: Content(3));
        var hotfix = await Publish("1.2.5", content: Content(2));

        var result = await _fixture.DiffService.GeneratePatchAsync(hotfix.Id);

        Assert.That(result.Outcome, Is.EqualTo(PatchOutcome.Created));
        var from = await _fixture.Db.VersionBuilds.Include(b => b.Version).SingleAsync(b => b.Id == result.Patch!.FromBuildId);
        Assert.That(from.Version.VersionString, Is.EqualTo("1.2.4"));
    }

    private async Task<string?> Latest(TargetOS os = TargetOS.Windows, string channel = "stable") =>
        (await _fixture.PackageService.GetLatestVersionAsync(_package.Id, channel, os, Architecture.X64))?.VersionString;

    private async Task<VersionBuild> Publish(string version, string channel = "stable", TargetOS os = TargetOS.Windows,
        bool installer = false, byte[]? content = null)
    {
        var upload = _fixture.UploadService;
        var session = await upload.StartSessionAsync("com.test.app", version, channel, os, Architecture.X64);
        var bytes = content ?? Encoding.UTF8.GetBytes($"app {version} {os}");
        await upload.UploadFileAsync(session.Id, "app.bin", Sha(bytes), new MemoryStream(bytes));
        if (installer)
        {
            var setup = Encoding.UTF8.GetBytes($"setup {version}");
            await upload.UploadInstallerAsync(session.Id, "online", $"Setup-{version}.exe", Sha(setup), new MemoryStream(setup));
        }
        await upload.CompleteSessionAsync(session.Id, null);
        _fixture.Db.ChangeTracker.Clear();
        return await _fixture.Db.VersionBuilds.SingleAsync(b => b.Version.VersionString == version && b.OS == os);
    }

    private Task SetDraft(string version, bool draft) =>
        _fixture.Db.VersionBuilds.Where(b => b.Version.VersionString == version)
            .ExecuteUpdateAsync(u => u.SetProperty(b => b.State, draft ? BuildState.Draft : BuildState.Published));

    /// <summary>64 KiB that differ in one place per <paramref name="variant"/>, so a patch is worth it.</summary>
    private static byte[] Content(int variant)
    {
        var bytes = new byte[64 * 1024];
        new Random(42).NextBytes(bytes);
        bytes[1000 * variant] ^= 0xFF;
        return bytes;
    }

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
