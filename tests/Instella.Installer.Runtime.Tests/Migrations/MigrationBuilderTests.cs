using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Installation;
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Runtime.Migrations;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Migrations;

/// <summary>Registration and <c>Build()</c> validation of install migrations.</summary>
[TestFixture]
public class MigrationBuilderTests
{
    private static InstallerBuilder App() => InstellaInstaller.Create().WithApp("ExampleApp", "com.example.app", new Version(1, 0));

    private static FrozenConfig Config(InstallerBuilder builder) => ((InstellaInstallerImpl)builder.Build()).ConfigForTests;

    private static string BuildError(InstallerBuilder builder) =>
        Assert.Throws<InvalidOperationException>(() => builder.Build())!.Message;

    private sealed class Registered : InstallMigration
    {
        public override string Id => "registered";
        protected override Condition When() => IsFirstInstall();
        protected override Task ExecuteAsync(CancellationToken ct) => Task.CompletedTask;
    }

    [Test]
    public void AddMigrationOfT_CreatesTheClass_AndFreezesIt()
    {
        var config = Config(App().AddMigration<Registered>());
        Assert.That(config.MigrationsOrEmpty.Single(), Is.TypeOf<Registered>());
    }

    [Test]
    public void AddMigrationInstance_KeepsTheInstance()
    {
        var m = new TestMigration("instance");
        Assert.That(Config(App().AddMigration(m)).MigrationsOrEmpty.Single(), Is.SameAs(m));
    }

    [Test]
    public void WithoutMigrations_TheConfigHasNone()
    {
        var config = Config(App());
        Assert.That(config.MigrationsOrEmpty, Is.Empty);
        Assert.That(config.AppManagedRunValuesOrEmpty, Is.Empty);
    }

    [Test]
    public void Migrations_AreSortedByTimingOrderAndId_WhateverTheRegistrationOrder()
    {
        var all = new[]
        {
            new TestMigration("z-after", MigrationTiming.AfterCommit),
            new TestMigration("uninstall", MigrationTiming.Uninstall) { RunOnceValue = false, WhenFactory = m => m.IsUninstall() },
            new TestMigration("a-after", MigrationTiming.AfterCommit),
            new TestMigration("late-before", MigrationTiming.BeforeCommit, order: 5),
            new TestMigration("early-after", MigrationTiming.AfterCommit, order: -1),
            new TestMigration("b-before", MigrationTiming.BeforeCommit),
        };
        var expected = new[] { "b-before", "late-before", "early-after", "a-after", "z-after", "uninstall" };
        for (var seed = 0; seed < 5; seed++)
        {
            var shuffled = all.OrderBy(_ => Random.Shared.Next()).ToList();
            var builder = App();
            foreach (var m in shuffled) builder.AddMigration(m);
            Assert.That(Config(builder).MigrationsOrEmpty.Select(m => m.Id), Is.EqualTo(expected));
        }
    }

    [TestCase("")]
    [TestCase("Upper")]
    [TestCase("-leading")]
    [TestCase("trailing.")]
    [TestCase("has space")]
    [TestCase("slash/id")]
    [TestCase("colon:id")]
    public void InvalidIds_AreRefused(string id)
    {
        Assert.That(BuildError(App().AddMigration(new TestMigration(id))), Does.Contain("the id must be"));
    }

    [Test]
    public void AnIdLongerThan64_IsRefused()
    {
        Assert.That(BuildError(App().AddMigration(new TestMigration(new string('a', 65)))), Does.Contain("the id must be"));
        Assert.DoesNotThrow(() => App().AddMigration(new TestMigration(new string('a', 64))).Build());
    }

    [TestCase("a")]
    [TestCase("replace-pre-instella-copy")]
    [TestCase("v1.2_settings")]
    public void ValidIds_AreAccepted(string id)
    {
        Assert.DoesNotThrow(() => App().AddMigration(new TestMigration(id)).Build());
    }

    [Test]
    public void DuplicateIds_AreRefused_AcrossTimings()
    {
        var error = BuildError(App()
            .AddMigration(new TestMigration("same", MigrationTiming.BeforeCommit))
            .AddMigration(new TestMigration("same", MigrationTiming.AfterCommit)));
        Assert.That(error, Does.Contain("same id"));
    }

    [Test]
    public void ABlankDisplayName_IsRefused()
    {
        Assert.That(BuildError(App().AddMigration(new TestMigration { DisplayNameValue = " " })), Does.Contain("DisplayName"));
    }

    [Test]
    public void AnUndefinedTiming_IsRefused()
    {
        Assert.That(BuildError(App().AddMigration(new TestMigration { TimingValue = (MigrationTiming)42 })), Does.Contain("Timing 42"));
    }

    [Test]
    public void AnUninstallMigration_MustNotBeRunOnce()
    {
        Assert.That(BuildError(App().AddMigration(new TestMigration("u", MigrationTiming.Uninstall) { RunOnceValue = true })),
            Does.Contain("RunOnce must be false"));
        Assert.That(new TestMigration("u", MigrationTiming.Uninstall).RunOnce, Is.False, "the default for uninstall migrations");
        Assert.That(new TestMigration("i").RunOnce, Is.True, "the default for install migrations");
    }

    [Test]
    public void WhenReadingContext_IsRefused_WithTheRule()
    {
        var m = new TestMigration { WhenFactory = t => t.Ctx.Mode == InstallerMode.Upgrade ? Condition.Always : Condition.Always };
        Assert.That(BuildError(App().AddMigration(m)), Does.Contain("When() may only compose conditions"));
    }

    [Test]
    public void AnInvalidVersionRange_IsABuildError()
    {
        var m = new TestMigration { WhenFactory = t => t.UpgradingFrom(">=one") };
        Assert.That(BuildError(App().AddMigration(m)), Does.Contain("invalid condition"));
    }

    [Test]
    public void AnInstallMigrationThatOnlyRunsOnUninstall_IsRefused()
    {
        var m = new TestMigration { WhenFactory = t => t.IsUninstall() & t.IsWindows() };
        Assert.That(BuildError(App().AddMigration(m)), Does.Contain("would never run"));
    }

    [Test]
    public void AnUninstallMigrationThatNeedsAnUpgrade_IsRefused()
    {
        var m = new TestMigration("u", MigrationTiming.Uninstall) { RunOnceValue = false, WhenFactory = t => t.UpgradingFrom("<2.0") };
        Assert.That(BuildError(App().AddMigration(m)), Does.Contain("would never run"));
    }

    [Test]
    public void ContradictoryModes_AreRefused()
    {
        var m = new TestMigration { WhenFactory = t => t.IsFirstInstall() & t.IsRepair() };
        Assert.That(BuildError(App().AddMigration(m)), Does.Contain("would never run"));
    }

    [Test]
    public void ReachableModeCombinations_AreAccepted()
    {
        Assert.DoesNotThrow(() => App().AddMigration(new TestMigration("a") { WhenFactory = t => !t.IsUninstall() }).Build());
        Assert.DoesNotThrow(() => App().AddMigration(new TestMigration("b") { WhenFactory = t => t.IsUninstall() | t.IsRepair() }).Build());
        Assert.DoesNotThrow(() => App().AddMigration(new TestMigration("c", MigrationTiming.Uninstall)
            { RunOnceValue = false, WhenFactory = t => !(t.IsRepair() & t.IsWindows()) }).Build());
    }

    [Test]
    public void UserStepsMayNotUseTheMigrationPrefix()
    {
        var builder = App().AddStep("migration:mine", s => s.Execute((_, _, _) => Task.FromResult(StepResult.Ok)).NoRollbackNeeded("test"));
        Assert.That(BuildError(builder), Does.Contain("reserved for migrations"));
    }

    [Test]
    public void AppManagedAutoStart_IsRecorded_OnceEach()
    {
        var config = Config(App().WithAppManagedAutoStart("ExampleApp").WithAppManagedAutoStart("exampleapp").WithAppManagedAutoStart("Helper"));
        Assert.That(config.AppManagedRunValuesOrEmpty, Is.EqualTo(new[] { "ExampleApp", "Helper" }));
    }

    [TestCase("")]
    [TestCase("  ")]
    [TestCase(@"Run\ExampleApp")]
    public void AppManagedAutoStart_RefusesBadNames(string name)
    {
        Assert.Throws<ArgumentException>(() => App().WithAppManagedAutoStart(name));
    }

    [Test]
    public void AppManagedAutoStart_MayNotNameInstellasOwnValue()
    {
        Assert.That(BuildError(App().WithAutoStart().WithAppManagedAutoStart("com.example.app")), Does.Contain("WithAutoStart()"));
        Assert.DoesNotThrow(() => App().WithAppManagedAutoStart("com.example.app").Build(), "fine without WithAutoStart");
    }

    [Test]
    public void AddMigration_RefusesNull()
    {
        Assert.Throws<ArgumentNullException>(() => App().AddMigration(null!));
    }
}
