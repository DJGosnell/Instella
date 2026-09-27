using System;
using System.Threading.Tasks;
using Instella.Core.Installation;
using Instella.Installer.Runtime.Installation.Builders;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Installation;

[TestFixture]
public class StepBuilderValidationTests
{
    [Test]
    public void Build_Throws_When_Execute_Not_Set()
    {
        var sb = StepBuilder.Create("step-a")
            .UseTrackedRollback();

        var ex = Assert.Throws<InvalidOperationException>(() => sb.Build());
        Assert.That(ex!.Message, Does.Contain("Execute"));
    }

    [Test]
    public void Build_Throws_When_No_Rollback_Mode_Chosen()
    {
        var sb = StepBuilder.Create("step-a")
            .Execute((ctx, p, ct) => Task.FromResult(StepResult.Ok));

        var ex = Assert.Throws<InvalidOperationException>(() => sb.Build());
        Assert.That(ex!.Message, Does.Contain("exactly one"));
    }

    [Test]
    public void Build_Throws_When_Rollback_And_UseTrackedRollback_Both_Chosen()
    {
        var sb = StepBuilder.Create("step-a")
            .Execute((ctx, p, ct) => Task.FromResult(StepResult.Ok))
            .Rollback((ctx, ct) => Task.CompletedTask)
            .UseTrackedRollback();

        var ex = Assert.Throws<InvalidOperationException>(() => sb.Build());
        Assert.That(ex!.Message, Does.Contain("mutually exclusive"));
    }

    [Test]
    public void Build_Throws_When_Rollback_And_NoRollbackNeeded_Both_Chosen()
    {
        var sb = StepBuilder.Create("step-a")
            .Execute((ctx, p, ct) => Task.FromResult(StepResult.Ok))
            .Rollback((ctx, ct) => Task.CompletedTask)
            .NoRollbackNeeded("safe");

        Assert.Throws<InvalidOperationException>(() => sb.Build());
    }

    [Test]
    public void Build_Throws_When_All_Three_Rollback_Modes_Chosen()
    {
        var sb = StepBuilder.Create("step-a")
            .Execute((ctx, p, ct) => Task.FromResult(StepResult.Ok))
            .Rollback((ctx, ct) => Task.CompletedTask)
            .UseTrackedRollback()
            .NoRollbackNeeded("safe");

        Assert.Throws<InvalidOperationException>(() => sb.Build());
    }

    [Test]
    public void Build_Succeeds_With_Explicit_Rollback()
    {
        var sb = StepBuilder.Create("step-a")
            .Execute((ctx, p, ct) => Task.FromResult(StepResult.Ok))
            .Rollback((ctx, ct) => Task.CompletedTask);

        var spec = sb.Build();
        Assert.That(spec.Name, Is.EqualTo("step-a"));
        Assert.That(spec.RollbackMode, Is.EqualTo(RollbackMode.Explicit));
    }

    [Test]
    public void Build_Succeeds_With_UseTrackedRollback()
    {
        var spec = StepBuilder.Create("step-a")
            .Execute((ctx, p, ct) => Task.FromResult(StepResult.Ok))
            .UseTrackedRollback()
            .Build();

        Assert.That(spec.RollbackMode, Is.EqualTo(RollbackMode.Tracked));
    }

    [Test]
    public void Build_Succeeds_With_NoRollbackNeeded()
    {
        var spec = StepBuilder.Create("step-a")
            .Execute((ctx, p, ct) => Task.FromResult(StepResult.Ok))
            .NoRollbackNeeded("idempotent log line");

        var built = spec.Build();
        Assert.That(built.RollbackMode, Is.EqualTo(RollbackMode.None));
    }

    [Test]
    public void NoRollbackNeeded_Rejects_Empty_Reason()
    {
        var sb = StepBuilder.Create("step-a")
            .Execute((ctx, p, ct) => Task.FromResult(StepResult.Ok));

        Assert.Throws<ArgumentException>(() => sb.NoRollbackNeeded(""));
    }

    [Test]
    public void PointOfNoReturn_Rejects_Empty_Reason()
    {
        var sb = StepBuilder.Create("step-a")
            .Execute((ctx, p, ct) => Task.FromResult(StepResult.Ok));

        Assert.Throws<ArgumentException>(() => sb.PointOfNoReturn(""));
    }

    [Test]
    public void PointOfNoReturn_Surfaces_On_Spec()
    {
        var spec = StepBuilder.Create("step-a")
            .Execute((ctx, p, ct) => Task.FromResult(StepResult.Ok))
            .UseTrackedRollback()
            .PointOfNoReturn("payload extracted")
            .Build();

        Assert.That(spec.IsPointOfNoReturn, Is.True);
        Assert.That(spec.PointOfNoReturnReason, Is.EqualTo("payload extracted"));
    }

    [Test]
    public void Constructor_Rejects_Empty_Name()
    {
        Assert.Throws<ArgumentException>(() => StepBuilder.Create(""));
    }

    [Test]
    public void Stage_And_Weight_Defaults_And_Overrides()
    {
        var defaultSpec = StepBuilder.Create("default")
            .Execute((ctx, p, ct) => Task.FromResult(StepResult.Ok))
            .NoRollbackNeeded("n/a")
            .Build();
        Assert.Multiple(() =>
        {
            Assert.That(defaultSpec.Stage, Is.EqualTo(InstallStage.Register));
            Assert.That(defaultSpec.Weight, Is.EqualTo(1));
        });

        var overridden = StepBuilder.Create("override")
            .InStage(InstallStage.Finalize)
            .Weight(7)
            .Execute((ctx, p, ct) => Task.FromResult(StepResult.Ok))
            .NoRollbackNeeded("n/a")
            .Build();
        Assert.Multiple(() =>
        {
            Assert.That(overridden.Stage, Is.EqualTo(InstallStage.Finalize));
            Assert.That(overridden.Weight, Is.EqualTo(7));
        });
    }

    [Test]
    public void When_Predicate_Defaults_To_Always_Run()
    {
        var spec = StepBuilder.Create("step")
            .Execute((ctx, p, ct) => Task.FromResult(StepResult.Ok))
            .NoRollbackNeeded("n/a")
            .Build();
        // We don't have an InstallContext here — verify by checking that with no When predicate,
        // ShouldRun returns true even with a null-ish context. Use a real one via TestContextBuilder.
        var ctx = new TestContextBuilder().Build();
        Assert.That(spec.ShouldRun(ctx), Is.True);
    }

    [Test]
    public void When_Predicate_Skips_Step()
    {
        var spec = StepBuilder.Create("step")
            .Execute((ctx, p, ct) => Task.FromResult(StepResult.Ok))
            .NoRollbackNeeded("n/a")
            .When(_ => false)
            .Build();

        var ctx = new TestContextBuilder().Build();
        Assert.That(spec.ShouldRun(ctx), Is.False);
    }
}
