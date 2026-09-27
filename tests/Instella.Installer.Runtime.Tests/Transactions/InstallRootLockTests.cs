using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Installation;
using Instella.Core.Logging;
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Runtime.Core.Transactions;
using Instella.Installer.Runtime.Runners;
using Instella.Installer.Testing;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Transactions;

/// <summary>
/// One Instella process per install folder. Without the lock, opening Uninstall while the
/// updater commits would run recovery, which rolls the live commit back halfway through its renames.
/// </summary>
[TestFixture]
[NonParallelizable]
public class InstallRootLockTests
{
    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "instella-lock-tests", Guid.NewGuid().ToString("N"));
        InstallRootLock.ForceSeparateOwner = true;
    }

    [TearDown]
    public void TearDown() => InstallRootLock.ForceSeparateOwner = false;

    [Test]
    public async Task SecondOwner_TimesOutWhileHeld_AndSucceedsAfterRelease()
    {
        var first = await InstallRootLock.TryAcquireAsync(_root, TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.That(first, Is.Not.Null);

        var second = await InstallRootLock.TryAcquireAsync(_root, TimeSpan.FromMilliseconds(100), CancellationToken.None);
        Assert.That(second, Is.Null, "busy while the first owner holds it");

        await first!.DisposeAsync();
        var third = await InstallRootLock.TryAcquireAsync(_root, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.That(third, Is.Not.Null);
        await third!.DisposeAsync();
    }

    [Test]
    public async Task ReleaseFromAnotherThread_AfterAwaits_Works()
    {
        // A Mutex is owned by a thread; a hold that spans awaits must still release cleanly.
        var held = await InstallRootLock.TryAcquireAsync(_root, TimeSpan.FromSeconds(1), CancellationToken.None);
        await Task.Yield();
        await Task.Yield();
        await Task.Yield();
        await Task.Run(async () => await held!.DisposeAsync());

        var again = await InstallRootLock.TryAcquireAsync(_root, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.That(again, Is.Not.Null);
        await again!.DisposeAsync();
    }

    [Test]
    public async Task ReentrantAcquire_InOneOwner_Succeeds()
    {
        InstallRootLock.ForceSeparateOwner = false;
        var outer = await InstallRootLock.TryAcquireAsync(_root, TimeSpan.FromSeconds(1), CancellationToken.None);
        var inner = await InstallRootLock.TryAcquireAsync(_root, TimeSpan.FromMilliseconds(100), CancellationToken.None);
        Assert.That(inner, Is.Not.Null, "Manage and the updater it drives share one hold");

        await inner!.DisposeAsync();
        InstallRootLock.ForceSeparateOwner = true;
        var other = await InstallRootLock.TryAcquireAsync(_root, TimeSpan.FromMilliseconds(100), CancellationToken.None);
        Assert.That(other, Is.Null, "the outer hold is still there");

        await outer!.DisposeAsync();
    }

    [Test]
    public void TheName_IsTheSameForEverySpellingOfTheFolder()
    {
        Assert.That(InstallRootLock.MutexName(_root + Path.DirectorySeparatorChar), Is.EqualTo(InstallRootLock.MutexName(_root)));
        if (OperatingSystem.IsWindows())
            Assert.That(InstallRootLock.MutexName(_root.ToUpperInvariant()), Is.EqualTo(InstallRootLock.MutexName(_root)));
        Assert.That(InstallRootLock.MutexName(_root), Does.StartWith(@"Global\Instella-").And.Length.EqualTo(@"Global\Instella-".Length + 32));
    }

    [Test]
    public async Task Uninstall_WhileAnUpdateHoldsTheFolder_Exits52()
    {
        var fs = new InMemoryFileSystem();
        await new InstallManifestWriter(fs).WriteAsync(_root, new InstalledManifest
        {
            AppName = "App", AppId = "com.app", Version = new Version(1, 0), InstallDirectory = _root,
            ExecutableName = "App.exe", InstalledAt = DateTime.UtcNow, InstalledPerUser = true, Files = [],
        }, CancellationToken.None);
        var config = ((InstellaInstallerImpl)InstellaInstaller.Create().WithApp("App", "com.app", new Version(1, 0)).Build()).ConfigForTests;
        var sink = new RecordingSink();
        await using var update = await InstallRootLock.TryAcquireAsync(_root, TimeSpan.FromSeconds(1), CancellationToken.None);

        var exit = await new UninstallModeRunner(config, new RecordingLogger(sink), new FakePlatformServices(), fs)
        {
            LockTimeout = TimeSpan.FromMilliseconds(100),
        }.RunAsync(new DispatchResult(DispatchKind.Mode, InstallerMode.Uninstall, _root, IsSilent: true), CancellationToken.None);

        Assert.That(exit, Is.EqualTo(InstellaExitCode.InstallationBusy));
        Assert.That(fs.Exists(Path.Combine(_root, InstellaOwnedPaths.InstalledManifest)), Is.True, "nothing was touched");
        Assert.That(sink.Entries, Has.Some.Matches<InstellaLogEntry>(e => e.Message.Contains("Another setup, update or uninstall of App is running")));
    }
}
