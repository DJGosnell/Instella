using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Installation;
using Instella.Core.Platform;
using Instella.Installer.Runtime.Core.Services;
using Instella.Installer.Testing;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Services;

/// <summary>
/// Recovery reads the stopped-services side file before it removes the transaction folder, then
/// starts only what the installation records.
/// </summary>
[TestFixture]
public class ServiceRecoveryTests
{
    private const string Root = @"C:\Apps\App";
    private InMemoryFileSystem _fs = null!;
    private FakeServiceManager _scm = null!;

    [SetUp]
    public async Task SetUp()
    {
        _fs = new InMemoryFileSystem();
        _scm = new FakeServiceManager();
        await new InstallManifestWriter(_fs).WriteAsync(Root, new InstalledManifest
        {
            AppName = "App", AppId = "com.app", Version = new Version(1, 0), InstallDirectory = Root,
            ExecutableName = "App.exe", InstalledAt = DateTime.UtcNow, Files = [],
            Services = [new ManifestService("App.Svc", "App.exe", null, ManifestService.Automatic, CreatedByInstella: true)],
        }, CancellationToken.None);
        foreach (var name in new[] { "App.Svc", "Other.Svc" })
        {
            var created = await ((IServiceManager)_scm).CreateAsync(new ServiceConfiguration
            {
                Name = name, DisplayName = name, ExecutablePath = Path.Combine(Root, "App.exe"),
            }, null, CancellationToken.None);
            Assert.That(created.Outcome.Success, Is.True);
        }
    }

    private string SideFile(string txnId) => Path.Combine(Root, ".instella", "txn", txnId, ServicesRunningFile.FileName);

    [Test]
    public async Task Recover_StartsTheListedServicesTheInstallationRecords_AndRemovesTheFolder()
    {
        _fs.AddFile(SideFile("abc"), Encoding.UTF8.GetBytes("""{"services":["Other.Svc","App.Svc"]}"""));

        await ServiceRecovery.RecoverAsync(_fs, Root, declared: null, _scm, new RecordingLogger(new RecordingSink()), CancellationToken.None);

        Assert.That(_scm.Find("App.Svc")!.State, Is.EqualTo(FakeServiceState.Running));
        Assert.That(_scm.Find("Other.Svc")!.State, Is.EqualTo(FakeServiceState.Stopped), "not recorded by this installation");
        Assert.That(_fs.Exists(SideFile("abc")), Is.False);
    }

    [Test]
    public async Task Recover_IgnoresAnUnreadableSideFile()
    {
        _fs.AddFile(SideFile("abc"), Encoding.UTF8.GetBytes("not json"));

        await ServiceRecovery.RecoverAsync(_fs, Root, declared: null, _scm, new RecordingLogger(new RecordingSink()), CancellationToken.None);

        Assert.That(_scm.Find("App.Svc")!.State, Is.EqualTo(FakeServiceState.Stopped));
        Assert.That(_scm.Calls, Has.None.StartsWith("start:"));
    }
}
