using Instella.Server.Services;
using Instella.Server.Tests.Integration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Instella.Server.Tests.Services;

/// <summary>The sweeper deletes only Instella's own objects; the local folder is Instella's alone.</summary>
[TestFixture]
public sealed class StorageSafetyTests
{
    private ServerTestFixture _fixture = null!;

    [SetUp]
    public void SetUp() => _fixture = new ServerTestFixture();

    [TearDown]
    public void TearDown() => _fixture.Dispose();

    [Test]
    public async Task TheSweeper_LeavesForeignFiles_AndDeletesOldOrphansOfItsOwn()
    {
        var old = DateTime.UtcNow.AddDays(-3);
        var orphan = "ab/cd/" + new string('e', 64);
        var orphanTemp = "patches/com.x/1.0.0-to-1.1.0/windows-x64/patch.zip." + new string('0', 32) + ".tmp";
        _fixture.Storage.Seed("notes.txt", [1], old);
        _fixture.Storage.Seed("keys/key-1.xml", [1], old);
        _fixture.Storage.Seed("AB/CD/" + new string('E', 64), [1], old);   // not the layout: uppercase
        _fixture.Storage.Seed(orphan, [1], old);
        _fixture.Storage.Seed(orphanTemp, [1], old);

        var removed = await OrphanSweeper.SweepOnceAsync(_fixture.Db, _fixture.Storage, DateTime.UtcNow, NullLogger.Instance, CancellationToken.None);

        Assert.That(removed, Is.EqualTo(2));
        Assert.That(_fixture.Storage.Files.Keys, Is.EquivalentTo(new[] { "notes.txt", "keys/key-1.xml", "AB/CD/" + new string('E', 64) }));
    }

    [TestCase("ab/cd/0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", true)]
    [TestCase("patches/com.app/1.0.0-to-1.1.0/windows-x64/patch.zip", true)]
    [TestCase("ab/cd/0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef.0123456789abcdef0123456789abcdef.tmp", true)]
    [TestCase("__instella_connection_test__/0123", false)]
    [TestCase("backup.tar", false)]
    [TestCase("patches/readme.txt", false)]
    public void InstellasLayout(string key, bool ours) => Assert.That(OrphanSweeper.IsInstellaKey(key), Is.EqualTo(ours));

    [Test]
    public void TheLocalFolder_MustBeAbsolute_NotARoot_AndNotHoldTheConfig()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        var config = Path.Combine(Path.GetTempPath(), "instella-config");

        Assert.That(StorageSettingsService.ValidateLocalBasePath("packages", config), Does.Contain("absolute"));
        Assert.That(StorageSettingsService.ValidateLocalBasePath(root, config), Does.Contain("root"));
        Assert.That(StorageSettingsService.ValidateLocalBasePath(config, config), Does.Contain("config"));
        Assert.That(StorageSettingsService.ValidateLocalBasePath(Path.GetTempPath(), config), Does.Contain("config"), "contains it");
        Assert.That(StorageSettingsService.ValidateLocalBasePath(Path.Combine(Path.GetTempPath(), "instella-packages"), config), Is.Null);
    }
}
