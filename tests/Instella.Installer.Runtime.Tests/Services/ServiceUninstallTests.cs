using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Installation;
using Instella.Core.Platform;
using Instella.Installer.Testing;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Services;

/// <summary>
/// Uninstalling an installation with Windows services: they are stopped (dependents first) before
/// the app's uninstall hooks and deleted before any file goes; a service that cannot be stopped
/// stops the uninstall with exit 33 before anything is removed. And a later install that meets a
/// service still marked for deletion.
/// </summary>
public partial class ServiceInstallTests
{
    private Task<int> Uninstall(InstellaTestHarness harness) =>
        harness.RunFullWithArgsAsync(["--uninstall", "--silent", "--path", _installPath]);

    [Test]
    public async Task Uninstall_StopsTheServiceBeforeTheUninstallHooks_AndDeletesIt()
    {
        var seenByHook = new List<FakeServiceState?>();
        var h = Installer("1.0", SimpleService, more: b => b.AddStep("observe-uninstall", sb => sb
            .Execute((_, _, _) => Task.FromResult(StepResult.Ok))
            .NoRollbackNeeded("observes")
            .OnUninstall((_, _) =>
            {
                seenByHook.Add(Scm.Find("App.Svc")?.State);
                return Task.CompletedTask;
            })));
        Assert.That(await Install(h), Is.EqualTo(0));
        Assert.That(Scm.Find("App.Svc")!.State, Is.EqualTo(FakeServiceState.Running));

        var exit = await Uninstall(h);

        Assert.That(exit, Is.EqualTo(0), Log(h));
        Assert.That(seenByHook, Has.Count.EqualTo(1));
        Assert.That(seenByHook[0], Is.Null.Or.EqualTo(FakeServiceState.Stopped), "the hook runs with the service stopped");
        Assert.That(Scm.Find("App.Svc"), Is.Null, "no service entry is left pointing at deleted files");
        Assert.That(Scm.Calls.Skip(2), Is.EqualTo(new[] { "stop:App.Svc", "delete:App.Svc" }));
        Assert.That(Live("App.exe"), Is.Null);
    }

    [Test]
    public async Task Uninstall_StopsDependentsFirst_AndDeletesEveryService()
    {
        var h = Installer("1.0", w => w
            .AddService("App.Core", _ => { })
            .AddService("App.Web", s => s.WithExecutable("bin/App.Worker.exe").DependsOn("App.Core")));
        Assert.That(await Install(h), Is.EqualTo(0));

        var exit = await Uninstall(h);

        Assert.That(exit, Is.EqualTo(0), Log(h));
        var calls = Scm.Calls.ToList();
        Assert.That(calls.IndexOf("stop:App.Web"), Is.GreaterThanOrEqualTo(0).And.LessThan(calls.IndexOf("stop:App.Core")),
            "the dependent stops first");
        Assert.That(calls, Does.Contain("delete:App.Web").And.Contain("delete:App.Core"));
        Assert.That(Scm.Services, Is.Empty);
    }

    [Test]
    public Task Uninstall_ServiceThatWillNotStop_Exits33_AndRemovesNothing() => UninstallRefused(denyAccess: false);

    [Test]
    public Task Uninstall_ServiceAccessDenied_Exits33_AndRemovesNothing() => UninstallRefused(denyAccess: true);

    private async Task UninstallRefused(bool denyAccess)
    {
        var h = Installer("1.0", SimpleService);
        Assert.That(await Install(h), Is.EqualTo(0));
        if (denyAccess) Scm.DenyAccess = true;
        else Scm.SetStopBehavior("App.Svc", FakeServiceBehavior.Fail);

        var exit = await Uninstall(h);

        Assert.That(exit, Is.EqualTo(33), "UninstallServiceFailed: " + Log(h));
        Assert.That(Log(h), Does.Contain("App.Svc"), "the message names the service");
        Assert.That(Scm.Find("App.Svc"), Is.Not.Null, "the service is not deleted");
        Assert.That(Scm.Find("App.Svc")!.State, Is.EqualTo(FakeServiceState.Running));
        Assert.That(Live("App.exe"), Is.EqualTo("app 1.0"), "no file is removed");
        Assert.That(Live("bin/App.Worker.exe"), Is.EqualTo("worker 1.0"));
        Assert.That(await Manifest(), Is.Not.Null, "the installation is still there to uninstall later");
    }

    [Test]
    public async Task Uninstall_ServiceStuckStopping_IsEnded_ThenDeleted()
    {
        var h = Installer("1.0", SimpleService);
        Assert.That(await Install(h), Is.EqualTo(0));
        Scm.SetStopBehavior("App.Svc", FakeServiceBehavior.Hang);

        var exit = await Uninstall(h);

        Assert.That(exit, Is.EqualTo(0), Log(h));
        Assert.That(Scm.Find("App.Svc"), Is.Null, "the stuck service's process was ended, so the delete completed");
        Assert.That(Live("App.exe"), Is.Null);
    }

    [Test]
    public async Task Uninstall_ServiceHeldOpen_FinishesWithAWarning_ThatARestartMayBeNeeded()
    {
        var h = Installer("1.0", SimpleService);
        Assert.That(await Install(h), Is.EqualTo(0));
        Scm.SetHeldOpen("App.Svc", held: true);

        var exit = await Uninstall(h);

        Assert.That(exit, Is.EqualTo(0), Log(h));
        Assert.That(Scm.Find("App.Svc")!.MarkedForDeletion, Is.True);
        Assert.That(Log(h), Does.Contain("marked for deletion").And.Contain("restart"));
        Assert.That(Live("App.exe"), Is.Null, "the uninstall finished");
    }

    [Test]
    public async Task FirstInstall_OverAServiceMarkedForDeletion_Exits16_AndSaysToRestart()
    {
        // What an uninstall can leave behind while Services or Event Viewer holds the service open.
        IServiceManager scm = Scm;
        Assert.That((await scm.CreateAsync(new ServiceConfiguration
        {
            Name = "App.Svc", DisplayName = "App.Svc", ExecutablePath = System.IO.Path.Combine(_installPath, "App.exe"),
        }, null, CancellationToken.None)).Outcome.Success, Is.True);
        Assert.That((await scm.StartAsync("App.Svc", TimeSpan.FromSeconds(1), CancellationToken.None)).Outcome.Success, Is.True);
        Assert.That((await scm.DeleteAsync("App.Svc", CancellationToken.None)).Outcome.Success, Is.True);
        Assert.That(Scm.Find("App.Svc")!.MarkedForDeletion, Is.True);
        var h = Installer("1.0", SimpleService);

        var exit = await Install(h);

        Assert.That(exit, Is.EqualTo((int)InstellaExitCode.InstallServiceFailed), Log(h));
        Assert.That(Log(h), Does.Contain("marked for deletion").IgnoreCase
            .And.Contain("restart").IgnoreCase);
        Assert.That(Live("App.exe"), Is.Null, "nothing was installed");
    }

    [Test, Platform("Win")]
    public async Task Uninstall_DoesNotDelete_AServiceTheInstallationDidNotCreate()
    {
        var fs = new Instella.Installer.Testing.InMemoryFileSystem();
        var platform = new Instella.Installer.Testing.FakePlatformServices();
        var installPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "instella-probe", Guid.NewGuid().ToString("N"), "App");
        var builder = Instella.Installer.Runtime.Builders.InstellaInstaller.Create()
            .WithApp("App", "com.example.app", Version.Parse("1.0"))
            .WithExecutableName("App.exe")
            .WithElevation(Instella.Core.Manifest.ElevationMode.SystemWide)
            .OnWindows(w => w.AddService("App.Svc", s => s.WithArguments("--service")));
        await using var harness = Instella.Installer.Testing.InstellaTestHarness.Create()
            .WithInstaller(builder.Build())
            .WithFileSystem(fs)
            .WithPlatformServices(platform)
            .WithElevationService(new Instella.Installer.Testing.FakeElevationService(isElevated: true))
            .WithPayload(new System.Collections.Generic.Dictionary<string, string> { ["App.exe"] = "app 1.0" })
            .Build();
        Assert.That(await harness.RunFullWithArgsAsync(["--install", "--silent", "--path", installPath]), Is.EqualTo(0));

        var writer = new Instella.Core.Installation.InstallManifestWriter(fs);
        var manifest = (await writer.ReadAsync(installPath, System.Threading.CancellationToken.None))!;
        manifest = manifest with { Services = System.Linq.Enumerable.ToList(System.Linq.Enumerable.Select(manifest.Services!, s => s with { CreatedByInstella = false })) };
        await writer.WriteAsync(installPath, manifest, System.Threading.CancellationToken.None);

        var exit = await harness.RunFullWithArgsAsync(["--uninstall", "--silent", "--path", installPath]);

        Assert.That(exit, Is.EqualTo(0));
        Assert.That(platform.ServiceManager.Find("App.Svc"), Is.Not.Null,
            "a service the installation found already registered (CreatedByInstella false) is not deleted by uninstall");
    }
}
