using Instella.Core.Installation;
using Instella.Core.Platform;
using Instella.Installer.Runtime.Migrations;
using NUnit.Framework;

namespace Instella.Installer.Testing.Tests;

/// <summary>The public <see cref="MigrationHarness"/>, driven the way an app's tests would use it.</summary>
[TestFixture]
public class MigrationHarnessTests
{
    /// <summary>The anonymized case from the docs: a copy of ExampleApp installed without Instella.</summary>
    private sealed class ReplaceLegacyCopy : InstallMigration
    {
        public override string Id => "replace-pre-instella-copy";
        public override string DisplayName => "Removing the previous copy of ExampleApp";

        private MigrationFolder OldCopy => Folder(KnownFolder.LocalAppData, "ExampleApp");

        protected override Condition When() =>
            IsFirstInstallOrUpgrade()
            & FileExists(KnownFolder.LocalAppData, "ExampleApp/ExampleApp.exe")
            & !InstellaInstallationAt(KnownFolder.LocalAppData, "ExampleApp");

        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            await StopProcessesInAsync(OldCopy, ct);
            await RepointRunValueAsync("ExampleApp", OldCopy, ct);
            await DeleteFilesAsync(OldCopy, ["ExampleApp.exe", "ExampleApp.pdb"], ct);
            await DeleteFolderIfEmptyAsync(OldCopy, ct);
        }
    }

    private sealed class Delegated(string id, MigrationTiming timing, Func<Delegated, Condition> when, Func<Delegated, CancellationToken, Task> body)
        : InstallMigration
    {
        public override string Id => id;
        public override MigrationTiming Timing => timing;
        public override bool RunOnce => timing != MigrationTiming.Uninstall;
        public int Rollbacks { get; private set; }
        protected override Condition When() => when(this);
        protected override Task ExecuteAsync(CancellationToken ct) => body(this, ct);
        protected override Task RollbackAsync(CancellationToken ct) { Rollbacks++; return Task.CompletedTask; }
        public MigrationContext Ctx() => Context;
        public MigrationFolder F(KnownFolder root, string relative) => Folder(root, relative);
        public Condition Upgrading(string range) => UpgradingFrom(range);
        public Condition Uninstalling() => IsUninstall();
        public Condition Registry(RegistryHive hive, string key, string name) => RegistryValueExists(hive, key, name);
        public Task<int> Delete(MigrationFolder folder, params string[] names) => DeleteFilesAsync(folder, names, CancellationToken.None);
        public Task<int> Run(string exe) => RunProgramAsync(exe, ["/S"], [0], CancellationToken.None);
        public Task Stop(MigrationFolder folder) => StopProcessesInAsync(folder, CancellationToken.None);
        public Task<bool> Adopt(string name) => AdoptRunValueAsync(name, CancellationToken.None);
        public Task<bool> DeleteRun(string name, MigrationFolder folder) => DeleteRunValueAsync(name, folder, CancellationToken.None);
    }

    private static MigrationHarness OldCopyScenario()
    {
        var harness = MigrationHarness.For<ReplaceLegacyCopy>()
            .WithFile(KnownFolder.LocalAppData, "ExampleApp/ExampleApp.exe", "old exe")
            .WithFile(KnownFolder.LocalAppData, "ExampleApp/ExampleApp.pdb", "old pdb")
            .WithFile(KnownFolder.RoamingAppData, "ExampleApp/settings.json", "{}")
            .WithRunningProcess(KnownFolder.LocalAppData, "ExampleApp/ExampleApp.exe");
        return harness.WithRunValue("ExampleApp", $"\"{harness.PathOf(KnownFolder.LocalAppData, "ExampleApp/ExampleApp.exe")}\"");
    }

    [Test]
    public async Task TheOldCopy_IsReplaced()
    {
        var result = await OldCopyScenario().RunAsync();

        Assert.That(result.Outcome, Is.EqualTo(MigrationOutcome.Completed), result.Error ?? result.SkipReason);
        Assert.That(result.Ran, Is.True);
        Assert.That(result.RecordedAsCompleted, Is.True);
        Assert.That(result.StoppedProcesses.Single(), Does.Contain("ExampleApp.exe"));
        Assert.That(result.DeletedFiles.Select(Path.GetFileName), Is.EqualTo(new[] { "ExampleApp.exe", "ExampleApp.pdb" }));
        Assert.That(result.FolderExists(KnownFolder.LocalAppData, "ExampleApp"), Is.False);
        Assert.That(result.FileExists(KnownFolder.RoamingAppData, "ExampleApp/settings.json"), Is.True, "user data is never touched");
        Assert.That(result.RunValue("ExampleApp"), Is.EqualTo($"\"{result.PathOf(KnownFolder.InstallFolder, "ExampleApp.exe")}\""));
        Assert.That(result.RegistryChanges.Single().Name, Is.EqualTo("ExampleApp"));
        Assert.That(result.LogLines, Has.Some.Contains("migration[replace-pre-instella-copy]: completed"));
    }

    [Test]
    public async Task WithoutTheOldCopy_ItIsSkipped_WithTheReason()
    {
        var result = await MigrationHarness.For<ReplaceLegacyCopy>().RunAsync();
        Assert.That(result.Outcome, Is.EqualTo(MigrationOutcome.Skipped));
        Assert.That(result.Ran, Is.False);
        Assert.That(result.SkipReason, Is.EqualTo("FileExists(LocalAppData/ExampleApp/ExampleApp.exe) is false"));
        Assert.That(result.RecordedAsCompleted, Is.False);
    }

    [Test]
    public async Task AnOldCopyThatIsAnInstellaInstallation_IsLeftAlone()
    {
        var result = await OldCopyScenario().WithInstellaInstallation(KnownFolder.LocalAppData, "ExampleApp").RunAsync();
        Assert.That(result.Outcome, Is.EqualTo(MigrationOutcome.Skipped));
        Assert.That(result.SkipReason, Does.Contain("InstellaInstallationAt"));
        Assert.That(result.FileExists(KnownFolder.LocalAppData, "ExampleApp/ExampleApp.exe"), Is.True);
    }

    [Test]
    public async Task AlreadyCompleted_IsSkipped()
    {
        var result = await OldCopyScenario().Mode(InstallerMode.Repair).AlreadyCompleted().RunAsync();
        Assert.That(result.Outcome, Is.EqualTo(MigrationOutcome.AlreadyCompleted));
        Assert.That(result.FileExists(KnownFolder.LocalAppData, "ExampleApp/ExampleApp.exe"), Is.True);
    }

    [Test]
    public async Task Preview_PlansEverything_AndChangesNothing()
    {
        var result = await OldCopyScenario().Preview().RunAsync();
        Assert.That(result.Outcome, Is.EqualTo(MigrationOutcome.Completed));
        Assert.That(result.PlannedActions, Has.Count.EqualTo(5));
        Assert.That(result.PlannedActions, Has.All.StartWith("would "));
        Assert.That(result.DeletedFiles, Is.Empty);
        Assert.That(result.StoppedProcesses, Is.Empty);
        Assert.That(result.RegistryChanges, Is.Empty);
        Assert.That(result.FileExists(KnownFolder.LocalAppData, "ExampleApp/ExampleApp.exe"), Is.True);
        Assert.That(result.RecordedAsCompleted, Is.False);
    }

    [Test]
    public async Task AProgramThatWillNotClose_FailsTheMigration()
    {
        var harness = MigrationHarness.For<ReplaceLegacyCopy>()
            .WithRunningProcess(KnownFolder.LocalAppData, "ExampleApp/ExampleApp.exe", closes: false);
        var result = await harness.RunAsync();
        Assert.That(result.Outcome, Is.EqualTo(MigrationOutcome.Failed));
        Assert.That(result.Error, Does.Contain("still running"));
        Assert.That(result.FileExists(KnownFolder.LocalAppData, "ExampleApp/ExampleApp.exe"), Is.True);
        Assert.That(result.RecordedAsCompleted, Is.False);
    }

    [Test]
    public async Task WithoutForceClose_ASilentInstallClosesNothing()
    {
        var result = await OldCopyScenario().ForceClose(false).RunAsync();
        Assert.That(result.Outcome, Is.EqualTo(MigrationOutcome.Failed));
        Assert.That(result.Error, Does.Contain("--force-close"));
    }

    [Test]
    public async Task Elevated_MeansAMachineInstall_WherePerUserFoldersAreOffLimits()
    {
        var result = await OldCopyScenario().Elevated().RunAsync();
        Assert.That(result.Outcome, Is.EqualTo(MigrationOutcome.Skipped));
        Assert.That(result.SkipReason, Does.Contain("per-user folder"));
    }

    [Test]
    public async Task FailLaterStep_RollsABeforeCommitMigrationBack()
    {
        Delegated? m = null;
        m = new Delegated("before", MigrationTiming.BeforeCommit, _ => Condition.Always,
            (t, _) => t.Delete(t.F(KnownFolder.ProgramData, "OldVendor"), "old.dll"));
        var result = await MigrationHarness.For(m).WithFile(KnownFolder.ProgramData, "OldVendor/old.dll").FailLaterStep().RunAsync();
        Assert.That(result.Outcome, Is.EqualTo(MigrationOutcome.RolledBack));
        Assert.That(result.Error, Is.EqualTo("a later step failed"));
        Assert.That(result.FileExists(KnownFolder.ProgramData, "OldVendor/old.dll"), Is.True, "restored");
        Assert.That(m.Rollbacks, Is.EqualTo(1));
        Assert.That(result.RecordedAsCompleted, Is.False);
    }

    [Test]
    public async Task ABeforeCommitFailure_IsRolledBack()
    {
        var m = new Delegated("before", MigrationTiming.BeforeCommit, _ => Condition.Always, async (t, _) =>
        {
            await t.Delete(t.F(KnownFolder.ProgramData, "OldVendor"), "old.dll");
            throw new IOException("nope");
        });
        var result = await MigrationHarness.For(m).WithFile(KnownFolder.ProgramData, "OldVendor/old.dll").RunAsync();
        Assert.That(result.Outcome, Is.EqualTo(MigrationOutcome.RolledBack));
        Assert.That(result.Error, Is.EqualTo("nope"));
        Assert.That(result.FileExists(KnownFolder.ProgramData, "OldVendor/old.dll"), Is.True);
    }

    [Test]
    public async Task PreviousVersion_MakesAnUpgrade()
    {
        var m = new Delegated("settings-v1", MigrationTiming.AfterCommit, t => t.Upgrading("<2.0"), (_, _) => Task.CompletedTask);
        Assert.That((await MigrationHarness.For(m).PreviousVersion("1.4").RunAsync()).Outcome, Is.EqualTo(MigrationOutcome.Completed));
        Assert.That((await MigrationHarness.For(m).PreviousVersion("2.0").RunAsync()).Outcome, Is.EqualTo(MigrationOutcome.Skipped));
        Assert.That((await MigrationHarness.For(m).RunAsync()).SkipReason, Does.Contain("not an upgrade"));
    }

    [Test]
    public async Task AnUninstallMigration_RunsInUninstallMode()
    {
        var m = new Delegated("goodbye", MigrationTiming.Uninstall, t => t.Uninstalling(),
            async (t, _) => await t.DeleteRun("Helper", t.F(KnownFolder.LocalAppData, "ExampleAppHelper")));
        var harness = MigrationHarness.For(m).WithFile(KnownFolder.LocalAppData, "ExampleAppHelper/helper.exe");
        harness.WithRunValue("Helper", $"\"{harness.PathOf(KnownFolder.LocalAppData, "ExampleAppHelper/helper.exe")}\"");
        var result = await harness.RunAsync();
        Assert.That(result.Outcome, Is.EqualTo(MigrationOutcome.Completed));
        Assert.That(result.RunValue("Helper"), Is.Null);
        Assert.That(result.RecordedAsCompleted, Is.False, "uninstall migrations are not run-once");
        Assert.That(result.RegistryChanges.Single().After, Is.Null);
    }

    [Test]
    public async Task Programs_AreRecorded_AndTheirExitCodeChecked()
    {
        var harness = MigrationHarness.For(new Delegated("old-uninstaller", MigrationTiming.AfterCommit, _ => Condition.Always,
            async (t, _) => await t.Run(Path.Combine(t.Ctx().GetFolderPath(KnownFolder.ProgramFiles)!, "OldVendor", "uninstall.exe"))));
        harness.WithFile(KnownFolder.ProgramFiles, "OldVendor/uninstall.exe");
        var ok = await harness.RunAsync();
        Assert.That(ok.Outcome, Is.EqualTo(MigrationOutcome.Completed));
        Assert.That(ok.ProgramsRun.Single(), Does.EndWith("uninstall.exe /S"));

        var failed = await harness.ProgramExitCode(5).RunAsync();
        Assert.That(failed.Outcome, Is.EqualTo(MigrationOutcome.Failed));
        Assert.That(failed.Error, Does.Contain("exited with 5"));
    }

    [Test]
    public async Task RegistryValues_CanBeSeeded_AndAdopted()
    {
        var m = new Delegated("adopt", MigrationTiming.AfterCommit,
            t => t.Registry(RegistryHive.CurrentUser, @"Software\ExampleApp", "Theme"),
            async (t, _) => await t.Adopt("ExampleApp"));
        var result = await MigrationHarness.For(m)
            .WithRegistryValue(RegistryHive.CurrentUser, @"Software\ExampleApp", "Theme", "dark")
            .WithRunValue("ExampleApp", "\"C:\\x\\ExampleApp.exe\"")
            .RunAsync();
        Assert.That(result.Outcome, Is.EqualTo(MigrationOutcome.Completed));
        Assert.That(result.AdoptedItems.Single(), Is.EqualTo(new ManifestAdoptedItem("run-value", "ExampleApp", true, "adopt")));
        Assert.That(result.RegistryChanges, Is.Empty, "adoption writes nothing");
    }

    [Test]
    public async Task WithFolder_AndWithApp()
    {
        var m = new Delegated("folder", MigrationTiming.AfterCommit, _ => Condition.Always, (_, _) => Task.CompletedTask);
        var harness = MigrationHarness.For(m).WithApp("Other", "com.other", "Other.exe").WithFolder(KnownFolder.ProgramData, "Other/cache");
        var result = await harness.RunAsync();
        Assert.That(result.FolderExists(KnownFolder.ProgramData, "Other/cache"), Is.True);
        Assert.That(result.PathOf(KnownFolder.InstallFolder, ""), Does.EndWith(Path.Combine("Programs", "Other")));
    }

    [Test]
    public void AMigrationBuildWouldRefuse_IsRefusedHereToo()
    {
        var bad = new Delegated("Not Valid", MigrationTiming.AfterCommit, _ => Condition.Always, (_, _) => Task.CompletedTask);
        Assert.ThrowsAsync<InvalidOperationException>(() => MigrationHarness.For(bad).RunAsync());
    }

    [Test]
    public void For_RefusesNull()
    {
        Assert.Throws<ArgumentNullException>(() => MigrationHarness.For(null!));
    }
}

