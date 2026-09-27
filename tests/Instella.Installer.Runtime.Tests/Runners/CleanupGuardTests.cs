using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.FileSystem;
using Instella.Core.Installation;
using Instella.Core.Logging;
using Instella.Core.Platform;
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Runtime.Core;
using Instella.Installer.Runtime.Runners;
using Instella.Installer.Testing;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Runners;

/// <summary>Cleanup is tombstone-authorised and refuses anything else.</summary>
[TestFixture]
public class CleanupGuardTests
{
    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "instella-cleanup-tests", Guid.NewGuid().ToString("N"), "QuickNotes");
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true); } catch { /* best effort */ }
    }

    [Test]
    public void RefusesVolumeRoot() =>
        Assert.That(CleanupModeRunner.ValidateCleanupTarget(Path.GetPathRoot(Path.GetTempPath())!, "t"), Does.Contain("volume root"));

    [TestCase(Environment.SpecialFolder.UserProfile)]
    [TestCase(Environment.SpecialFolder.ProgramFiles)]
    [TestCase(Environment.SpecialFolder.LocalApplicationData)]
    public void RefusesProtectedFolders(Environment.SpecialFolder folder)
    {
        var path = Environment.GetFolderPath(folder);
        if (string.IsNullOrEmpty(path)) Assert.Ignore($"{folder} is not defined on this host");
        Assert.That(CleanupModeRunner.ValidateCleanupTarget(path, "t"), Does.Contain("protected folder"));
    }

    [Test]
    public void RefusesAnAncestorOfAProtectedFolder()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var parent = Path.GetDirectoryName(profile.TrimEnd('\\', '/'));
        if (string.IsNullOrEmpty(parent) || Path.GetPathRoot(parent) == parent) Assert.Ignore("no ancestor below the volume root");
        Assert.That(CleanupModeRunner.ValidateCleanupTarget(parent, "t"), Does.Contain("protected folder"));
    }

    [Test]
    public void RefusesAFolderWithoutATombstone() =>
        Assert.That(CleanupModeRunner.ValidateCleanupTarget(_root, "t"), Is.EqualTo("no uninstall tombstone"));

    [Test]
    public void RefusesAWrongToken()
    {
        WriteTombstone(_root, "right-token", _root, ["instella.exe"]);
        Assert.That(CleanupModeRunner.ValidateCleanupTarget(_root, "wrong-token"), Is.EqualTo("tombstone token mismatch"));
    }

    [Test]
    public void RefusesATombstoneForAnotherRoot()
    {
        WriteTombstone(_root, "tok", Path.Combine(Path.GetTempPath(), "elsewhere"), ["instella.exe"]);
        Assert.That(CleanupModeRunner.ValidateCleanupTarget(_root, "tok"), Is.EqualTo("tombstone root mismatch"));
    }

    [Test]
    public void AcceptsAMatchingTombstone()
    {
        WriteTombstone(_root, "tok", _root, ["instella.exe"]);
        Assert.That(CleanupModeRunner.ValidateCleanupTarget(_root, "tok"), Is.Null);
    }

    [Test]
    public async Task Sweep_DeletesOnlyListedFiles_AndLeavesUserFiles()
    {
        File.WriteAllText(Path.Combine(_root, "instella.exe"), "stub");
        Directory.CreateDirectory(Path.Combine(_root, "data"));
        File.WriteAllText(Path.Combine(_root, "data", "notes.txt"), "the user's own file");
        Directory.CreateDirectory(Path.Combine(_root, "empty", "nested"));
        WriteTombstone(_root, "tok", _root, ["instella.exe"]);

        var exit = await Cleanup("tok");

        Assert.That(exit, Is.EqualTo(InstellaExitCode.Success));
        Assert.That(File.Exists(Path.Combine(_root, "instella.exe")), Is.False);
        Assert.That(File.Exists(Path.Combine(_root, "data", "notes.txt")), Is.True, "files Instella did not install stay");
        Assert.That(Directory.Exists(Path.Combine(_root, ".instella")), Is.False);
        Assert.That(Directory.Exists(Path.Combine(_root, "empty")), Is.False, "empty directories go");
    }

    [Test]
    public async Task Sweep_WithWrongToken_DeletesNothing()
    {
        File.WriteAllText(Path.Combine(_root, "instella.exe"), "stub");
        WriteTombstone(_root, "tok", _root, ["instella.exe"]);

        var exit = await Cleanup("nope");

        Assert.That(exit, Is.EqualTo(InstellaExitCode.UsageInvalidArgs));
        Assert.That(File.Exists(Path.Combine(_root, "instella.exe")), Is.True);
    }

    [Test]
    public async Task Uninstall_WithLockedStub_WritesTombstone_AndCleanupFinishesTheJob()
    {
        // A real install directory with a stub the uninstaller cannot delete (held open).
        File.WriteAllText(Path.Combine(_root, "App.exe"), "app");
        var stub = Path.Combine(_root, InstellaOwnedPaths.StubFileName);
        File.WriteAllText(stub, "stub");
        await new InstallManifestWriter(RealFileSystem.Instance).WriteAsync(_root, new InstalledManifest
        {
            AppName = "App", AppId = "com.app", Version = new Version(1, 0), InstallDirectory = _root,
            ExecutableName = "App.exe", InstalledAt = DateTime.UtcNow, InstalledPerUser = true,
            Files = [new InstalledFile("App.exe", "", 3)],
        }, CancellationToken.None);

        (string Path, string Token)? launched = null;
        var config = ((InstellaInstallerImpl)InstellaInstaller.Create().WithApp("App", "com.app", new Version(1, 0)).Build()).ConfigForTests;
        var uninstall = new UninstallModeRunner(config, new NullLog(), new FakePlatformServices(), RealFileSystem.Instance)
        {
            CleanupLauncher = (path, token) => launched = (path, token),
        };

        InstellaExitCode exit;
        using (new FileStream(stub, FileMode.Open, FileAccess.Read, FileShare.None))
            exit = await uninstall.RunAsync(new DispatchResult(DispatchKind.Mode, InstallerMode.Uninstall, _root, IsSilent: true), CancellationToken.None);

        Assert.That(exit, Is.EqualTo(InstellaExitCode.Success));
        Assert.That(launched, Is.Not.Null, "cleanup was scheduled for the locked stub");
        Assert.That(CleanupModeRunner.ReadTombstone(_root)!.Remaining, Is.EqualTo(new[] { InstellaOwnedPaths.StubFileName }));
        Assert.That(File.Exists(Path.Combine(_root, "App.exe")), Is.False);

        Assert.That(await Cleanup(launched!.Value.Token), Is.EqualTo(InstellaExitCode.Success));
        Assert.That(Directory.Exists(_root), Is.False, "nothing is left of the install directory");
    }

    private Task<InstellaExitCode> Cleanup(string token) =>
        new CleanupModeRunner(new NullLog(), [TimeSpan.Zero], TimeSpan.Zero).RunAsync(
            new DispatchResult(DispatchKind.Mode, InstallerMode.Cleanup, _root),
            ["--cleanup", "--path", _root, "--token", token], CancellationToken.None);

    private static void WriteTombstone(string root, string nonce, string installRoot, IReadOnlyList<string> remaining)
    {
        var path = Path.Combine(root, ".instella", "uninstall.tombstone");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(
            new UninstallTombstone("com.app", installRoot, nonce, DateTimeOffset.UtcNow, remaining),
            CleanupJsonContext.Default.UninstallTombstone));
    }

    private sealed class NullLog : IInstellaLogger
    {
        public void Trace(string message) { }
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
        public IDisposable Scope(string segment) => new MemoryStream();
        public bool IsEnabled(InstellaLogLevel level) => false;
    }
}
