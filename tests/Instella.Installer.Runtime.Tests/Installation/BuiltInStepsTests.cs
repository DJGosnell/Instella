using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Instella.Core.Installation;
using Instella.Core.Manifest;
using Instella.Core.Platform;
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Runtime.Installation;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Installation;

/// <summary>
/// <see cref="BuiltInSteps.For"/>: the built-ins a configuration adds (registry writes, Windows
/// services) run in their stage, before <c>write-manifest</c> and the commit, whether or not the
/// installer has custom steps. Without custom steps <see cref="StepOrdering"/> keeps the list as it
/// is, so the position in it is the position the step runs at.
/// </summary>
[TestFixture, Platform("Win")]
public sealed class BuiltInStepsTests
{
    private static FrozenConfig Config(bool userStep)
    {
        var builder = InstellaInstaller.Create()
            .WithApp("App", "com.example.app", new Version(1, 0))
            .WithExecutableName("App.exe")
            .WithElevation(ElevationMode.SystemWide)
            .OnWindows(w => w
                .AddRegistryKey(RegistryHive.LocalMachine, @"Software\App", k => k.SetString("Dir", "x"))
                .AddService("App.Svc", _ => { }));
        if (userStep)
            builder.AddStep("user-register", sb => sb
                .InStage(InstallStage.Register)
                .Execute((_, _, _) => Task.FromResult(StepResult.Ok))
                .NoRollbackNeeded("test"));
        return ((InstellaInstallerImpl)builder.Build()).ConfigForTests;
    }

    private static List<string> Ordered(FrozenConfig config) =>
        StepOrdering.BuildOrderedSteps(BuiltInSteps.For(config, existing: null), config.UserSteps, config.MigrationsOrEmpty)
            .Select(s => s.Name).ToList();

    [TestCase(false)]
    [TestCase(true)]
    public void RegistryWritesAndServiceRegistration_RunBeforeTheManifestAndTheCommit(bool userStep)
    {
        var names = Ordered(Config(userStep));
        int At(string name) => names.IndexOf(name);

        Assert.That(At("write-registry-specs"), Is.GreaterThan(At("register-uninstall-entry")));
        Assert.That(At("write-registry-specs"), Is.LessThan(At("write-manifest")));
        Assert.That(At("register-services"), Is.GreaterThan(At("write-registry-specs")));
        Assert.That(At("register-services"), Is.LessThan(At("write-manifest")));
        Assert.That(At("remove-dropped-services"), Is.EqualTo(At("register-services") + 1));
        Assert.That(At("stop-services"), Is.EqualTo(At("commit-transaction") - 1));
        Assert.That(At("start-services"), Is.EqualTo(At("app-upgrade") + 1));
    }

    [Test]
    public void TheUnconditionalBuiltIns_KeepTheirOrder()
    {
        var plain = ((InstellaInstallerImpl)InstellaInstaller.Create()
            .WithApp("App", "com.example.app", new Version(1, 0)).Build()).ConfigForTests;

        Assert.That(BuiltInSteps.For(plain, existing: null).Select(s => s.Name),
            Is.EqualTo(OfflineInstallRunner.BuildDefaultSteps().Select(s => s.Name)));
    }

    [Test]
    public void AnInstallationThatRecordsServices_GetsTheServiceSteps_EvenWhenNoneAreDeclared()
    {
        var plain = ((InstellaInstallerImpl)InstellaInstaller.Create()
            .WithApp("App", "com.example.app", new Version(2, 0)).Build()).ConfigForTests;
        var existing = new InstalledManifest
        {
            AppName = "App",
            AppId = "com.example.app",
            Version = new Version(1, 0, 0),
            InstallDirectory = @"C:\App",
            ExecutableName = "App.exe",
            InstalledAt = DateTime.UtcNow,
            Files = [],
            Services = [new ManifestService("App.Old", "App.exe", null, ManifestService.Automatic, true)],
        };

        Assert.That(BuiltInSteps.For(plain, existing).Select(s => s.Name),
            Does.Contain("remove-dropped-services").And.Contain("stop-services"));
    }
}
