using System.Text.Json;
using Instella.Core.Installation;
using Instella.Core.Platform;
using Instella.Core.Update;
using Instella.Sdk;
using Instella.Sdk.Internal;
using NUnit.Framework;

namespace Instella.Sdk.Tests;

/// <summary>Installation info from the installed manifest, updater launch args, post-update flags.</summary>
[TestFixture]
public class SdkContractTests
{
    private string _root = null!;

    [SetUp]
    public void SetUp() => _root = Directory.CreateTempSubdirectory("instella-sdk-").FullName;

    [TearDown]
    public void TearDown() => Directory.Delete(_root, recursive: true);

    [Test]
    public void ManifestLoader_WalksUpFromAPayloadSubdirectory()
    {
        WriteManifest(_root, channel: "beta", perUser: false);
        var appDir = Directory.CreateDirectory(Path.Combine(_root, "app", "bin")).FullName;

        var info = ManifestLoader.TryLoad(appDir, out var reason);

        Assert.That(info, Is.Not.Null, reason);
        Assert.That(info!.InstallRoot, Is.EqualTo(Path.GetFullPath(_root).TrimEnd(Path.DirectorySeparatorChar)));
        Assert.That(info.Channel, Is.EqualTo("beta"), "the channel comes from the manifest, not a default");
        Assert.That(info.IsPerUser, Is.False);
        Assert.That(info.Architecture, Is.EqualTo(Architecture.ARM64));
        Assert.That(info.ExecutableName, Is.EqualTo("bin/QuickNotes.exe"));
    }

    [Test]
    public void ManifestLoader_StopsAfterThreeLevels_AndExplainsWhy()
    {
        WriteManifest(_root, "stable", true);
        var deep = Directory.CreateDirectory(Path.Combine(_root, "a", "b", "c", "d")).FullName;

        Assert.That(ManifestLoader.TryLoad(deep, out var reason), Is.Null);
        Assert.That(reason, Does.Contain("not installed by Instella"));
    }

    [Test]
    public void UpdaterArgs_FromTheSdk_CarryParentPidAndPatchHash_AndNoServer()
    {
        var info = new InstellaInfo
        {
            AppName = "Q", AppId = "com.q", Version = new Version(1, 0), InstallRoot = @"C:\Apps\Q N",
            Channel = "beta", ExecutableName = "Q N.exe",
        };
        var update = new UpdateInfo
        {
            Version = new Version(1, 1), Changelog = "", FullSize = 1,            PatchAvailable = true, PatchSha256 = new string('b', 64), Channel = "beta",
        };

        var args = UpdaterLauncher.BuildArgs(update, new UpdateOptions { AdditionalArgs = ["--open", "x y"] }, info, parentPid: 4242);
        var list = args.ToArgumentList();

        Assert.That(args.ParentPid, Is.EqualTo(4242));
        Assert.That(args.PatchSha256, Is.EqualTo(update.PatchSha256));
        Assert.That(list, Does.Not.Contain("--server-url"));
        Assert.That(UpdaterArgs.Parse(list), Is.EqualTo(args));
    }

    private static void WriteManifest(string root, string channel, bool perUser)
    {
        var manifest = new InstalledManifest
        {
            AppName = "QuickNotes", AppId = "com.q", Version = new Version(1, 0), InstallDirectory = root,
            ExecutableName = "bin/QuickNotes.exe", InstalledAt = DateTime.UtcNow, InstalledPerUser = perUser,
            Channel = channel, Architecture = Architecture.ARM64, Platform = TargetPlatform.Windows,
            ServerUrl = "https://updates.example.com", Files = [],
        };
        File.WriteAllBytes(Path.Combine(root, InstellaOwnedPaths.InstalledManifest),
            JsonSerializer.SerializeToUtf8Bytes(manifest, InstalledManifestJsonContext.Default.InstalledManifest));
    }
}
