using Instella.Core.Update;
using Instella.Sdk;
using NUnit.Framework;

namespace Instella.Sdk.Tests;

/// <summary>
/// Tests for the Instella SDK public API.
/// </summary>
[TestFixture]
public class SdkTests
{
    #region InstellaInfo Tests

    [Test]
    public void InstellaInfo_CanBeCreated()
    {
        var info = new InstellaInfo
        {
            AppName = "TestApp",
            AppId = "com.test.app",
            Version = new Version(1, 0, 0),
            InstallRoot = "/path/to/app",
            ServerUrl = "https://example.com",
            Channel = "stable"
        };

        Assert.Multiple(() =>
        {
            Assert.That(info.AppName, Is.EqualTo("TestApp"));
            Assert.That(info.AppId, Is.EqualTo("com.test.app"));
            Assert.That(info.Version, Is.EqualTo(new Version(1, 0, 0)));
            Assert.That(info.InstallRoot, Is.EqualTo("/path/to/app"));
            Assert.That(info.ServerUrl, Is.EqualTo("https://example.com"));
            Assert.That(info.Channel, Is.EqualTo("stable"));
        });
    }

    [Test]
    public void InstellaInfo_ChannelDefaultsToStable()
    {
        var info = new InstellaInfo
        {
            AppName = "TestApp",
            AppId = "com.test.app",
            Version = new Version(1, 0, 0),
            InstallRoot = "/path/to/app",
            ServerUrl = "https://example.com"
        };

        Assert.That(info.Channel, Is.EqualTo("stable"));
    }

    #endregion

    #region UpdateOptions Tests

    [Test]
    public void UpdateOptions_HasCorrectDefaults()
    {
        var options = new UpdateOptions();

        Assert.Multiple(() =>
        {
            Assert.That(options.AllowForceClose, Is.True);
            Assert.That(options.GracefulCloseTimeout, Is.EqualTo(TimeSpan.FromSeconds(30)));
            Assert.That(options.RestartAfterUpdate, Is.True);
            Assert.That(options.Silent, Is.False);
            Assert.That(options.PreferPatch, Is.True);
            Assert.That(options.AdditionalArgs, Is.Null);
        });
    }

    [Test]
    public void UpdateOptions_CanBeCustomized()
    {
        var options = new UpdateOptions
        {
            AllowForceClose = false,
            GracefulCloseTimeout = TimeSpan.FromMinutes(2),
            RestartAfterUpdate = false,
            Silent = true,
            PreferPatch = false,
            AdditionalArgs = ["--updated", "--silent"]
        };

        Assert.Multiple(() =>
        {
            Assert.That(options.AllowForceClose, Is.False);
            Assert.That(options.GracefulCloseTimeout, Is.EqualTo(TimeSpan.FromMinutes(2)));
            Assert.That(options.RestartAfterUpdate, Is.False);
            Assert.That(options.Silent, Is.True);
            Assert.That(options.PreferPatch, Is.False);
            Assert.That(options.AdditionalArgs, Is.EquivalentTo(new[] { "--updated", "--silent" }));
        });
    }

    #endregion

    #region UpdateInfo Tests

    [Test]
    public void UpdateInfo_CanBeCreated()
    {
        var info = new UpdateInfo
        {
            Version = new Version(2, 0, 0),
            Changelog = "New features!",
            FullSize = 50_000_000,
            Channel = "stable"
        };

        Assert.Multiple(() =>
        {
            Assert.That(info.Version, Is.EqualTo(new Version(2, 0, 0)));
            Assert.That(info.Changelog, Is.EqualTo("New features!"));
            Assert.That(info.FullSize, Is.EqualTo(50_000_000));
            Assert.That(info.Channel, Is.EqualTo("stable"));
        });
    }

    [Test]
    public void UpdateInfo_PatchFieldsOptional()
    {
        var info = new UpdateInfo
        {
            Version = new Version(2, 0, 0),
            Changelog = "",
            FullSize = 50_000_000,
        };

        Assert.Multiple(() =>
        {
            Assert.That(info.PatchAvailable, Is.False);
            Assert.That(info.PatchSize, Is.Null);
            Assert.That(info.PatchSha256, Is.Null);
            Assert.That(info.Mandatory, Is.False);
        });
    }

    [Test]
    public void UpdateInfo_PatchAvailableWhenSizeProvided()
    {
        var info = new UpdateInfo
        {
            Version = new Version(2, 0, 0),
            Changelog = "",
            FullSize = 50_000_000,
            PatchAvailable = true,
            PatchSize = 1_000_000,
            PatchSha256 = "def456"
        };

        Assert.Multiple(() =>
        {
            Assert.That(info.PatchAvailable, Is.True);
            Assert.That(info.PatchSize, Is.EqualTo(1_000_000));
            Assert.That(info.PatchSha256, Is.EqualTo("def456"));
        });
    }

    [Test]
    public void UpdateInfo_ChannelDefaultsToStable()
    {
        var info = new UpdateInfo
        {
            Version = new Version(1, 0, 0),
            Changelog = "",
            FullSize = 1000,
        };

        Assert.That(info.Channel, Is.EqualTo("stable"));
    }

    #endregion

    #region UpdateCheckResult Tests

    [Test]
    public void UpdateCheckResult_NoUpdateAvailable()
    {
        var result = new UpdateCheckResult(UpdateCheckStatus.UpToDate, null, null);

        Assert.Multiple(() =>
        {
            Assert.That(result.UpdateAvailable, Is.False);
            Assert.That(result.Update, Is.Null);
            Assert.That(result.Error, Is.Null);
        });
    }

    [Test]
    public void UpdateCheckResult_UpdateAvailable()
    {
        var updateInfo = new UpdateInfo
        {
            Version = new Version(2, 0, 0),
            Changelog = "Changelog",
            FullSize = 1000,
        };

        var result = new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, updateInfo, null);

        Assert.Multiple(() =>
        {
            Assert.That(result.UpdateAvailable, Is.True);
            Assert.That(result.Update, Is.Not.Null);
            Assert.That(result.Update!.Version, Is.EqualTo(new Version(2, 0, 0)));
            Assert.That(result.Error, Is.Null);
        });
    }

    [Test]
    public void UpdateCheckResult_ErrorState()
    {
        var result = new UpdateCheckResult(UpdateCheckStatus.Failed, null, "Network error");

        Assert.Multiple(() =>
        {
            Assert.That(result.UpdateAvailable, Is.False);
            Assert.That(result.Update, Is.Null);
            Assert.That(result.Error, Is.EqualTo("Network error"));
        });
    }

    #endregion
}
