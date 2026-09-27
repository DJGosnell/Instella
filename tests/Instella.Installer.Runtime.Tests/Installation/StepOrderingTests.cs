using System;
using System.Collections.Generic;
using System.Linq;
using Instella.Core.Installation;
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Runtime.Installation;
using Instella.Installer.Runtime.Installation.Builders;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Installation;

/// <summary>
/// Exercises <see cref="StepOrdering.BuildOrderedSteps"/>: built-in ordering is
/// preserved as a weak constraint, user <c>Before</c>/<c>After</c> hints move
/// specs, and cycles throw.
/// </summary>
[TestFixture]
public sealed class StepOrderingTests
{
    [Test]
    public void BuildOrderedSteps_preservesBuiltInOrder_whenNoUserSteps()
    {
        var builtIn = OfflineInstallRunner.BuildDefaultSteps();
        var ordered = StepOrdering.BuildOrderedSteps(builtIn, Array.Empty<StepSpec>());

        Assert.That(ordered.Count, Is.EqualTo(builtIn.Count));
        for (var i = 0; i < builtIn.Count; i++)
            Assert.That(ordered[i].Name, Is.EqualTo(builtIn[i].Name), $"position {i}");
    }

    [Test]
    public void BuildOrderedSteps_preservesStageUninstallerStub_beforeRegisterUninstallEntry()
    {
        var user = StepBuilder.Create("my-register-step")
            .InStage(InstallStage.Register)
            .Execute((_, _, _) => System.Threading.Tasks.Task.FromResult(StepResult.Ok))
            .NoRollbackNeeded("test")
            .Build();

        var ordered = StepOrdering.BuildOrderedSteps(
            OfflineInstallRunner.BuildDefaultSteps(),
            new[] { user });

        var stubIndex = IndexOf(ordered, "stage-uninstaller-stub");
        var arpIndex = IndexOf(ordered, "register-uninstall-entry");
        Assert.That(stubIndex, Is.GreaterThanOrEqualTo(0));
        Assert.That(arpIndex, Is.GreaterThanOrEqualTo(0));
        Assert.That(stubIndex, Is.LessThan(arpIndex), "stub must precede ARP registration");
    }

    [Test]
    public void BuildOrderedSteps_honorsAfterHint_movingUserStepBehindBuiltIn()
    {
        var user = StepBuilder.Create("after-path")
            .InStage(InstallStage.Register)
            .Execute((_, _, _) => System.Threading.Tasks.Task.FromResult(StepResult.Ok))
            .NoRollbackNeeded("test")
            .After("add-to-path")
            .Build();

        var ordered = StepOrdering.BuildOrderedSteps(
            OfflineInstallRunner.BuildDefaultSteps(),
            new[] { user });

        var pathIndex = IndexOf(ordered, "add-to-path");
        var userIndex = IndexOf(ordered, "after-path");
        Assert.That(pathIndex, Is.GreaterThanOrEqualTo(0));
        Assert.That(userIndex, Is.GreaterThan(pathIndex), "After-hint target should precede the hinting step");
    }

    [Test]
    public void BuildOrderedSteps_honorsBeforeHint_movingUserStepAheadOfBuiltIn()
    {
        var user = StepBuilder.Create("before-path")
            .InStage(InstallStage.Register)
            .Execute((_, _, _) => System.Threading.Tasks.Task.FromResult(StepResult.Ok))
            .NoRollbackNeeded("test")
            .Before("add-to-path")
            .Build();

        var ordered = StepOrdering.BuildOrderedSteps(
            OfflineInstallRunner.BuildDefaultSteps(),
            new[] { user });

        var pathIndex = IndexOf(ordered, "add-to-path");
        var userIndex = IndexOf(ordered, "before-path");
        Assert.That(userIndex, Is.GreaterThanOrEqualTo(0));
        Assert.That(pathIndex, Is.GreaterThanOrEqualTo(0));
        Assert.That(userIndex, Is.LessThan(pathIndex), "Before-hint step should precede its target");
    }

    [Test]
    public void BuildOrderedSteps_groupsByStage()
    {
        var finalizeStep = StepBuilder.Create("late-step")
            .InStage(InstallStage.Finalize)
            .Execute((_, _, _) => System.Threading.Tasks.Task.FromResult(StepResult.Ok))
            .NoRollbackNeeded("test")
            .Build();

        var ordered = StepOrdering.BuildOrderedSteps(
            OfflineInstallRunner.BuildDefaultSteps(),
            new[] { finalizeStep });

        // Extract index ranges per stage; every Register step must precede every Finalize step.
        var maxRegisterIdx = ordered
            .Select((s, i) => (s.Stage, i))
            .Where(t => t.Stage == InstallStage.Register)
            .Max(t => t.i);
        var minFinalizeIdx = ordered
            .Select((s, i) => (s.Stage, i))
            .Where(t => t.Stage == InstallStage.Finalize)
            .Min(t => t.i);

        Assert.That(maxRegisterIdx, Is.LessThan(minFinalizeIdx));
    }

    [Test]
    public void BuildOrderedSteps_throwsOnCycle()
    {
        var a = StepBuilder.Create("a")
            .InStage(InstallStage.Register)
            .Execute((_, _, _) => System.Threading.Tasks.Task.FromResult(StepResult.Ok))
            .NoRollbackNeeded("test")
            .After("b")
            .Build();
        var b = StepBuilder.Create("b")
            .InStage(InstallStage.Register)
            .Execute((_, _, _) => System.Threading.Tasks.Task.FromResult(StepResult.Ok))
            .NoRollbackNeeded("test")
            .After("a")
            .Build();

        Assert.Throws<InvalidOperationException>(() => StepOrdering.BuildOrderedSteps(
            Array.Empty<IInstallStepExecution>(),
            new[] { a, b }));
    }

    private static int IndexOf(IReadOnlyList<IInstallStepExecution> steps, string name)
    {
        for (var i = 0; i < steps.Count; i++)
            if (steps[i].Name == name) return i;
        return -1;
    }
}
