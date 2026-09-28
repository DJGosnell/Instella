using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Installation;
using Instella.Core.Platform;
using Instella.Installer.Runtime.Migrations;
using Instella.Installer.Testing;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Migrations;

/// <summary>Every migration condition, true and false, and how they combine.</summary>
[TestFixture]
public class MigrationConditionTests
{
    private MigrationTestBed _bed = null!;
    private TestMigration _m = null!;

    [SetUp]
    public void SetUp()
    {
        _bed = new MigrationTestBed();
        _m = new TestMigration();
    }

    private Task<(bool Value, string? Reason)> Eval(Condition c) => _bed.EvaluateAsync(c);

    // ---- Modes -----------------------------------------------------------------------------

    [TestCase(InstallerMode.FirstInstall, true, false, true, false, false)]
    [TestCase(InstallerMode.Upgrade, false, true, true, false, false)]
    [TestCase(InstallerMode.Repair, false, false, false, true, false)]
    [TestCase(InstallerMode.Uninstall, false, false, false, false, true)]
    public async Task ModeConditions_FollowTheMode(InstallerMode mode, bool first, bool upgrade, bool firstOrUpgrade, bool repair, bool uninstall)
    {
        _bed.Mode = mode;
        Assert.That((await Eval(_m.IsFirstInstall())).Value, Is.EqualTo(first));
        Assert.That((await Eval(_m.IsUpgrade())).Value, Is.EqualTo(upgrade));
        Assert.That((await Eval(_m.IsFirstInstallOrUpgrade())).Value, Is.EqualTo(firstOrUpgrade));
        Assert.That((await Eval(_m.IsRepair())).Value, Is.EqualTo(repair));
        Assert.That((await Eval(_m.IsUninstall())).Value, Is.EqualTo(uninstall));
    }

    [Test]
    public async Task AFalseMode_SaysWhatTheModeIs()
    {
        _bed.Mode = InstallerMode.Repair;
        Assert.That((await Eval(_m.IsUpgrade())).Reason, Is.EqualTo("IsUpgrade() is false (the mode is Repair)"));
    }

    // ---- Versions --------------------------------------------------------------------------

    [TestCase("1.5", "<2.0", true)]
    [TestCase("2.0", "<2.0", false)]
    [TestCase("2", "<=2.0.0", true)]
    [TestCase("1.3", "=1.3.0", true)]
    [TestCase("1.3.0.0", "1.3", true)]
    [TestCase("1.4", "1.3", false)]
    [TestCase("1.0", ">=1.0 <2.0", true)]
    [TestCase("2.0", ">=1.0 <2.0", false)]
    [TestCase("0.9", ">=1.0 <2.0", false)]
    [TestCase("3.1", ">3", true)]
    [TestCase("7.7.7", "*", true)]
    public async Task UpgradingFrom_MatchesTheRange(string previous, string range, bool expected)
    {
        _bed.Mode = InstallerMode.Upgrade;
        _bed.PreviousVersion = Version.Parse(previous.Contains('.') ? previous : previous + ".0");
        Assert.That((await Eval(_m.UpgradingFrom(range))).Value, Is.EqualTo(expected));
    }

    [TestCase(InstallerMode.FirstInstall)]
    [TestCase(InstallerMode.Repair)]
    public async Task UpgradingFrom_IsFalseOutsideAnUpgrade(InstallerMode mode)
    {
        _bed.Mode = mode;
        _bed.PreviousVersion = mode == InstallerMode.Repair ? new Version(2, 0, 0) : null;
        var (value, reason) = await Eval(_m.UpgradingFrom("*"));
        Assert.That(value, Is.False);
        Assert.That(reason, Does.Contain("not an upgrade"));
    }

    [TestCase("")]
    [TestCase(" ")]
    [TestCase("one")]
    [TestCase("~1.0")]
    [TestCase(">=")]
    [TestCase("1.0 || 2.0")]
    [TestCase("v1.0")]
    public void UpgradingFrom_RefusesBadSyntax(string range)
    {
        Assert.Throws<ArgumentException>(() => _m.UpgradingFrom(range));
    }

    [Test]
    public void UpgradingFrom_PrintsCanonically()
    {
        Assert.That(_m.UpgradingFrom(">=1 <2.0").ToString(), Is.EqualTo("UpgradingFrom(>=1.0.0 <2.0.0)"));
    }

    // ---- Paths -----------------------------------------------------------------------------

    [Test]
    public async Task FileExists_AndFolderExists()
    {
        _bed.AddFile(KnownFolder.LocalAppData, "ExampleApp/ExampleApp.exe");
        Assert.That((await Eval(_m.FileExists(KnownFolder.LocalAppData, "ExampleApp/ExampleApp.exe"))).Value, Is.True);
        Assert.That((await Eval(_m.FileExists(KnownFolder.LocalAppData, @"ExampleApp\ExampleApp.exe"))).Value, Is.True, "either separator");
        Assert.That((await Eval(_m.FileExists(KnownFolder.LocalAppData, "ExampleApp/Missing.exe"))).Value, Is.False);
        Assert.That((await Eval(_m.FileExists(KnownFolder.LocalAppData, "ExampleApp"))).Value, Is.False, "a folder is not a file");
        Assert.That((await Eval(_m.FolderExists(KnownFolder.LocalAppData, "ExampleApp"))).Value, Is.True);
        Assert.That((await Eval(_m.FolderExists(KnownFolder.LocalAppData, "Other"))).Value, Is.False);
        Assert.That((await Eval(_m.FileExists(KnownFolder.RoamingAppData, "ExampleApp/ExampleApp.exe"))).Value, Is.False, "another root");
    }

    [TestCase(KnownFolder.ProgramFiles)]
    [TestCase(KnownFolder.ProgramFilesX86)]
    [TestCase(KnownFolder.ProgramData)]
    [TestCase(KnownFolder.RoamingAppData)]
    public async Task EveryKnownFolder_Resolves(KnownFolder root)
    {
        _bed.AddFile(root, "Vendor/app.exe");
        Assert.That((await Eval(_m.FileExists(root, "Vendor/app.exe"))).Value, Is.True);
    }

    [Test]
    public async Task StartMenuPrograms_FollowsTheScope()
    {
        _bed.FileSystem.AddFile(Path.Combine(_bed.UserStartMenu, "ExampleApp.lnk"), [1]);
        Assert.That((await Eval(_m.FileExists(KnownFolder.StartMenuPrograms, "ExampleApp.lnk"))).Value, Is.True);
        _bed.Scope = InstallationScope.SystemWide;
        Assert.That((await Eval(_m.FileExists(KnownFolder.StartMenuPrograms, "ExampleApp.lnk"))).Value, Is.False, "all users' Start menu");
        _bed.FileSystem.AddFile(Path.Combine(_bed.CommonStartMenu, "ExampleApp.lnk"), [1]);
        Assert.That((await Eval(_m.FileExists(KnownFolder.StartMenuPrograms, "ExampleApp.lnk"))).Value, Is.True);
    }

    [Test]
    public async Task InstallFolder_WorksInConditions()
    {
        _bed.AddFile(KnownFolder.InstallFolder, "legacy.cfg");
        Assert.That((await Eval(_m.FileExists(KnownFolder.InstallFolder, "legacy.cfg"))).Value, Is.True);
    }

    [TestCase(KnownFolder.LocalAppData)]
    [TestCase(KnownFolder.RoamingAppData)]
    public void PerUserFolders_CannotBeCheckedInAMachineInstall(KnownFolder root)
    {
        _bed.AddFile(root, "ExampleApp/ExampleApp.exe");
        _bed.Scope = InstallationScope.SystemWide;
        Assert.That(Unknown(_m.FileExists(root, "ExampleApp/ExampleApp.exe")), Does.Contain("per-user folder").And.Contain("machine-wide"));
        Assert.That(Unknown(!_m.FileExists(root, "ExampleApp/ExampleApp.exe")), Does.Contain("per-user folder"),
            "\"cannot check\" is never read as \"false\", so ! cannot make it true");
    }

    [Test]
    public void AFolderMissingOnThisPlatform_CannotBeChecked()
    {
        _bed.Folders.Remove(KnownFolder.ProgramFilesX86);
        Assert.That(Unknown(_m.FolderExists(KnownFolder.ProgramFilesX86, "Vendor")), Does.Contain("does not exist on this platform"));
    }

    [Test]
    public void AnUnreadableFile_CannotBeChecked_UnderAnyOperator()
    {
        _bed.AddFile(KnownFolder.LocalAppData, "ExampleApp/ExampleApp.exe");
        _bed.FileSystem.DenyReads(_bed.PathOf(KnownFolder.LocalAppData, "ExampleApp"));
        Assert.That(Unknown(_m.FileExists(KnownFolder.LocalAppData, "ExampleApp/ExampleApp.exe")), Does.Contain("cannot be read"));
        Assert.That(Unknown(!_m.FolderExists(KnownFolder.LocalAppData, "ExampleApp")), Does.Contain("cannot be read"));
        Assert.That(Unknown(!_m.InstellaInstallationAt(KnownFolder.LocalAppData, "ExampleApp")), Does.Contain("cannot be read"));
    }

    [Test]
    public void AnUnreadableRunKey_CannotBeChecked()
    {
        _bed.SetRunValue("ExampleApp", "\"C:\\x\\a.exe\"");
        _bed.Platform.DenyRegistryReads(Instella.Core.Platform.RegistryHive.CurrentUser, RunCommand.RunKey);
        Assert.That(Unknown(!_m.RunValueExists("ExampleApp")), Does.Contain("could not be read").And.Contain("denied"));
        Assert.That(Unknown(!_m.RunValuePointsInto("ExampleApp", _m.Folder(KnownFolder.LocalAppData, "x"))), Does.Contain("could not be read"));
        Assert.That(Unknown(!_m.RegistryValueExists(Instella.Core.Platform.RegistryHive.CurrentUser, RunCommand.RunKey, "ExampleApp")),
            Does.Contain("could not be read"));
    }

    [Test]
    public void AProcessLookupThatFails_CannotBeChecked()
    {
        _bed.AddFile(KnownFolder.LocalAppData, "ExampleApp/ExampleApp.exe");
        _bed.FileSystem.DenyReads(_bed.PathOf(KnownFolder.LocalAppData, "ExampleApp"));
        Assert.That(Unknown(!_m.ProcessRunningIn(_m.Folder(KnownFolder.LocalAppData, "ExampleApp"))), Does.Contain("could not be found"));
    }

    /// <summary>Asserts the condition cannot be evaluated, and returns why.</summary>
    private string Unknown(Condition condition) =>
        Assert.ThrowsAsync<ConditionEvaluationException>(() => Eval(condition))!.Message;

    [Test]
    public async Task InstellaInstallationAt_LooksForTheManifest()
    {
        _bed.AddFile(KnownFolder.LocalAppData, "ExampleApp/ExampleApp.exe");
        Assert.That((await Eval(_m.InstellaInstallationAt(KnownFolder.LocalAppData, "ExampleApp"))).Value, Is.False);
        _bed.AddFile(KnownFolder.LocalAppData, "ExampleApp/" + InstellaOwnedPaths.InstalledManifest, "{}");
        Assert.That((await Eval(_m.InstellaInstallationAt(KnownFolder.LocalAppData, "ExampleApp"))).Value, Is.True);
    }

    [TestCase("")]
    [TestCase("../escape")]
    [TestCase("a/../../b")]
    [TestCase(@"C:\absolute")]
    [TestCase("/rooted")]
    [TestCase("wild*.exe")]
    [TestCase("what?.exe")]
    [TestCase("CON")]
    [TestCase("ads:stream")]
    public void UnsafeRelativePaths_AreRefusedWhenTheConditionIsBuilt(string relative)
    {
        Assert.Throws<ArgumentException>(() => _m.FileExists(KnownFolder.LocalAppData, relative));
        Assert.Throws<ArgumentException>(() => _m.Folder(KnownFolder.LocalAppData, relative));
    }

    // ---- Registry --------------------------------------------------------------------------

    [Test]
    public async Task RunValueExists_ReadsTheScopesRunKey()
    {
        Assert.That((await Eval(_m.RunValueExists("ExampleApp"))).Value, Is.False);
        _bed.SetRunValue("ExampleApp", "\"C:\\x\\ExampleApp.exe\"");
        Assert.That((await Eval(_m.RunValueExists("ExampleApp"))).Value, Is.True);
        _bed.Scope = InstallationScope.SystemWide;
        Assert.That((await Eval(_m.RunValueExists("ExampleApp"))).Value, Is.False, "HKCU's value is not HKLM's");
        _bed.SetRunValue("ExampleApp", "\"C:\\x\\ExampleApp.exe\"");
        Assert.That((await Eval(_m.RunValueExists("ExampleApp"))).Value, Is.True);
    }

    [Test]
    public async Task RunValuePointsInto_ComparesTheExecutablesFolder()
    {
        var oldExe = _bed.PathOf(KnownFolder.LocalAppData, "ExampleApp/ExampleApp.exe");
        var folder = _m.Folder(KnownFolder.LocalAppData, "ExampleApp");
        Assert.That((await Eval(_m.RunValuePointsInto("ExampleApp", folder))).Reason, Does.Contain("no Run value"));

        _bed.SetRunValue("ExampleApp", $"\"{oldExe}\" --tray");
        Assert.That((await Eval(_m.RunValuePointsInto("ExampleApp", folder))).Value, Is.True);

        _bed.SetRunValue("ExampleApp", $"\"{_bed.PathOf(KnownFolder.LocalAppData, "ExampleAppOther/ExampleApp.exe")}\"");
        Assert.That((await Eval(_m.RunValuePointsInto("ExampleApp", folder))).Value, Is.False, "a sibling sharing the prefix");

        _bed.SetRunValue("ExampleApp", "not a path");
        Assert.That((await Eval(_m.RunValuePointsInto("ExampleApp", folder))).Value, Is.False);
    }

    [Test]
    public void RunValuePointsInto_APerUserFolderInAMachineInstall_CannotBeChecked()
    {
        _bed.Scope = InstallationScope.SystemWide;
        _bed.SetRunValue("ExampleApp", $"\"{Path.Combine(_bed.Folders[KnownFolder.LocalAppData], "ExampleApp", "ExampleApp.exe")}\"");
        Assert.That(Unknown(_m.RunValuePointsInto("ExampleApp", _m.Folder(KnownFolder.LocalAppData, "ExampleApp"))), Does.Contain("per-user folder"));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase(@"Run\Name")]
    public void RunValueNames_AreValidated(string? name)
    {
        Assert.Catch<ArgumentException>(() => _m.RunValueExists(name!));
    }

    [Test]
    public async Task RegistryValueExists_ByHive()
    {
        _bed.Platform.Registry.Set(RegistryHive.CurrentUser, @"Software\ExampleApp", "Theme", InstellaRegistryValueKind.String, "dark");
        _bed.Platform.Registry.Set(RegistryHive.LocalMachine, @"Software\ExampleApp", "Edition", InstellaRegistryValueKind.String, "pro");

        Assert.That((await Eval(_m.RegistryValueExists(RegistryHive.CurrentUser, @"Software\ExampleApp", "Theme"))).Value, Is.True);
        Assert.That((await Eval(_m.RegistryValueExists(RegistryHive.CurrentUser, @"Software\ExampleApp", "Missing"))).Value, Is.False);
        Assert.That((await Eval(_m.RegistryValueExists(RegistryHive.LocalMachine, @"Software\ExampleApp", "Edition"))).Value, Is.True);
        Assert.That((await Eval(_m.RegistryValueExists(RegistryHive.AutoFromScope, @"Software\ExampleApp", "Theme"))).Value, Is.True, "per-user → HKCU");

        _bed.Scope = InstallationScope.SystemWide;
        Assert.That(Unknown(_m.RegistryValueExists(RegistryHive.CurrentUser, @"Software\ExampleApp", "Theme")), Does.Contain("HKCU"));
        Assert.That((await Eval(_m.RegistryValueExists(RegistryHive.AutoFromScope, @"Software\ExampleApp", "Edition"))).Value, Is.True, "machine → HKLM");
    }

    // ---- Processes -------------------------------------------------------------------------

    [Test]
    public async Task ProcessRunningIn_FindsProgramsHoldingFilesInTheFolder()
    {
        var folder = _m.Folder(KnownFolder.LocalAppData, "ExampleApp");
        var exe = _bed.PathOf(KnownFolder.LocalAppData, "ExampleApp/ExampleApp.exe");
        _bed.AddFile(KnownFolder.LocalAppData, "ExampleApp/ExampleApp.exe");
        Assert.That((await Eval(_m.ProcessRunningIn(folder))).Value, Is.False);

        _bed.Processes.Start(exe);
        var (value, reason) = await Eval(_m.ProcessRunningIn(folder));
        Assert.That(value, Is.True);
        Assert.That(reason, Does.Contain("ExampleApp.exe"));
    }

    [Test]
    public async Task ProcessRunningIn_IgnoresProcessesThatAreNeverClosed()
    {
        var exe = _bed.PathOf(KnownFolder.LocalAppData, "ExampleApp/shell.dll");
        _bed.AddFile(KnownFolder.LocalAppData, "ExampleApp/shell.dll");
        _bed.Processes.Start(exe, canClose: false);
        Assert.That((await Eval(_m.ProcessRunningIn(_m.Folder(KnownFolder.LocalAppData, "ExampleApp")))).Value, Is.False);
    }

    // ---- Platform, scope, custom -----------------------------------------------------------

    [Test]
    public async Task PlatformAndScope()
    {
        Assert.That((await Eval(_m.IsWindows())).Value, Is.True);
        Assert.That((await Eval(_m.IsPerUserInstall())).Value, Is.True);
        Assert.That((await Eval(_m.IsMachineInstall())).Value, Is.False);
        _bed.Scope = InstallationScope.SystemWide;
        _bed.Platform = new FakePlatformServices(TargetPlatform.Linux);
        Assert.That((await Eval(_m.IsWindows())).Reason, Does.Contain("Linux"));
        Assert.That((await Eval(_m.IsPerUserInstall())).Value, Is.False);
        Assert.That((await Eval(_m.IsMachineInstall())).Value, Is.True);
    }

    [Test]
    public async Task ConditionFrom_SeesTheContext()
    {
        _bed.Mode = InstallerMode.Upgrade;
        _bed.PreviousVersion = new Version(1, 2, 0);
        var c = Condition.From(ctx => ctx.PreviousVersion == new Version(1, 2, 0) && ctx.AppId == "com.example.app", "custom check");
        Assert.That((await Eval(c)).Value, Is.True);
        Assert.That(c.ToString(), Is.EqualTo("custom check"));
        Assert.That((await Eval(Condition.From(_ => false))).Reason, Is.EqualTo("custom condition is false"));
    }

    [Test]
    public void AThrowingCustomCondition_IsNotFalse_ItThrows()
    {
        // "false" would become "true" under !, so a check that never completed must not have a value.
        var ex = Assert.ThrowsAsync<ConditionEvaluationException>(() => Eval(Condition.From(_ => throw new InvalidOperationException("boom"), "fragile")));
        Assert.That(ex!.Message, Is.EqualTo("fragile threw InvalidOperationException: boom"));
        Assert.ThrowsAsync<ConditionEvaluationException>(() => Eval(!Condition.From(_ => throw new IOException("x"))));
    }

    [TestCase("not")]
    [TestCase("and")]
    [TestCase("or")]
    public async Task AMigrationWhoseCustomConditionThrows_IsSkipped_WhateverTheOperators(string shape)
    {
        var failing = Condition.From(_ => throw new UnauthorizedAccessException("denied"), "already migrated");
        var m = new TestMigration
        {
            WhenFactory = t => shape switch
            {
                "not" => t.IsFirstInstall() & !failing,
                "and" => !(failing & t.IsWindows()),
                _ => !(failing | t.IsRepair()),
            },
        };
        var result = await MigrationExecution.RunAsync(m, _bed.Context(), CancellationToken.None);
        Assert.That(result.Outcome, Is.EqualTo(MigrationRunOutcome.Skipped));
        Assert.That(result.Reason, Is.EqualTo("could not evaluate the condition: already migrated threw UnauthorizedAccessException: denied"));
        Assert.That(m.Executions, Is.Zero, "the migration never acts on a check that did not complete");
    }

    [Test]
    public async Task Always_IsTrue()
    {
        Assert.That((await Eval(Condition.Always)).Value, Is.True);
    }

    // ---- Combinators -----------------------------------------------------------------------

    [Test]
    public async Task And_Or_Not_Combine_AndShortCircuit()
    {
        var evaluated = 0;
        var counting = Condition.From(_ => { evaluated++; return true; }, "counting");
        var no = Condition.From(_ => false, "no");

        Assert.That((await Eval(no & counting)).Value, Is.False);
        Assert.That(evaluated, Is.Zero, "& stops at the first false");
        Assert.That((await Eval(Condition.Always | counting)).Value, Is.True);
        Assert.That(evaluated, Is.Zero, "| stops at the first true");
        Assert.That((await Eval(counting & Condition.Always)).Value, Is.True);
        Assert.That((await Eval(!no)).Value, Is.True);
        Assert.That((await Eval(!Condition.Always)).Reason, Is.EqualTo("Always is true"));
        Assert.That((await Eval(no | no)).Reason, Is.EqualTo("neither (no is false) nor (no is false)"));
    }

    [Test]
    public async Task AFalseAnd_ReportsTheLeafThatWasFalse()
    {
        _bed.Mode = InstallerMode.Upgrade;
        var c = _m.IsFirstInstallOrUpgrade() & _m.FileExists(KnownFolder.LocalAppData, "ExampleApp/ExampleApp.exe");
        Assert.That((await Eval(c)).Reason, Is.EqualTo("FileExists(LocalAppData/ExampleApp/ExampleApp.exe) is false"));
    }

    [Test]
    public void Conditions_PrintReadably()
    {
        var folder = _m.Folder(KnownFolder.LocalAppData, "ExampleApp");
        var c = _m.IsFirstInstallOrUpgrade() & (_m.RunValueExists("A") | _m.ProcessRunningIn(folder)) & !_m.InstellaInstallationAt(KnownFolder.LocalAppData, "ExampleApp");
        Assert.That(c.ToString(), Is.EqualTo(
            "IsFirstInstallOrUpgrade() & (RunValueExists(A) | ProcessRunningIn(LocalAppData/ExampleApp)) & !InstellaInstallationAt(LocalAppData/ExampleApp)"));
        Assert.That((!(_m.IsRepair() & _m.IsWindows())).ToString(), Is.EqualTo("!(IsRepair() & IsWindows())"));
        Assert.That(_m.RunValuePointsInto("A", folder).ToString(), Is.EqualTo("RunValuePointsInto(A, LocalAppData/ExampleApp)"));
        Assert.That(_m.RegistryValueExists(RegistryHive.CurrentUser, @"Software\X", "V").ToString(), Is.EqualTo(@"RegistryValueExists(CurrentUser\Software\X\V)"));
        Assert.That(folder.ToString(), Is.EqualTo("LocalAppData/ExampleApp"));
    }

    [Test]
    public void PossibleModes_FollowTheAlgebra()
    {
        Assert.That(_m.IsUpgrade().PossibleModes, Is.EqualTo(ModeSet.Upgrade));
        Assert.That((_m.IsUpgrade() | _m.IsRepair()).PossibleModes, Is.EqualTo(ModeSet.Upgrade | ModeSet.Repair));
        Assert.That((_m.IsFirstInstallOrUpgrade() & _m.IsUpgrade()).PossibleModes, Is.EqualTo(ModeSet.Upgrade));
        Assert.That((!_m.IsUninstall()).PossibleModes, Is.EqualTo(ModeSet.Install));
        Assert.That((!(_m.IsUninstall() & _m.IsWindows())).PossibleModes, Is.EqualTo(ModeSet.All), "! of a compound is conservative");
        Assert.That(_m.UpgradingFrom("*").PossibleModes, Is.EqualTo(ModeSet.Upgrade));
        Assert.That((!_m.UpgradingFrom("*")).PossibleModes, Is.EqualTo(ModeSet.All));
        Assert.That(_m.IsWindows().PossibleModes, Is.EqualTo(ModeSet.All));
    }

    [Test]
    public void CombiningNull_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => _ = Condition.Always & null!);
        Assert.Throws<ArgumentNullException>(() => _ = null! | Condition.Always);
        Assert.Throws<ArgumentNullException>(() => _ = !(Condition)null!);
        Assert.Throws<ArgumentNullException>(() => Condition.From(null!));
    }

    [Test]
    public void ContextAndLog_ThrowOutsideARun()
    {
        Assert.Throws<InvalidOperationException>(() => _ = _m.Ctx);
    }

}
