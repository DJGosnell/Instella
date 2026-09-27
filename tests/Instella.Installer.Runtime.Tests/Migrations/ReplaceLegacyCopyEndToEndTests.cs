using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Installation;
using Instella.Core.Manifest;
using Instella.Core.Platform;
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Runtime.Migrations;
using Instella.Installer.Testing;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Migrations;

/// <summary>
/// The motivating case, end to end through the real installer on the harness fakes: ExampleApp
/// was installed without Instella in %LOCALAPPDATA%\ExampleApp, runs as a tray app, and starts
/// with Windows through its own Run value. Installing it with Instella replaces that copy;
/// uninstalling leaves nothing pointing at a deleted exe.
/// </summary>
[TestFixture]
[NonParallelizable]
public class ReplaceLegacyCopyEndToEndTests
{
    private sealed class ReplaceLegacyCopy : InstallMigration
    {
        public int Executions { get; private set; }

        public override string Id => "replace-pre-instella-copy";
        public override string DisplayName => "Removing the previous copy of ExampleApp";

        private MigrationFolder OldCopy => Folder(KnownFolder.LocalAppData, "ExampleApp");

        protected override Condition When() =>
            // No mode test: a repair or a later upgrade must be able to catch up (installer-only migrations).
            IsPerUserInstall()
            & FileExists(KnownFolder.LocalAppData, "ExampleApp/ExampleApp.exe")
            & !InstellaInstallationAt(KnownFolder.LocalAppData, "ExampleApp");

        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            Executions++;
            await StopProcessesInAsync(OldCopy, ct);
            await RepointRunValueAsync("ExampleApp", OldCopy, ct);
            await DeleteFilesAsync(OldCopy, ["ExampleApp.exe", "ExampleApp.pdb"], ct);
            await DeleteFolderIfEmptyAsync(OldCopy, ct);
        }
    }

    private ReplaceLegacyCopy _migration = null!;
    private InstellaTestHarness _harness = null!;
    private string _installPath = null!;
    private string _oldExe = null!;
    private string _userData = null!;

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    [SetUp]
    public void SetUp()
    {
        _migration = new ReplaceLegacyCopy();
        var installer = InstellaInstaller.Create()
            .WithApp("ExampleApp", "com.example.app", new Version(1, 0))
            .WithExecutableName("ExampleApp.exe")
            .WithElevation(ElevationMode.PerUser)
            .AddMigration(_migration)
            .WithAppManagedAutoStart("ExampleApp")
            .Build();
        _harness = InstellaTestHarness.Create()
            .WithInstaller(installer)
            .WithPayload(new Dictionary<string, string> { ["ExampleApp.exe"] = "instella-installed exe" })
            .Build();

        var local = _harness.KnownFolderPath(KnownFolder.LocalAppData);
        _installPath = Path.Combine(local, "Programs", "ExampleApp");
        _oldExe = Path.Combine(local, "ExampleApp", "ExampleApp.exe");
        _userData = Path.Combine(_harness.KnownFolderPath(KnownFolder.RoamingAppData), "ExampleApp", "settings.json");

        // The old copy: exe + pdb only, running, started at sign-in by its own Run value; user data in Roaming.
        var fs = (InMemoryFileSystem)_harness.FileSystem;
        fs.AddFile(_oldExe, Encoding.UTF8.GetBytes("old exe"));
        fs.AddFile(Path.Combine(local, "ExampleApp", "ExampleApp.pdb"), Encoding.UTF8.GetBytes("old pdb"));
        fs.AddFile(_userData, Encoding.UTF8.GetBytes("{\"theme\":\"dark\"}"));
        _harness.Registry.Set(RegistryHive.CurrentUser, RunKey, "ExampleApp", InstellaRegistryValueKind.String, $"\"{_oldExe}\" --tray");
        _harness.StartProcess(_oldExe);
    }

    [TearDown]
    public async Task TearDown() => await _harness.DisposeAsync();

    private Task<int> Install(params string[] extra) =>
        _harness.RunFullWithArgsAsync(["--install", "--silent", "--path", _installPath, .. extra]);

    private Task<int> Uninstall() =>
        _harness.RunFullWithArgsAsync(["--uninstall", "--silent", "--path", _installPath]);

    private string? RunValue() => _harness.Registry.Get(RegistryHive.CurrentUser, RunKey, "ExampleApp") as string;

    private Task<InstalledManifest?> Manifest() =>
        new InstallManifestWriter(_harness.FileSystem).ReadAsync(_installPath, CancellationToken.None);

    [Test]
    public async Task Install_ReplacesTheOldCopy_Repair_SkipsIt_Uninstall_RemovesTheRunValue()
    {
        Assert.That(await Install("--force-close"), Is.EqualTo(0), string.Join("\n", _harness.ShownMessages));

        Assert.That(_harness.IsProcessRunning(_oldExe), Is.False, "the old tray app was closed");
        Assert.That(_harness.FileSystem.Exists(_oldExe), Is.False);
        Assert.That(_harness.FileSystem.DirectoryExists(Path.GetDirectoryName(_oldExe)!), Is.False, "the empty old folder is gone");
        Assert.That(_harness.FileSystem.Exists(_userData), Is.True, "user data is never touched");
        Assert.That(_harness.FileSystem.Exists(Path.Combine(_installPath, "ExampleApp.exe")), Is.True);
        Assert.That(RunValue(), Is.EqualTo($"\"{Path.Combine(_installPath, "ExampleApp.exe")}\" --tray"),
            "sign-in now starts the Instella copy, with the same arguments");
        var manifest = (await Manifest())!;
        Assert.That(manifest.CompletedMigrations, Is.EqualTo(new[] { "replace-pre-instella-copy" }));
        Assert.That(manifest.AdoptedItems, Is.EqualTo(new[] { new ManifestAdoptedItem("run-value", "ExampleApp", true, "app-managed") }));
        Assert.That(_migration.Executions, Is.EqualTo(1));

        // A repair (same version) never runs a completed migration again.
        Assert.That(await Install(), Is.EqualTo(0));
        Assert.That(_migration.Executions, Is.EqualTo(1));

        Assert.That(await Uninstall(), Is.EqualTo(0), string.Join("\n", _harness.ShownMessages));
        Assert.That(RunValue(), Is.Null, "the app-managed Run value pointed into the installation, so it went with it");
        Assert.That(_harness.FileSystem.Exists(Path.Combine(_installPath, "ExampleApp.exe")), Is.False);
        Assert.That(_harness.FileSystem.Exists(_userData), Is.True);
    }

    [Test]
    public async Task ASilentInstallWithoutForceClose_KeepsTheInstall_AndCatchesUpLater()
    {
        Assert.That(await Install(), Is.EqualTo(0), "an AfterCommit failure never fails the install");
        Assert.That(_harness.IsProcessRunning(_oldExe), Is.True);
        Assert.That(_harness.FileSystem.Exists(_oldExe), Is.True);
        Assert.That(RunValue(), Does.Contain(_oldExe), "nothing was changed");
        Assert.That((await Manifest())!.CompletedMigrations, Is.Null, "not recorded, so it runs again");

        // The next installer run (here a repair with --force-close) catches up.
        Assert.That(await Install("--force-close"), Is.EqualTo(0));
        Assert.That(_harness.FileSystem.Exists(_oldExe), Is.False);
        Assert.That((await Manifest())!.CompletedMigrations, Is.EqualTo(new[] { "replace-pre-instella-copy" }));
    }

    [Test]
    public async Task AValueTheUserPointedElsewhere_SurvivesUninstall()
    {
        Assert.That(await Install("--force-close"), Is.EqualTo(0));
        const string elsewhere = "\"C:\\PortableApps\\ExampleApp\\ExampleApp.exe\"";
        _harness.Registry.Set(RegistryHive.CurrentUser, RunKey, "ExampleApp", InstellaRegistryValueKind.String, elsewhere);

        Assert.That(await Uninstall(), Is.EqualTo(0));
        Assert.That(RunValue(), Is.EqualTo(elsewhere));
    }
}
