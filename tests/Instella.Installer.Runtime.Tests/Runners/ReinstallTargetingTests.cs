using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Installation;
using Instella.Core.Manifest;
using Instella.Core.Platform;
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Runtime.Core.Elevation;
using Instella.Installer.Runtime.Runners;
using Instella.Installer.Testing;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Runners;

/// <summary>
/// A re-install without <c>--path</c> targets the existing installation's folder and scope.
/// Installing to the default folder instead would make a second copy that takes over the
/// Installed Apps entry and the shortcuts (and, machine vs user, leaves two Run entries).
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class ReinstallTargetingTests
{
    private const string AppId = "com.example.target";
    private InMemoryFileSystem _fs = null!;
    private FakePlatformServices _platform = null!;

    [SetUp]
    public void SetUp()
    {
        _fs = new InMemoryFileSystem();
        _platform = new FakePlatformServices();
    }

    // ---- the probe ----

    [TestCase(RegistryHive.CurrentUser, true, "HKCU")]
    [TestCase(RegistryHive.LocalMachine, false, "HKLM")]
    public async Task Probe_FindsTheInstallationTheEntryPointsAt_WithItsScope(RegistryHive hive, bool perUser, string source)
    {
        var dir = Path.Combine(Path.GetTempPath(), "instella-target", Guid.NewGuid().ToString("N"));
        await Manifest(dir, new Version(1, 0, 0), perUser);
        _platform.Registry.Set(hive, UninstallEntryKeys.PathFor(AppId), "InstallLocation", InstellaRegistryValueKind.String, dir + "\\");

        var found = await InstalledVersionProbe.FindInstallationsAsync(Config(ElevationMode.UserChoice), _platform, _fs, CancellationToken.None);

        Assert.That(found.Single().Path, Is.EqualTo(dir), "normalised");
        Assert.That(found.Single().Scope, Is.EqualTo(perUser ? InstallationScope.PerUser : InstallationScope.SystemWide));
        Assert.That(found.Single().Source, Is.EqualTo(source));
    }

    [Test]
    public async Task Probe_BothScopes_AreFound_AndDuplicatesAndStaleEntriesSkipped()
    {
        var config = Config(ElevationMode.UserChoice);
        var user = InstallPaths.Default(config, _platform, InstallationScope.PerUser);
        var machine = InstallPaths.Default(config, _platform, InstallationScope.SystemWide);
        await Manifest(user, new Version(1, 0, 0), perUser: true);
        await Manifest(machine, new Version(1, 1, 0), perUser: false);
        _platform.Registry.Set(RegistryHive.CurrentUser, UninstallEntryKeys.PathFor(AppId), "InstallLocation", InstellaRegistryValueKind.String, user);
        _platform.Registry.Set(RegistryHive.LocalMachine, UninstallEntryKeys.PathFor(AppId), "InstallLocation", InstellaRegistryValueKind.String,
            @"C:\Gone\Target");

        var found = await InstalledVersionProbe.FindInstallationsAsync(config, _platform, _fs, CancellationToken.None);

        Assert.That(found.Select(f => (f.Path, f.Scope)), Is.EqualTo(new[]
        {
            (user, InstallationScope.PerUser), (machine, InstallationScope.SystemWide),
        }));
    }

    [Test]
    public async Task Probe_AnotherAppsFolder_IsNotAnInstallation()
    {
        var config = Config(ElevationMode.UserChoice);
        await Manifest(InstallPaths.Default(config, _platform, InstallationScope.PerUser), new Version(1, 0, 0), perUser: true, appId: "com.other");

        Assert.That(await InstalledVersionProbe.FindInstallationsAsync(config, _platform, _fs, CancellationToken.None), Is.Empty);
    }

    // ---- silent runs ----

    [Test]
    public async Task SilentReinstall_WithoutPath_UpgradesTheMachineInstallInPlace()
    {
        Assert.That(await Install(new Version(1, 0, 0), ElevationMode.SystemWide, "--silent"), Is.Zero);
        var machine = InstallPaths.Default(Config(ElevationMode.SystemWide), _platform, InstallationScope.SystemWide);

        // A UserChoice installer run silently defaults to "user": it must find the machine install instead.
        Assert.That(await Install(new Version(2, 0, 0), ElevationMode.UserChoice, "--silent"), Is.Zero);

        var manifest = (await new InstallManifestWriter(_fs).ReadAsync(machine, CancellationToken.None))!;
        Assert.That(manifest.Version, Is.EqualTo(new Version(2, 0, 0)));
        Assert.That(manifest.InstalledPerUser, Is.False);
        var user = InstallPaths.Default(Config(ElevationMode.UserChoice), _platform, InstallationScope.PerUser);
        Assert.That(_fs.Exists(Path.Combine(user, InstellaOwnedPaths.InstalledManifest)), Is.False, "no second copy");
    }

    [Test]
    public async Task SilentReinstall_InstalledTwice_Exits40_UnlessScopeChooses()
    {
        Assert.That(await Install(new Version(1, 0, 0), ElevationMode.PerUser, "--silent"), Is.Zero);
        // A second copy can only be made on purpose: an explicit --path.
        var machinePath = InstallPaths.Default(Config(ElevationMode.SystemWide), _platform, InstallationScope.SystemWide);
        Assert.That(await Install(new Version(1, 0, 0), ElevationMode.SystemWide, "--silent", "--path", machinePath), Is.Zero);

        Assert.That(await Install(new Version(2, 0, 0), ElevationMode.UserChoice, "--silent"), Is.EqualTo((int)InstellaExitCode.UsageInvalidArgs));
        Assert.That(await Install(new Version(2, 0, 0), ElevationMode.UserChoice, "--silent", "--scope", "machine"), Is.Zero);

        var machine = InstallPaths.Default(Config(ElevationMode.UserChoice), _platform, InstallationScope.SystemWide);
        Assert.That((await new InstallManifestWriter(_fs).ReadAsync(machine, CancellationToken.None))!.Version, Is.EqualTo(new Version(2, 0, 0)));
    }

    [Test]
    public async Task PathOfAMachineInstall_WithScopeUser_Exits40()
    {
        Assert.That(await Install(new Version(1, 0, 0), ElevationMode.SystemWide, "--silent"), Is.Zero);
        var machine = InstallPaths.Default(Config(ElevationMode.SystemWide), _platform, InstallationScope.SystemWide);

        Assert.That(await Install(new Version(2, 0, 0), ElevationMode.UserChoice, "--silent", "--path", machine, "--scope", "user"),
            Is.EqualTo((int)InstellaExitCode.UsageInvalidArgs));
    }

    // ---- interactive runs ----

    [Test]
    public async Task InteractiveReinstall_OfAMachineInstall_SkipsTheScopePage_AndRelaunchesForThatFolder()
    {
        var config = Config(ElevationMode.UserChoice);
        var machine = InstallPaths.Default(config, _platform, InstallationScope.SystemWide);
        await Manifest(machine, new Version(1, 0, 0), perUser: false);
        var dispatch = new DispatchResult(DispatchKind.Mode, InstallerMode.FirstInstall, null, IsSilent: false);
        var elevation = new RecordingElevation(elevated: false);

        var (targeted, error) = await ExistingInstallTargeting.ApplyAsync(config, dispatch, _platform, _fs, CancellationToken.None);
        var (_, exit) = await ElevationGate.ResolveInstallScopeAsync(config, ["--install"], targeted, elevation,
            (_, _) => throw new AssertionException("no scope page for an existing installation"), new NullLog(), CancellationToken.None);

        Assert.That(error, Is.Null);
        Assert.That(exit, Is.EqualTo(InstellaExitCode.Success));
        Assert.That(elevation.Relaunches.Single(), Is.EqualTo(new[] { "--install", "--scope", "machine", "--path", machine, "--elevated-child" }));
    }

    [Test]
    public async Task InteractiveReinstall_InstalledTwice_ScopePageNamesBoth_AndTheChoiceSelects()
    {
        var config = Config(ElevationMode.UserChoice);
        var user = InstallPaths.Default(config, _platform, InstallationScope.PerUser);
        await Manifest(user, new Version(1, 0, 0), perUser: true);
        await Manifest(InstallPaths.Default(config, _platform, InstallationScope.SystemWide), new Version(1, 0, 0), perUser: false);
        string? notice = null;

        var (targeted, _) = await ExistingInstallTargeting.ApplyAsync(config,
            new DispatchResult(DispatchKind.Mode, InstallerMode.FirstInstall, null, IsSilent: false), _platform, _fs, CancellationToken.None);
        var (resolved, exit) = await ElevationGate.ResolveInstallScopeAsync(config, ["--install"], targeted, new RecordingElevation(false),
            (_, n) => { notice = n; return "user"; }, new NullLog(), CancellationToken.None);

        Assert.That(exit, Is.Null);
        Assert.That(notice, Does.Contain("installed more than once").And.Contain(user));
        Assert.That(resolved!.Value.InstallPath, Is.EqualTo(user));
        Assert.That(resolved.Value.Existing!.Scope, Is.EqualTo(InstallationScope.PerUser));
    }

    // ---- helpers ----

    private async Task<int> Install(Version version, ElevationMode elevation, params string[] args)
    {
        var installer = (InstellaInstallerImpl)InstellaInstaller.Create()
            .WithApp("Target", AppId, version).WithExecutableName("Target.exe").WithElevation(elevation).Build();
        return await installer.RunAsync(args, new InstallerServices(_platform, _fs, Elevation: new RecordingElevation(elevated: true),
            Payload: () => Zip(new() { ["Target.exe"] = "v" + version })), CancellationToken.None);
    }

    private static FrozenConfig Config(ElevationMode elevation) =>
        ((InstellaInstallerImpl)InstellaInstaller.Create().WithApp("Target", AppId, new Version(2, 0, 0))
            .WithExecutableName("Target.exe").WithElevation(elevation).Build()).ConfigForTests;

    private Task Manifest(string dir, Version version, bool perUser, string appId = AppId) =>
        new InstallManifestWriter(_fs).WriteAsync(dir, new InstalledManifest
        {
            AppName = "Target", AppId = appId, Version = version, InstallDirectory = dir,
            ExecutableName = "Target.exe", InstalledAt = DateTime.UtcNow, InstalledPerUser = perUser, Files = [],
        }, CancellationToken.None);

    private static MemoryStream Zip(Dictionary<string, string> files)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, text) in files)
            {
                using var s = zip.CreateEntry(path).Open();
                s.Write(Encoding.UTF8.GetBytes(text));
            }
        }
        ms.Position = 0;
        return ms;
    }

    private sealed class RecordingElevation(bool elevated) : IElevationService
    {
        public bool IsElevated => elevated;
        public List<IReadOnlyList<string>> Relaunches { get; } = [];

        public Task<int?> RelaunchElevatedAsync(IReadOnlyList<string> args, CancellationToken ct)
        {
            Relaunches.Add(args);
            return Task.FromResult<int?>(0);
        }
    }

    private sealed class NullLog : Instella.Core.Logging.IInstellaLogger
    {
        public void Trace(string message) { }
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
        public IDisposable Scope(string segment) => new MemoryStream();
        public bool IsEnabled(Instella.Core.Logging.InstellaLogLevel level) => false;
    }
}
