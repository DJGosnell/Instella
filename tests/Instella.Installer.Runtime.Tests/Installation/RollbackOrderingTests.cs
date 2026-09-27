using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Installation;
using Instella.Core.Platform;
using Instella.Installer.Runtime.Installation;
using Instella.Installer.Runtime.Installation.Builders;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Installation;

[TestFixture]
public class RollbackOrderingTests
{
    [Test]
    public async Task Without_PONR_All_Completed_Steps_Roll_Back_In_Reverse_Order()
    {
        var log = new List<string>();
        var executor = new StepExecutor(new IInstallStepExecution[]
        {
            MakeStep("a", log),
            MakeStep("b", log),
            MakeStep("c", log),
            MakeFailingStep("d", log, "boom"),
        });

        var result = await executor.ExecuteAsync(new TestContextBuilder().Build(), progress: null, CancellationToken.None);

        Assert.That(result.Success, Is.False);
        Assert.That(log, Is.EqualTo(new[]
        {
            "a:exec", "b:exec", "c:exec", "d:exec-fail",
            "c:rollback", "b:rollback", "a:rollback",
        }));
    }

    [Test]
    public async Task PONR_Step_And_Earlier_Steps_Are_Not_Rolled_Back()
    {
        var log = new List<string>();
        var executor = new StepExecutor(new IInstallStepExecution[]
        {
            MakeStep("a", log),
            MakeStep("b-ponr", log, ponrReason: "payload extracted"),
            MakeStep("c", log),
            MakeFailingStep("d", log, "boom"),
        });

        var result = await executor.ExecuteAsync(new TestContextBuilder().Build(), progress: null, CancellationToken.None);

        Assert.That(result.Success, Is.False);
        // Rollback should ONLY include "c" — both "a" and the PONR step itself ("b-ponr") survive.
        Assert.That(log, Is.EqualTo(new[]
        {
            "a:exec", "b-ponr:exec", "c:exec", "d:exec-fail",
            "c:rollback",
        }));
    }

    [Test]
    public async Task PONR_Snapshots_Ledger_So_Pre_PONR_Tracked_Entries_Survive()
    {
        var b = new TestContextBuilder();
        b.FileSystem.Files[@"C:\install\pre.txt"] = new byte[] { 1 };
        b.FileSystem.Files[@"C:\install\post.txt"] = new byte[] { 2 };
        var ctx = b.Build();

        var executor = new StepExecutor(new IInstallStepExecution[]
        {
            new TrackingStep("pre", trackPath: @"C:\install\pre.txt"),
            new TrackingStep("ponr", trackPath: null, ponrReason: "no return"),
            new TrackingStep("post", trackPath: @"C:\install\post.txt"),
            MakeFailingStep("fail", new List<string>(), "boom"),
        });

        await executor.ExecuteAsync(ctx, progress: null, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(b.FileSystem.Files.ContainsKey(@"C:\install\pre.txt"), Is.True,
                "pre-PONR tracked file should survive rollback");
            Assert.That(b.FileSystem.Files.ContainsKey(@"C:\install\post.txt"), Is.False,
                "post-PONR tracked file should be unwound");
        });
    }

    [Test]
    public async Task Rollback_Of_Rollback_Failures_Are_Collected_As_Warnings_With_PONR_Boundary()
    {
        var log = new List<string>();
        var executor = new StepExecutor(new IInstallStepExecution[]
        {
            new RollbackThrowingStep("a", "should-not-throw-because-pre-PONR"),
            MakeStep("b-ponr", log, ponrReason: "no return"),
            new RollbackThrowingStep("c", "rollback-c-boom"),
            MakeFailingStep("d", log, "step-d-boom"),
        });

        var result = await executor.ExecuteAsync(new TestContextBuilder().Build(), progress: null, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.Error, Is.EqualTo("step-d-boom"));
            // c's rollback throws and is collected.
            Assert.That(result.Warnings, Has.Some.Contains("rollback-c-boom"));
            // a's rollback should NOT have been invoked at all (pre-PONR), so its message must be absent.
            Assert.That(result.Warnings, Has.None.Contains("should-not-throw-because-pre-PONR"));
        });
    }

    [Test]
    public async Task Skipped_Step_Does_Not_Enter_Rollback_List()
    {
        var log = new List<string>();
        var skipSpec = StepBuilder.Create("skip-me")
            .Execute((ctx, p, ct) => { log.Add("skip-me:exec"); return Task.FromResult(StepResult.Ok); })
            .Rollback((ctx, ct) => { log.Add("skip-me:rollback"); return Task.CompletedTask; })
            .When(_ => false)
            .Build();

        var executor = new StepExecutor(new IInstallStepExecution[]
        {
            MakeStep("a", log),
            skipSpec,
            MakeFailingStep("c", log, "boom"),
        });

        await executor.ExecuteAsync(new TestContextBuilder().Build(), progress: null, CancellationToken.None);

        // skip-me's exec must not have run; its rollback must not have run either.
        Assert.That(log, Does.Not.Contain("skip-me:exec"));
        Assert.That(log, Does.Not.Contain("skip-me:rollback"));
    }

    // ----- Test fakes -----

    private static IInstallStepExecution MakeStep(string name, List<string> log, string? ponrReason = null)
    {
        var sb = StepBuilder.Create(name)
            .Execute((ctx, p, ct) => { log.Add($"{name}:exec"); return Task.FromResult(StepResult.Ok); })
            .Rollback((ctx, ct) => { log.Add($"{name}:rollback"); return Task.CompletedTask; });
        if (ponrReason is not null) sb = sb.PointOfNoReturn(ponrReason);
        return sb.Build();
    }

    private static IInstallStepExecution MakeFailingStep(string name, List<string> log, string error)
    {
        return StepBuilder.Create(name)
            .Execute((ctx, p, ct) => { log.Add($"{name}:exec-fail"); return Task.FromResult(StepResult.Fail(error)); })
            .NoRollbackNeeded("step never succeeded")
            .Build();
    }

    private sealed class TrackingStep(string name, string? trackPath, string? ponrReason = null) : IInstallStepExecution
    {
        public string Name => name;
        public InstallStage Stage => InstallStage.Register;
        public bool IsPointOfNoReturn => ponrReason is not null;
        public string? PointOfNoReturnReason => ponrReason;
        public Task<StepResult> ExecuteAsync(InstallContext ctx, IStepProgress p, CancellationToken ct)
        {
            if (trackPath is not null) ctx.TrackFile(trackPath);
            return Task.FromResult(StepResult.Ok);
        }
    }

    private sealed class RollbackThrowingStep(string name, string rollbackError) : IInstallStepExecution
    {
        public string Name => name;
        public InstallStage Stage => InstallStage.Register;
        public Task<StepResult> ExecuteAsync(InstallContext ctx, IStepProgress p, CancellationToken ct)
            => Task.FromResult(StepResult.Ok);
        public Task RollbackAsync(InstallContext ctx, CancellationToken ct)
            => throw new System.Exception(rollbackError);
    }
}
