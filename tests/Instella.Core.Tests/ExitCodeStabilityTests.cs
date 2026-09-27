using Instella.Core.Installation;
using NUnit.Framework;

namespace Instella.Core.Tests;

/// <summary>
/// Integer values of <see cref="InstellaExitCode"/> are the public contract
/// scripts rely on. Reordering or renumbering silently breaks enterprise
/// deployment scripts that branch on <c>%errorlevel%</c>. Lock the values down
/// with explicit assertions so any future edit is caught at test time.
/// </summary>
[TestFixture]
public class ExitCodeStabilityTests
{
    [Test] public void Success_Is_0() => Assert.That((int)InstellaExitCode.Success, Is.EqualTo(0));
    [Test] public void UserCancelled_Is_1() => Assert.That((int)InstellaExitCode.UserCancelled, Is.EqualTo(1));

    [Test] public void Install_Range_Is_10_To_14()
    {
        Assert.That((int)InstellaExitCode.InstallGeneralFailure, Is.EqualTo(10));
        Assert.That((int)InstellaExitCode.InstallPrereqFailed, Is.EqualTo(11));
        Assert.That((int)InstellaExitCode.InstallIntegrityFailed, Is.EqualTo(12));
        Assert.That((int)InstellaExitCode.InstallRollbackCompletedWithWarnings, Is.EqualTo(13));
        Assert.That((int)InstellaExitCode.InstallSilentMissingState, Is.EqualTo(14));
    }

    [Test] public void Update_Range_Is_20_To_24()
    {
        Assert.That((int)InstellaExitCode.UpdateGeneralFailure, Is.EqualTo(20));
        Assert.That((int)InstellaExitCode.UpdateServerUnreachable, Is.EqualTo(21));
        Assert.That((int)InstellaExitCode.UpdateRolledBack, Is.EqualTo(22));
        Assert.That((int)InstellaExitCode.UpdateRollbackFailed, Is.EqualTo(23));
        Assert.That((int)InstellaExitCode.UpdateAppCouldNotClose, Is.EqualTo(24));
    }

    [Test] public void Uninstall_Range_Is_30_To_32()
    {
        Assert.That((int)InstellaExitCode.UninstallGeneralFailure, Is.EqualTo(30));
        Assert.That((int)InstellaExitCode.UninstallManifestMissing, Is.EqualTo(31));
        Assert.That((int)InstellaExitCode.UninstallFilesLocked, Is.EqualTo(32));
    }

    [Test] public void Usage_Range_Is_40_To_41()
    {
        Assert.That((int)InstellaExitCode.UsageInvalidArgs, Is.EqualTo(40));
        Assert.That((int)InstellaExitCode.UsageUnknownMode, Is.EqualTo(41));
    }

    [Test] public void Platform_Range_Is_50_To_52()
    {
        Assert.That((int)InstellaExitCode.UnsupportedPlatform, Is.EqualTo(50));
        Assert.That((int)InstellaExitCode.InsufficientPrivileges, Is.EqualTo(51));
        Assert.That((int)InstellaExitCode.InstallationBusy, Is.EqualTo(52));
    }
}

[TestFixture]
public class StepResultTests
{
    [Test] public void Ok_Is_Success_With_No_Error_Or_Warnings()
    {
        var r = StepResult.Ok;
        Assert.That(r.Success, Is.True);
        Assert.That(r.Error, Is.Null);
        Assert.That(r.Warnings, Is.Null);
    }

    [Test] public void Fail_Carries_Error()
    {
        var r = StepResult.Fail("boom");
        Assert.That(r.Success, Is.False);
        Assert.That(r.Error, Is.EqualTo("boom"));
    }

    [Test] public void OkWithWarnings_Carries_Warnings_But_Is_Success()
    {
        var r = StepResult.OkWithWarnings(new[] { "w1", "w2" });
        Assert.That(r.Success, Is.True);
        Assert.That(r.Error, Is.Null);
        Assert.That(r.Warnings!, Is.EqualTo(new[] { "w1", "w2" }));
    }
}

[TestFixture]
public class InstallStageOrderingTests
{
    [Test] public void Stages_Appear_In_Canonical_Order()
    {
        // Integer ordering matters: <, > comparisons against stages are
        // used when slotting custom steps before/after a given stage.
        Assert.That((int)InstallStage.Prereqs, Is.LessThan((int)InstallStage.Extract));
        Assert.That((int)InstallStage.Extract, Is.LessThan((int)InstallStage.Register));
        Assert.That((int)InstallStage.Register, Is.LessThan((int)InstallStage.Finalize));
    }
}
