using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Installation;
using Instella.Core.Manifest;
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Runtime.Core;
using Instella.Installer.Runtime.Installation;
using Instella.Installer.Runtime.Installation.BuiltIn;
using Instella.Installer.Runtime.Installation.Builders;
using Instella.Installer.Runtime.Migrations;
using Instella.Installer.Runtime.Runners;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Migrations;

/// <summary>Migrations inside the real install pipeline: position, run-once, failure rules, manifest.</summary>
[TestFixture]
public class MigrationPipelineTests
{
    private MigrationTestBed _bed = null!;

    [SetUp]
    public void SetUp() => _bed = new MigrationTestBed();

    private static InstallerBuilder App(string version = "2.0") =>
        InstellaInstaller.Create().WithApp("ExampleApp", "com.example.app", Version.Parse(version)).WithExecutableName("ExampleApp.exe");

    private static FrozenConfig Config(InstallerBuilder builder) => ((InstellaInstallerImpl)builder.Build()).ConfigForTests;

    private static IReadOnlyList<IInstallStepExecution> Steps(FrozenConfig config) =>
        StepOrdering.BuildOrderedSteps(OfflineInstallRunner.BuildDefaultSteps(), config.UserSteps, config.MigrationsOrEmpty);

    private static StepExecuteAsync Ok => (_, _, _) => Task.FromResult(StepResult.Ok);

    private static MemoryStream Payload()
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, text) in new Dictionary<string, string> { ["ExampleApp.exe"] = "new exe", ["lib/core.dll"] = "dll" })
            {
                using var s = zip.CreateEntry(path).Open();
                s.Write(Encoding.UTF8.GetBytes(text));
            }
        }
        ms.Position = 0;
        return ms;
    }

    /// <summary>Installs <paramref name="config"/> through the built-in pipeline on the bed's fakes.</summary>
    private async Task<(ExecutionResult Result, InstallContext Context)> InstallAsync(FrozenConfig config, CancellationToken ct = default)
    {
        var existing = await new InstallManifestWriter(_bed.FileSystem).ReadAsync(_bed.InstallPath, CancellationToken.None);
        var mode = existing is null ? InstallerMode.FirstInstall
            : existing.Version == config.AppVersion ? InstallerMode.Repair : InstallerMode.Upgrade;
        var options = new InstallOptions { InstallPath = _bed.InstallPath, Elevation = ElevationMode.PerUser };
        var context = InstallContextFactory.Create(config, mode, _bed.InstallPath, options, _bed.Platform, _bed.FileSystem, _bed.Log,
            payload: Payload(), existing: existing);
        context.Migrations = _bed.Runtime();
        var result = await new StepExecutor(Steps(config)).ExecuteAsync(context, null, ct);
        context.PayloadArchive?.Dispose();
        return (result, context);
    }

    private Task<InstalledManifest?> ManifestAsync() =>
        new InstallManifestWriter(_bed.FileSystem).ReadAsync(_bed.InstallPath, CancellationToken.None);

    // ---- Position --------------------------------------------------------------------------

    [Test]
    public void BeforeCommit_RunsJustBeforeWriteManifest_AfterCommit_RunsLast()
    {
        var config = Config(App()
            .AddStep("user-register", s => s.Execute(Ok).NoRollbackNeeded("t"))
            .AddStep("user-finalize", s => s.InStage(InstallStage.Finalize).Execute(Ok).NoRollbackNeeded("t"))
            .AddStep("user-before-manifest", s => s.InStage(InstallStage.Finalize).Before("write-manifest").Execute(Ok).NoRollbackNeeded("t"))
            .AddMigration(new TestMigration("after-b"))
            .AddMigration(new TestMigration("after-a"))
            .AddMigration(new TestMigration("before", MigrationTiming.BeforeCommit))
            .AddMigration(new TestMigration("uninstall-only", MigrationTiming.Uninstall) { RunOnceValue = false }));
        var names = Steps(config).Select(s => s.Name).ToList();

        int At(string name) => names.IndexOf(name);
        Assert.That(At("migration:before"), Is.EqualTo(At("write-manifest") - 1));
        Assert.That(At("migration:before"), Is.GreaterThan(At("user-register")));
        Assert.That(At("migration:before"), Is.GreaterThan(At("user-before-manifest")));
        Assert.That(At("migration:before"), Is.GreaterThan(At("register-uninstall-entry")));
        Assert.That(names.TakeLast(2), Is.EqualTo(new[] { "migration:after-a", "migration:after-b" }), "sorted by id, after everything");
        Assert.That(At("user-finalize"), Is.GreaterThan(At(CommitTransactionStep.StepName)));
        Assert.That(names, Does.Not.Contain("migration:uninstall-only"));
    }

    [Test]
    public void WithoutMigrations_TheStepListIsUnchanged()
    {
        var config = Config(App());
        Assert.That(Steps(config).Select(s => s.Name), Is.EqualTo(StepOrdering.BuildOrderedSteps(OfflineInstallRunner.BuildDefaultSteps(), config.UserSteps).Select(s => s.Name)));
    }

    [Test]
    public void TheProgressPage_ShowsTheDisplayName()
    {
        var config = Config(App().AddMigration(new TestMigration("replace-old-copy") { DisplayNameValue = "Removing the previous copy" }));
        Assert.That(StepDisplayNames.Map(Steps(config))["migration:replace-old-copy"], Is.EqualTo("Removing the previous copy"));
    }

    [Test]
    public void PreviewLists_IncludeMigrations()
    {
        var config = Config(App().AddMigration(new TestMigration("m")));
        Assert.That(PreviewStepLists.Build(config, InstallerMode.FirstInstall).Select(s => s.Name), Has.Member("migration:m"));
        Assert.That(new SimulatedStepExecutor(PreviewStepLists.Build(config, InstallerMode.Upgrade), PreviewSpeed.Fast, "migration:m"), Is.Not.Null,
            "--preview-fail can name a migration step");
    }

    // ---- Run-once and the manifest ---------------------------------------------------------

    [Test]
    public async Task CompletedMigrations_ReachTheInstalledManifest()
    {
        var before = new TestMigration("before", MigrationTiming.BeforeCommit);
        var after = new TestMigration("after");
        var (result, _) = await InstallAsync(Config(App().AddMigration(before).AddMigration(after)
            .AddMigration(new TestMigration("not-applicable") { WhenFactory = t => t.IsUpgrade() })
            .AddMigration(new TestMigration("every-time") { RunOnceValue = false })));

        Assert.That(result.Success, Is.True, result.Error);
        Assert.That((await ManifestAsync())!.CompletedMigrations, Is.EqualTo(new[] { "after", "before" }));
        Assert.That(_bed.Log.Lines, Has.Some.Contains("migrations: 3 completed (before, after, every-time), 1 skipped (not-applicable)"));
    }

    [Test]
    public async Task ACompletedMigration_NeverRunsAgain_AcrossRepairAndUpgrade()
    {
        var m = new TestMigration("once");
        await InstallAsync(Config(App().AddMigration(m)));
        Assert.That(m.Executions, Is.EqualTo(1));

        var (repair, ctx) = await InstallAsync(Config(App().AddMigration(m)));
        Assert.That(repair.Success, Is.True, repair.Error);
        Assert.That(ctx.Mode, Is.EqualTo(InstallerMode.Repair));
        Assert.That(m.Executions, Is.EqualTo(1), "a repair keeps the record");

        var added = new TestMigration("added-in-3");
        var (upgrade, _) = await InstallAsync(Config(App("3.0").AddMigration(m).AddMigration(added)));
        Assert.That(upgrade.Success, Is.True, upgrade.Error);
        Assert.That(m.Executions, Is.EqualTo(1));
        Assert.That(added.Executions, Is.EqualTo(1));
        Assert.That((await ManifestAsync())!.CompletedMigrations, Is.EqualTo(new[] { "added-in-3", "once" }));
    }

    [Test]
    public async Task AnUpgradeWithoutTheMigration_KeepsItsRecord()
    {
        await InstallAsync(Config(App().AddMigration(new TestMigration("dropped-later"))));
        await InstallAsync(Config(App("3.0")));
        Assert.That((await ManifestAsync())!.CompletedMigrations, Is.EqualTo(new[] { "dropped-later" }));
    }

    [Test]
    public async Task AMigrationSkippedByItsCondition_RunsOnALaterInstall()
    {
        var m = new TestMigration("later") { WhenFactory = t => t.FileExists(KnownFolder.LocalAppData, "ExampleApp/ExampleApp.exe") };
        await InstallAsync(Config(App().AddMigration(m)));
        Assert.That((await ManifestAsync())!.CompletedMigrations, Is.Null);

        _bed.AddFile(KnownFolder.LocalAppData, "ExampleApp/ExampleApp.exe");
        await InstallAsync(Config(App().AddMigration(m)));
        Assert.That(m.Executions, Is.EqualTo(1));
        Assert.That((await ManifestAsync())!.CompletedMigrations, Is.EqualTo(new[] { "later" }));
    }

    [Test]
    public async Task AppManagedAutoStart_AndAdoptions_ReachTheManifest()
    {
        _bed.SetRunValue("Adopted", "\"C:\\x\\a.exe\"");
        var config = Config(App().WithAppManagedAutoStart("ExampleApp")
            .AddMigration(new TestMigration("adopter") { Body = (t, _) => t.AdoptRunValueAsync("Adopted", default) }));
        await InstallAsync(config);
        Assert.That((await ManifestAsync())!.AdoptedItems, Is.EqualTo(new[]
        {
            new ManifestAdoptedItem("run-value", "ExampleApp", true, "app-managed"),
            new ManifestAdoptedItem("run-value", "Adopted", true, "adopter"),
        }));

        // A later version without either keeps them for uninstall.
        await InstallAsync(Config(App("3.0")));
        Assert.That((await ManifestAsync())!.AdoptedItems, Has.Count.EqualTo(2));
    }

    // ---- Failure rules ---------------------------------------------------------------------

    [Test]
    public async Task AnAfterCommitFailure_IsAWarning_AndTheInstallIsKept()
    {
        var failing = new TestMigration("fails") { Body = (_, _) => throw new IOException("disk said no") };
        var (result, _) = await InstallAsync(Config(App().AddMigration(failing)));

        Assert.That(result.Success, Is.True, result.Error);
        var record = result.Steps.Single(s => s.Name == "migration:fails");
        Assert.That(record.Outcome, Is.EqualTo(StepOutcome.SucceededWithWarnings));
        Assert.That(record.Warnings!.Single(), Does.Contain("disk said no"));
        Assert.That(_bed.FileSystem.Exists(Path.Combine(_bed.InstallPath, "ExampleApp.exe")), Is.True, "committed");
        var manifest = (await ManifestAsync())!;
        Assert.That(manifest.CompletedMigrations, Is.Null, "a failure is not recorded; it runs again next time");
        Assert.That(_bed.Log.Warnings, Has.Some.Contains("migration[fails]: failed: disk said no"));
    }

    [Test]
    public async Task ABeforeCommitFailure_FailsTheInstall_AndRollsItBack()
    {
        _bed.AddFile(KnownFolder.LocalAppData, "ExampleApp/ExampleApp.exe");
        var failing = new TestMigration("fails", MigrationTiming.BeforeCommit)
        {
            Body = async (t, _) =>
            {
                await t.DeleteFilesAsync(t.Folder(KnownFolder.LocalAppData, "ExampleApp"), ["ExampleApp.exe"], default);
                throw new IOException("disk said no");
            },
        };
        var (result, _) = await InstallAsync(Config(App().AddMigration(failing)));

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Is.EqualTo("migration 'fails' failed: disk said no"));
        Assert.That(failing.Rollbacks, Is.EqualTo(1));
        Assert.That(_bed.FileSystem.Exists(_bed.PathOf(KnownFolder.LocalAppData, "ExampleApp/ExampleApp.exe")), Is.True, "undone");
        Assert.That(await ManifestAsync(), Is.Null, "the commit never happened");
        Assert.That(_bed.FileSystem.Exists(Path.Combine(_bed.InstallPath, "ExampleApp.exe")), Is.False);
    }

    [Test]
    public async Task ALaterFailure_RollsABeforeCommitMigrationBack()
    {
        _bed.AddFile(KnownFolder.LocalAppData, "ExampleApp/ExampleApp.exe");
        var m = new TestMigration("before", MigrationTiming.BeforeCommit)
        {
            Body = (t, _) => t.DeleteFilesAsync(t.Folder(KnownFolder.LocalAppData, "ExampleApp"), ["ExampleApp.exe"], default),
        };
        var after = new TestMigration("after");
        var config = Config(App().AddMigration(m).AddMigration(after)
            .AddStep("fails-after-commit", s => s.InStage(InstallStage.Finalize)
                .Execute((_, _, _) => Task.FromResult(StepResult.Fail("custom step failed"))).NoRollbackNeeded("t")));

        var (result, _) = await InstallAsync(config);

        Assert.That(result.Success, Is.False);
        Assert.That(m.Rollbacks, Is.EqualTo(1));
        Assert.That(_bed.FileSystem.Exists(_bed.PathOf(KnownFolder.LocalAppData, "ExampleApp/ExampleApp.exe")), Is.True);
        Assert.That(after.Executions, Is.Zero, "AfterCommit migrations run only once everything else succeeded");
    }

    [Test]
    public async Task CancellingAfterTheCommit_SkipsAfterCommitMigrations_AndKeepsTheInstall()
    {
        using var cts = new CancellationTokenSource();
        var after = new TestMigration("after");
        var config = Config(App().AddMigration(after)
            .AddStep("cancel", s => s.InStage(InstallStage.Finalize).Execute((_, _, _) =>
            {
                cts.Cancel();
                return Task.FromResult(StepResult.Ok);
            }).NoRollbackNeeded("t")));

        var (result, _) = await InstallAsync(config, cts.Token);

        Assert.That(result.Success, Is.True, result.Error);
        Assert.That(after.Executions, Is.Zero);
        Assert.That(result.Steps.Single(s => s.Name == "migration:after").Outcome, Is.EqualTo(StepOutcome.Skipped));
        Assert.That(await ManifestAsync(), Is.Not.Null);
    }

    [Test]
    public async Task ABestEffortStepThatThrows_NeverFailsTheInstall()
    {
        var steps = new List<IInstallStepExecution>(Steps(Config(App()))) { new ThrowingBestEffortStep() };
        var options = new InstallOptions { InstallPath = _bed.InstallPath, Elevation = ElevationMode.PerUser };
        var context = InstallContextFactory.Create(Config(App()), InstallerMode.FirstInstall, _bed.InstallPath, options, _bed.Platform,
            _bed.FileSystem, _bed.Log, payload: Payload());
        var result = await new StepExecutor(steps).ExecuteAsync(context, null, CancellationToken.None);
        Assert.That(result.Success, Is.True, result.Error);
        Assert.That(result.Steps.Last().Outcome, Is.EqualTo(StepOutcome.SucceededWithWarnings));
    }

    [Test]
    public async Task UndoCopies_AreDeleted_WhenAFailurePastAPointOfNoReturnKeepsTheMigration()
    {
        _bed.AddFile(KnownFolder.LocalAppData, "ExampleApp/ExampleApp.exe");
        var m = new TestMigration("before", MigrationTiming.BeforeCommit)
        {
            Body = (t, _) => t.DeleteFilesAsync(t.Folder(KnownFolder.LocalAppData, "ExampleApp"), ["ExampleApp.exe"], default),
        };
        var config = Config(App().AddMigration(m)
            .AddStep("ponr", s => s.Execute(Ok).NoRollbackNeeded("t").PointOfNoReturn("data converted"))
            .AddStep("fails-later", s => s.InStage(InstallStage.Finalize).After("ponr")
                .Execute((_, _, _) => Task.FromResult(StepResult.Fail("custom step failed"))).NoRollbackNeeded("t")));

        var (result, _) = await InstallAsync(config);

        Assert.That(result.Success, Is.False);
        Assert.That(m.Rollbacks, Is.Zero, "rollback stops at the point of no return; the migration stands");
        Assert.That(_bed.FileSystem.Exists(_bed.PathOf(KnownFolder.LocalAppData, "ExampleApp/ExampleApp.exe")), Is.False);
        Assert.That(_bed.FileSystem.DirectoryExists(Path.Combine(_bed.Root, "undo")), Is.False, "no orphaned undo copies in %TEMP%");
    }

    [Test]
    public async Task UndoCopies_AreDeleted_OnceTheInstallSucceeds()
    {
        _bed.AddFile(KnownFolder.LocalAppData, "ExampleApp/ExampleApp.exe");
        var m = new TestMigration("before", MigrationTiming.BeforeCommit)
        {
            Body = (t, _) => t.DeleteFilesAsync(t.Folder(KnownFolder.LocalAppData, "ExampleApp"), ["ExampleApp.exe"], default),
        };
        var (result, _) = await InstallAsync(Config(App().AddMigration(m)));
        Assert.That(result.Success, Is.True, result.Error);
        Assert.That(_bed.FileSystem.Exists(_bed.PathOf(KnownFolder.LocalAppData, "ExampleApp/ExampleApp.exe")), Is.False);
        Assert.That(_bed.FileSystem.DirectoryExists(Path.Combine(_bed.Root, "undo")), Is.False);
    }

    private sealed class ThrowingBestEffortStep : IInstallStepExecution, IBestEffortStep
    {
        public string Name => "throws";
        public InstallStage Stage => InstallStage.Finalize;
        public int Weight => 1;
        public bool IsBestEffort => true;
        public Task<StepResult> ExecuteAsync(InstallContext context, IStepProgress progress, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("bug");
    }
}
