using Instella.Installer.Runtime.Runners;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Services;

public class ServiceDependentRestartTests
{
    [NUnit.Framework.Test]
    public async System.Threading.Tasks.Task Upgrade_RestartsAnotherAppsServiceThatDependsOnOurs_WhichStopServicesStoppedWithIt()
    {
        if (!System.OperatingSystem.IsWindows()) NUnit.Framework.Assert.Ignore("OnWindows");
        var fs = new Instella.Installer.Testing.InMemoryFileSystem();
        var platform = new Instella.Installer.Testing.FakePlatformServices();
        var installPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "instella-probe-deps", System.Guid.NewGuid().ToString("N"), "App");
        var harnesses = new System.Collections.Generic.List<Instella.Installer.Testing.InstellaTestHarness>();
        Instella.Installer.Testing.InstellaTestHarness Make(string version)
        {
            var builder = Instella.Installer.Runtime.Builders.InstellaInstaller.Create()
                .WithApp("App", "com.example.app", System.Version.Parse(version))
                .WithExecutableName("App.exe")
                .WithElevation(Instella.Core.Manifest.ElevationMode.SystemWide)
                .OnWindows(w => w.AddService("App.Svc", _ => { }));
            var h = Instella.Installer.Testing.InstellaTestHarness.Create()
                .WithInstaller(builder.Build())
                .WithFileSystem(fs)
                .WithPlatformServices(platform)
                .WithElevationService(new Instella.Installer.Testing.FakeElevationService(isElevated: true))
                .WithPayload(new System.Collections.Generic.Dictionary<string, string> { ["App.exe"] = "app " + version })
                .Build();
            harnesses.Add(h);
            return h;
        }
    
        try
        {
            NUnit.Framework.Assert.That(await Make("1.0").RunFullWithArgsAsync(new[] { "--install", "--silent", "--path", installPath }), NUnit.Framework.Is.EqualTo(0));
    
            var scm = (Instella.Core.Platform.IServiceManager)platform.ServiceManager;
            var created = await scm.CreateAsync(new Instella.Core.Platform.ServiceConfiguration
            {
                Name = "Other.Svc",
                DisplayName = "Other",
                ExecutablePath = @"C:\Other\Other.exe",
                StartType = Instella.Core.Platform.ServiceStartType.Automatic,
                Dependencies = new[] { "App.Svc" },
            }, null, System.Threading.CancellationToken.None);
            NUnit.Framework.Assert.That(created.Outcome.Success, NUnit.Framework.Is.True);
            NUnit.Framework.Assert.That((await scm.StartAsync("Other.Svc", System.TimeSpan.FromSeconds(5), System.Threading.CancellationToken.None)).Outcome.Success, NUnit.Framework.Is.True);
    
            NUnit.Framework.Assert.That(await Make("2.0").RunFullWithArgsAsync(new[] { "--install", "--silent", "--path", installPath }), NUnit.Framework.Is.EqualTo(0));
    
            NUnit.Framework.Assert.That(platform.ServiceManager.Find("App.Svc")!.State, NUnit.Framework.Is.EqualTo(Instella.Installer.Testing.FakeServiceState.Running));
            NUnit.Framework.Assert.That(platform.ServiceManager.Find("Other.Svc")!.State, NUnit.Framework.Is.EqualTo(Instella.Installer.Testing.FakeServiceState.Running),
                "stopping App.Svc for the upgrade stopped its running dependent; the upgrade must start it again");
        }
        finally
        {
            foreach (var h in harnesses) await h.DisposeAsync();
        }
    }
}
