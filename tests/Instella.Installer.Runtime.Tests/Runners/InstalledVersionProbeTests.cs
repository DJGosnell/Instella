using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Installation;
using Instella.Core.Platform;
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Runtime.Runners;
using Instella.Installer.Testing;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Runners;

/// <summary>What <c>WithNewerVersionPrompt()</c> treats as already installed (hand test: 1.4.0 was re-offered).</summary>
[TestFixture]
public sealed class InstalledVersionProbeTests
{
    private const string AppId = "com.example.probe";
    private InMemoryFileSystem _fs = null!;
    private FakePlatformServices _platform = null!;
    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        _fs = new InMemoryFileSystem();
        _platform = new FakePlatformServices();
        _root = Path.Combine(Path.GetTempPath(), "instella-probe-tests", Guid.NewGuid().ToString("N"));
    }

    [Test]
    public async Task NothingInstalled_IsNull()
    {
        Assert.That(await Probe(Dispatch()), Is.Null);
    }

    [Test]
    public async Task FindsTheInstallationTheInstalledAppsEntryPointsAt()
    {
        var dir = Path.Combine(_root, "Custom", "QuickNotes");
        await Install(dir, new Version(1, 4, 0));
        _platform.Registry.Set(RegistryHive.CurrentUser, UninstallEntryKeys.PathFor(AppId), "InstallLocation",
            InstellaRegistryValueKind.String, dir);

        Assert.That(await Probe(Dispatch()), Is.EqualTo(new Version(1, 4, 0)));
    }

    [Test]
    public async Task NewestOfSeveral_AndOtherAppsIgnored()
    {
        var mine = Path.Combine(_root, "a");
        var older = Path.Combine(_root, "b");
        var other = Path.Combine(_root, "c");
        await Install(mine, new Version(1, 4, 0));
        await Install(older, new Version(1, 3, 0));
        await Install(other, new Version(9, 0, 0), appId: "com.other.app");
        _platform.Registry.Set(RegistryHive.CurrentUser, UninstallEntryKeys.PathFor(AppId), "InstallLocation",
            InstellaRegistryValueKind.String, older);
        _platform.Registry.Set(RegistryHive.LocalMachine, UninstallEntryKeys.PathFor(AppId), "InstallLocation",
            InstellaRegistryValueKind.String, mine);

        Assert.That(await Probe(Dispatch()), Is.EqualTo(new Version(1, 4, 0)));

        // An entry pointing at another app's folder does not count.
        _platform.Registry.Set(RegistryHive.LocalMachine, UninstallEntryKeys.PathFor(AppId), "InstallLocation",
            InstellaRegistryValueKind.String, other);
        Assert.That(await Probe(Dispatch()), Is.EqualTo(new Version(1, 3, 0)));
    }

    [Test]
    public async Task ExplicitPath_IsTheOnlyPlaceLookedAt()
    {
        var registered = Path.Combine(_root, "registered");
        var chosen = Path.Combine(_root, "chosen");
        await Install(registered, new Version(1, 4, 0));
        _platform.Registry.Set(RegistryHive.CurrentUser, UninstallEntryKeys.PathFor(AppId), "InstallLocation",
            InstellaRegistryValueKind.String, registered);

        Assert.That(await Probe(Dispatch(chosen)), Is.Null);
        await Install(chosen, new Version(1, 2, 0));
        Assert.That(await Probe(Dispatch(chosen)), Is.EqualTo(new Version(1, 2, 0)));
    }

    private Task<Version?> Probe(DispatchResult dispatch) =>
        InstalledVersionProbe.FindAsync(Config(), dispatch, _platform, _fs, CancellationToken.None);

    private static DispatchResult Dispatch(string? path = null) =>
        new(DispatchKind.Mode, InstallerMode.FirstInstall, path, IsSilent: false);

    private static FrozenConfig Config() =>
        ((InstellaInstallerImpl)InstellaInstaller.Create().WithApp("QuickNotes", AppId, new Version(1, 2, 0)).Build()).ConfigForTests;

    private Task Install(string dir, Version version, string appId = AppId) =>
        new InstallManifestWriter(_fs).WriteAsync(dir, new InstalledManifest
        {
            AppName = "QuickNotes", AppId = appId, Version = version, InstallDirectory = dir,
            ExecutableName = "QuickNotes.exe", InstalledAt = DateTime.UtcNow, Files = [],
        }, CancellationToken.None);
}
