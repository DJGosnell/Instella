using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Installation;
using Instella.Core.Platform;
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Runtime.Core;
using Instella.Installer.Runtime.Migrations;
using Instella.Installer.Runtime.Runners;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Migrations;

/// <summary>Uninstall migrations, adopted items and app-managed Run values during uninstall.</summary>
[TestFixture]
public class MigrationUninstallTests
{
    private MigrationTestBed _bed = null!;

    [SetUp]
    public void SetUp() => _bed = new MigrationTestBed();

    private string InstalledExe => Path.Combine(_bed.InstallPath, "ExampleApp.exe");

    private static InstallerBuilder App() =>
        InstellaInstaller.Create().WithApp("ExampleApp", "com.example.app", new Version(2, 0)).WithExecutableName("ExampleApp.exe");

    private async Task SeedInstallationAsync(IReadOnlyList<ManifestAdoptedItem>? adopted = null)
    {
        _bed.FileSystem.AddFile(InstalledExe, [1, 2, 3]);
        await new InstallManifestWriter(_bed.FileSystem).WriteAsync(_bed.InstallPath, new InstalledManifest
        {
            AppName = "ExampleApp", AppId = "com.example.app", Version = new Version(2, 0, 0), InstallDirectory = _bed.InstallPath,
            ExecutableName = "ExampleApp.exe", InstalledAt = DateTime.UtcNow, InstalledPerUser = _bed.PerUser, HasAutoStart = false,
            Files = [new InstalledFile("ExampleApp.exe", new string('0', 64), 3)],
            AdoptedItems = adopted,
        }, CancellationToken.None);
    }

    private async Task<InstellaExitCode> UninstallAsync(InstallerBuilder builder)
    {
        var config = ((InstellaInstallerImpl)builder.Build()).ConfigForTests;
        var runner = new UninstallModeRunner(config, _bed.Log, _bed.Platform, _bed.FileSystem)
        {
            MigrationRuntime = _bed.Runtime(),
            ProcessFinder = _bed.Processes,
            CleanupLauncher = (_, _) => Assert.Fail("no cleanup expected"),
        };
        return await runner.RunAsync(
            new DispatchResult(DispatchKind.Mode, InstallerMode.Uninstall, _bed.InstallPath, IsSilent: true), CancellationToken.None);
    }

    [Test]
    public async Task UninstallMigrations_RunFirst_ThenHooks_ThenAdoptedItems_ThenInstellasCleanup()
    {
        await SeedInstallationAsync([new ManifestAdoptedItem("run-value", "ExampleApp", true, "replace-old-copy")]);
        _bed.SetRunValue("ExampleApp", $"\"{InstalledExe}\"");
        var order = new List<string>();

        var migration = new TestMigration("say-goodbye", MigrationTiming.Uninstall)
        {
            RunOnceValue = false,
            WhenFactory = t => t.IsUninstall(),
            Body = (t, _) =>
            {
                order.Add("migration");
                Assert.That(_bed.FileSystem.Exists(InstalledExe), Is.True, "the app's files are still there");
                Assert.That(_bed.RunValue("ExampleApp"), Is.Not.Null, "adopted items are still there");
                Assert.That(t.Ctx.Mode, Is.EqualTo(InstallerMode.Uninstall));
                Assert.That(t.Ctx.PreviousVersion, Is.EqualTo(new Version(2, 0, 0)));
                return Task.CompletedTask;
            },
        };
        var builder = App().AddMigration(migration)
            .AddStep("hooked", s => s.Execute((_, _, _) => Task.FromResult(StepResult.Ok)).NoRollbackNeeded("t")
                .OnUninstall((_, _) =>
                {
                    order.Add("hook");
                    Assert.That(_bed.RunValue("ExampleApp"), Is.Not.Null, "adopted items are removed after the hooks");
                    return Task.CompletedTask;
                }));

        var exit = await UninstallAsync(builder);

        Assert.That(exit, Is.EqualTo(InstellaExitCode.Success));
        Assert.That(order, Is.EqualTo(new[] { "migration", "hook" }));
        Assert.That(_bed.RunValue("ExampleApp"), Is.Null);
        Assert.That(_bed.FileSystem.Exists(InstalledExe), Is.False);
    }

    [Test]
    public async Task UninstallMigrations_RunInOrderThenId()
    {
        await SeedInstallationAsync();
        var order = new List<string>();
        TestMigration M(string id, int o) => new(id, MigrationTiming.Uninstall, o)
            { RunOnceValue = false, Body = (_, _) => { order.Add(id); return Task.CompletedTask; } };
        await UninstallAsync(App().AddMigration(M("b", 0)).AddMigration(M("late", 9)).AddMigration(M("a", 0)).AddMigration(M("first", -1)));
        Assert.That(order, Is.EqualTo(new[] { "first", "a", "b", "late" }));
    }

    [Test]
    public async Task AFailingUninstallMigration_DoesNotStopTheUninstall()
    {
        await SeedInstallationAsync();
        var failing = new TestMigration("fails", MigrationTiming.Uninstall)
            { RunOnceValue = false, Body = (_, _) => throw new InvalidOperationException("boom") };
        var exit = await UninstallAsync(App().AddMigration(failing));
        Assert.That(exit, Is.EqualTo(InstellaExitCode.Success));
        Assert.That(_bed.FileSystem.Exists(InstalledExe), Is.False);
        Assert.That(_bed.Log.Warnings, Has.Some.Contains("migration[fails]: failed: boom"));
    }

    [Test]
    public async Task AnUninstallMigrationWhoseConditionIsFalse_IsSkipped()
    {
        await SeedInstallationAsync();
        var m = new TestMigration("only-if", MigrationTiming.Uninstall)
            { RunOnceValue = false, WhenFactory = t => t.FileExists(KnownFolder.RoamingAppData, "ExampleApp/cache.db") };
        await UninstallAsync(App().AddMigration(m));
        Assert.That(m.Executions, Is.Zero);
    }

    [Test]
    public async Task InstallMigrations_DoNotRunOnUninstall()
    {
        await SeedInstallationAsync();
        var install = new TestMigration("install-time");
        await UninstallAsync(App().AddMigration(install));
        Assert.That(install.Executions, Is.Zero);
    }

    [Test]
    public async Task AnAdoptedValuePointingIntoTheInstallation_IsRemoved_OneElsewhereIsKept()
    {
        await SeedInstallationAsync([
            new ManifestAdoptedItem("run-value", "Mine", true, "m"),
            new ManifestAdoptedItem("run-value", "Elsewhere", true, "m"),
            new ManifestAdoptedItem("run-value", "Missing", true, "m"),
            new ManifestAdoptedItem("future-kind", "X", true, "m"),
        ]);
        _bed.SetRunValue("Mine", $"\"{InstalledExe}\" --tray");
        _bed.SetRunValue("Elsewhere", "\"C:\\Other\\ExampleApp.exe\"");

        var exit = await UninstallAsync(App());

        Assert.That(exit, Is.EqualTo(InstellaExitCode.Success));
        Assert.That(_bed.RunValue("Mine"), Is.Null);
        Assert.That(_bed.RunValue("Elsewhere"), Is.Not.Null);
        Assert.That(_bed.Log.Lines, Has.Some.Contains("Run value 'Elsewhere' left in place"));
        Assert.That(_bed.Log.Lines, Has.Some.Contains("kind 'future-kind'"));
    }

    [Test]
    public async Task AppManagedAutoStart_IsRemoved_EvenWithoutAManifestRecord()
    {
        await SeedInstallationAsync();
        _bed.SetRunValue("ExampleApp", $"\"{InstalledExe}\"");
        await UninstallAsync(App().WithAppManagedAutoStart("ExampleApp"));
        Assert.That(_bed.RunValue("ExampleApp"), Is.Null);
    }

    [Test]
    public async Task AppManagedAutoStart_PointingAtAnotherCopy_IsKept()
    {
        await SeedInstallationAsync();
        var other = _bed.PathOf(KnownFolder.LocalAppData, "ExampleApp/ExampleApp.exe");
        _bed.SetRunValue("ExampleApp", $"\"{other}\"");
        await UninstallAsync(App().WithAppManagedAutoStart("ExampleApp"));
        Assert.That(_bed.RunValue("ExampleApp"), Is.EqualTo($"\"{other}\""));
    }

    [Test]
    public async Task AnItemAdoptedByADroppedMigration_IsStillRemoved()
    {
        await SeedInstallationAsync([new ManifestAdoptedItem("run-value", "ExampleApp", true, "migration-removed-in-3")]);
        _bed.SetRunValue("ExampleApp", $"\"{InstalledExe}\"");
        await UninstallAsync(App());
        Assert.That(_bed.RunValue("ExampleApp"), Is.Null);
    }

    [Test]
    public async Task AMachineInstallsAdoptedValue_IsRemovedFromHklm()
    {
        _bed.Scope = InstallationScope.SystemWide;
        await SeedInstallationAsync([new ManifestAdoptedItem("run-value", "ExampleApp", false, "m")]);
        _bed.Platform.Registry.Set(RegistryHive.LocalMachine, RunCommand.RunKey, "ExampleApp", InstellaRegistryValueKind.String, $"\"{InstalledExe}\"");
        _bed.Platform.Registry.Set(RegistryHive.CurrentUser, RunCommand.RunKey, "ExampleApp", InstellaRegistryValueKind.String, $"\"{InstalledExe}\"");
        await UninstallAsync(App());
        Assert.That(_bed.Platform.Registry.Get(RegistryHive.LocalMachine, RunCommand.RunKey, "ExampleApp"), Is.Null);
        Assert.That(_bed.Platform.Registry.Get(RegistryHive.CurrentUser, RunCommand.RunKey, "ExampleApp"), Is.Not.Null, "HKCU was not adopted");
    }

    [Test]
    public void ThePreviewUninstallList_StartsWithTheUninstallMigrations()
    {
        var config = ((InstellaInstallerImpl)App()
            .AddMigration(new TestMigration("goodbye", MigrationTiming.Uninstall) { RunOnceValue = false })
            .AddMigration(new TestMigration("install-time"))
            .Build()).ConfigForTests;
        var names = PreviewStepLists.Build(config, InstallerMode.Uninstall).Select(s => s.Name).ToList();
        Assert.That(names[0], Is.EqualTo("migration:goodbye"));
        Assert.That(names, Does.Not.Contain("migration:install-time"));
    }
}
