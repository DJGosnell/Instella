using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Services;

/// <summary>
/// The <c>--service-password-file</c> of a silent upgrade that keeps the recorded account: an empty
/// file must never replace the password the service manager stores.
/// </summary>
[TestFixture]
[NonParallelizable]
public class ServicePasswordFileTests
{
    [Test]
    public async System.Threading.Tasks.Task AnEmptyPasswordFile_OnAnUpgradeThatKeepsTheAccount_DoesNotOverwriteTheStoredPassword()
    {
        const string user = @"CORP\svc-app";
        const string password = "Pa55-w0rd-7f3c9e1d";
        var fs = new Instella.Installer.Testing.InMemoryFileSystem();
        var platform = new Instella.Installer.Testing.FakePlatformServices();
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "instella-probe-empty-pw", System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(root);
        var installPath = System.IO.Path.Combine(root, "App");
        var passwordFile = System.IO.Path.Combine(root, "password.txt");
        var harnesses = new System.Collections.Generic.List<Instella.Installer.Testing.InstellaTestHarness>();
        Instella.Installer.Testing.InstellaTestHarness Installer(string version)
        {
            var builder = Instella.Installer.Runtime.Builders.InstellaInstaller.Create()
                .WithApp("App", "com.example.app", System.Version.Parse(version))
                .WithExecutableName("App.exe")
                .WithElevation(Instella.Core.Manifest.ElevationMode.SystemWide)
                .OnWindows(w => w.AddService("App.Svc", s => s.RunAs(Instella.Installer.Runtime.Builders.ServiceAccount.FromInstaller())));
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
            platform.ServiceManager.SetAccountPassword(user, password);
            System.IO.File.WriteAllText(passwordFile, password);
            var first = await Installer("1.0").RunFullWithArgsAsync(
                new[] { "--install", "--silent", "--path", installPath, "--service-account", user, "--service-password-file", passwordFile });
            NUnit.Framework.Assert.That(first, NUnit.Framework.Is.EqualTo(0));

            System.IO.File.WriteAllText(passwordFile, "");
            await Installer("2.0").RunFullWithArgsAsync(
                new[] { "--install", "--silent", "--path", installPath, "--service-password-file", passwordFile });

            NUnit.Framework.Assert.That(platform.ServiceManager.Find("App.Svc")!.State,
                NUnit.Framework.Is.EqualTo(Instella.Installer.Testing.FakeServiceState.Running),
                "an empty password file replaced the service's stored password with an empty one");
        }
        finally
        {
            foreach (var h in harnesses) await h.DisposeAsync();
            try { System.IO.Directory.Delete(root, recursive: true); } catch (System.IO.IOException) { }
        }
    }
}
