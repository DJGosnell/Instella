using System;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Platform;
using Instella.Installer.Testing;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Services;

/// <summary>Another product's service that depends on ours is stopped with it for an upgrade and started again afterwards.</summary>
public partial class ServiceInstallTests
{
    [Test]
    public async Task Upgrade_RestartsAnotherAppsServiceThatDependsOnOurs_WhichStopServicesStoppedWithIt()
    {
        Assert.That(await Install(Installer("1.0", w => w.AddService("App.Svc", _ => { }))), Is.EqualTo(0));

        IServiceManager scm = Scm;
        var created = await scm.CreateAsync(new ServiceConfiguration
        {
            Name = "Other.Svc",
            DisplayName = "Other",
            ExecutablePath = @"C:\Other\Other.exe",
            StartType = ServiceStartType.Automatic,
            Dependencies = ["App.Svc"],
        }, null, CancellationToken.None);
        Assert.That(created.Outcome.Success, Is.True);
        Assert.That((await scm.StartAsync("Other.Svc", TimeSpan.FromSeconds(5), CancellationToken.None)).Outcome.Success, Is.True);

        Assert.That(await Install(Installer("2.0", w => w.AddService("App.Svc", _ => { }))), Is.EqualTo(0));

        Assert.That(Scm.Find("App.Svc")!.State, Is.EqualTo(FakeServiceState.Running));
        Assert.That(Scm.Find("Other.Svc")!.State, Is.EqualTo(FakeServiceState.Running),
            "stopping App.Svc for the upgrade stopped its running dependent; the upgrade must start it again");
    }
}
