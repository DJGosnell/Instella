using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Installation;
using Instella.Core.Update;
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Runtime.Core.Transactions;
using Instella.Installer.Runtime.Runners;
using Instella.Installer.Runtime.Tests.Transactions;
using Instella.Installer.Testing;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Runners;

/// <summary>The recover and update mode runners' own contracts.</summary>
[TestFixture]
public sealed class RecoverAndUpdateRunnerTests
{
    private static readonly TimeSpan[] NoDelays = [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero];
    private readonly string _root = Path.Combine(Path.GetTempPath(), "instella-runner-tests", Guid.NewGuid().ToString("N"));
    private Func<string?> _savedStub = null!;

    // The updater only updates the folder its stub lives in; these tests use _root for both.
    [SetUp]
    public void SetUp()
    {
        _savedStub = InstallPaths.StubDirectory;
        InstallPaths.StubDirectory = () => _root;
    }

    [TearDown]
    public void TearDown() => InstallPaths.StubDirectory = _savedStub;

    [Test]
    public async Task Update_OfAFolderThatIsNotTheStubs_Exits40_WithoutReadingIt()
    {
        var fs = new InMemoryFileSystem();
        var other = Path.Combine(Path.GetTempPath(), "instella-runner-tests", Guid.NewGuid().ToString("N"));
        var args = new UpdaterArgs
        {
            AppPath = other, AppExecutable = "app.exe", FromVersion = new Version(1, 0), ToVersion = new Version(1, 1),
        }.ToArgumentList().ToArray();

        var exit = await Runner(fs).RunAsync(args, CancellationToken.None);

        Assert.That(exit, Is.EqualTo(InstellaExitCode.UsageInvalidArgs));
    }

    [Test]
    public async Task Update_OfAnotherAppsInstallation_Exits20()
    {
        var fs = new InMemoryFileSystem();
        await new InstallManifestWriter(fs).WriteAsync(_root, new InstalledManifest
        {
            AppName = "Other", AppId = "com.other", Version = new Version(1, 0), InstallDirectory = _root,
            ExecutableName = "app.exe", InstalledAt = DateTime.UtcNow, ServerUrl = "https://updates.example.com", Files = [],
        }, CancellationToken.None);
        var args = new UpdaterArgs
        {
            AppPath = _root + Path.DirectorySeparatorChar, AppExecutable = "app.exe", FromVersion = new Version(1, 0), ToVersion = new Version(1, 1),
        }.ToArgumentList().ToArray();
        var sink = new RecordingSink();

        var exit = await Runner(fs, sink).RunAsync(args, CancellationToken.None);

        Assert.That(exit, Is.EqualTo(InstellaExitCode.UpdateGeneralFailure));
        Assert.That(sink.Entries.Any(e => e.Message.Contains("belongs to another app")), Is.True);
    }

    [Test]
    public async Task Recover_RollsBackAnInterruptedCommit_AndSucceeds()
    {
        var fs = new InMemoryFileSystem();
        var app = Path.Combine(_root, "app.exe");
        fs.AddFile(app, Encoding.UTF8.GetBytes("v1"));
        var faulty = new FaultInjectingFileSystem(fs);
        var txn = await InstallTransaction.BeginAsync(faulty, _root, TxnKind.Update, new Version(1, 0), new Version(2, 0), CancellationToken.None, retryDelays: NoDelays);
        await txn.StageFileAsync("app.exe", new MemoryStream(Encoding.UTF8.GetBytes("v2")), null, null, false, CancellationToken.None);
        await txn.VerifyAsync(CancellationToken.None);
        faulty.Arm(2);   // the journal reaches Committing; the first live rename fails
        Assert.ThrowsAsync<IOException>(() => txn.CommitAsync());

        var exit = await new RecoverModeRunner(new RecordingLogger(new RecordingSink()), fs)
            .RunAsync(new DispatchResult(DispatchKind.Mode, InstallerMode.Recover, _root), CancellationToken.None);

        Assert.That(exit, Is.EqualTo(InstellaExitCode.Success));
        Assert.That(Encoding.UTF8.GetString((await fs.ReadAllBytesAsync(app, CancellationToken.None)).Value!), Is.EqualTo("v1"));
    }

    [Test]
    public async Task Recover_AJournalOfAnotherVersion_Exits23()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile(Path.Combine(_root, "app.exe"), Encoding.UTF8.GetBytes("v1"));
        await InstallTransaction.BeginAsync(fs, _root, TxnKind.Update, new Version(1, 0), new Version(2, 0), CancellationToken.None);
        var journal = fs.EnumerateFiles(Path.Combine(_root, ".instella"), TransactionJournal.FileName, recursive: true).Single();
        var json = Encoding.UTF8.GetString((await fs.ReadAllBytesAsync(journal, CancellationToken.None)).Value!);
        await fs.WriteAllBytesAsync(journal, Encoding.UTF8.GetBytes(json.Replace("\"journalVersion\": 1", "\"journalVersion\": 9")), CancellationToken.None);

        var exit = await new RecoverModeRunner(new RecordingLogger(new RecordingSink()), fs)
            .RunAsync(new DispatchResult(DispatchKind.Mode, InstallerMode.Recover, _root), CancellationToken.None);

        Assert.That(exit, Is.EqualTo(InstellaExitCode.UpdateRollbackFailed));
    }

    [Test]
    public async Task Update_WithInvalidArguments_Exits40()
    {
        var exit = await Runner(new InMemoryFileSystem()).RunAsync(["--update", "--app-path"], CancellationToken.None);
        Assert.That(exit, Is.EqualTo(InstellaExitCode.UsageInvalidArgs));
    }

    [Test]
    public async Task Update_WithoutAnInstalledManifest_Exits20_AndTouchesNothing()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile(Path.Combine(_root, "app.exe"), Encoding.UTF8.GetBytes("v1"));
        var args = new UpdaterArgs
        {
            AppPath = _root, AppExecutable = "app.exe", FromVersion = new Version(1, 0), ToVersion = new Version(1, 1),
        }.ToArgumentList().ToArray();

        var exit = await Runner(fs).RunAsync(args, CancellationToken.None);

        Assert.That(exit, Is.EqualTo(InstellaExitCode.UpdateGeneralFailure));
        Assert.That(fs.Snapshot().Keys, Is.EquivalentTo(new[] { Path.Combine(_root, "app.exe") }));
    }

    [TestCase(false, ExpectedResult = true)]
    [TestCase(true, ExpectedResult = false)]
    public async Task<bool> Update_PlainHttpServerInTheInstalledManifest_IsRefusedUnlessAllowed(bool allowInsecure)
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile(Path.Combine(_root, "app.exe"), Encoding.UTF8.GetBytes("v1"));
        await new InstallManifestWriter(fs).WriteAsync(_root, new InstalledManifest
        {
            AppName = "App", AppId = "com.app", Version = new Version(1, 0), InstallDirectory = _root,
            ExecutableName = "app.exe", InstalledAt = DateTime.UtcNow, Platform = Instella.Core.Platform.TargetPlatform.Windows,
            // Port 1 on a documentation address: a request, if made, fails fast.
            ServerUrl = "http://192.0.2.1:1", AllowInsecureServer = allowInsecure, Files = [],
        }, CancellationToken.None);
        var args = new UpdaterArgs
        {
            AppPath = _root, AppExecutable = "app.exe", FromVersion = new Version(1, 0), ToVersion = new Version(1, 1), Silent = true,
        }.ToArgumentList().ToArray();
        var sink = new RecordingSink();

        await Runner(fs, sink).RunAsync(args, CancellationToken.None);

        return sink.Entries.Any(e => e.Message.Contains("plain http"));
    }

    private static UpdateModeRunner Runner(InMemoryFileSystem fs, RecordingSink? sink = null)
    {
        var config = ((InstellaInstallerImpl)InstellaInstaller.Create().WithApp("App", "com.app", new Version(1, 0)).Build()).ConfigForTests;
        return new UpdateModeRunner(config, new RecordingLogger(sink ?? new RecordingSink()), new FakePlatformServices(), fs);
    }
}
