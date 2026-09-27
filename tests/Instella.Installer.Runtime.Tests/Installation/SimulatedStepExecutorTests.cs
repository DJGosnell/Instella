using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.FileSystem;
using Instella.Core.Installation;
using Instella.Core.Logging;
using Instella.Core.Manifest;
using Instella.Core.Platform;
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Runtime.Core;
using Instella.Installer.Runtime.Installation;
using Instella.Installer.Runtime.Runners;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Installation;

/// <summary>
/// <see cref="SimulatedStepExecutor"/> tests. Uses
/// <see cref="PreviewSpeed.Fast"/> (≈50 ms per step) so a 3-step run costs
/// ~150 ms end-to-end; tests stay responsive without sacrificing the
/// interpolated-progress shape.
/// </summary>
[TestFixture]
public sealed class SimulatedStepExecutorTests
{
    private static IInstallStepExecution Step(string name, InstallStage stage = InstallStage.Register, int weight = 1)
        => new FakeStep(name, stage, weight);

    /// <summary>
    /// <see cref="IProgress{T}"/> that invokes the handler synchronously on the
    /// reporting thread, so every report is observable once the awaited call returns.
    /// </summary>
    private sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    private static InstallContext BuildContext()
    {
        var platform = new NoOpPlatformServices(TargetPlatform.Windows);
        var manifest = new InstellaManifest
        {
            AppName = "Preview Test",
            AppId = "com.example.preview",
            Version = new Version(1, 0, 0),
            ServerUrl = string.Empty,
        };
        return new InstallContext
        {
            AppName = manifest.AppName,
            AppId = manifest.AppId,
            AppVersion = manifest.Version,
            InstallPath = "preview://path",
            Mode = InstallerMode.FirstInstall,
            Scope = InstallationScope.PerUser,
            Manifest = manifest,
            Options = new InstallOptions { InstallPath = "preview://path" },
            Platform = platform,
            FileSystem = RealFileSystem.Instance,
            Log = new CapturingLogger(),
        };
    }

    [Test]
    public async Task Execute_allStepsSucceed_whenNoFailureInjected()
    {
        var steps = new[] { Step("a"), Step("b"), Step("c") };
        var executor = new SimulatedStepExecutor(steps, PreviewSpeed.Fast, failAtStep: null);

        var result = await executor.ExecuteAsync(BuildContext(), progress: null, CancellationToken.None);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Steps.Select(s => s.Name), Is.EqualTo(new[] { "a", "b", "c" }));
        Assert.That(result.Steps.All(s => s.Outcome == StepOutcome.Succeeded), Is.True);
    }

    [Test]
    public async Task Execute_progressReports_produceInterpolatedTicks()
    {
        var steps = new[] { Step("a"), Step("b") };
        var executor = new SimulatedStepExecutor(steps, PreviewSpeed.Fast, failAtStep: null);
        var reports = new List<double>();
        // Deliberately NOT Progress<T>: it marshals the callback to the captured
        // SynchronizationContext (the thread pool, under NUnit), so reports can
        // still be in flight when ExecuteAsync returns and the assertions below
        // race the final tick. An inline IProgress makes the sequence complete.
        var progress = new InlineProgress<OverallProgress>(p => reports.Add(p.Fraction));

        await executor.ExecuteAsync(BuildContext(), progress, CancellationToken.None);

        // Several reports flow from the 5-tick-per-step interpolation plus the
        // post-step boundary report. At least one strictly-intermediate fraction
        // must land between 0 and 1 exclusive to prove we didn't just jump.
        Assert.That(reports, Is.Not.Empty);
        Assert.That(reports.Any(r => r > 0 && r < 1), Is.True);
        Assert.That(reports[^1], Is.EqualTo(1.0).Within(0.0001));
    }

    [Test]
    public async Task Execute_failureInjectedOnNamedStep_failsExactlyThere()
    {
        var steps = new[] { Step("a"), Step("target"), Step("c") };
        var executor = new SimulatedStepExecutor(steps, PreviewSpeed.Fast, failAtStep: "target");

        var result = await executor.ExecuteAsync(BuildContext(), progress: null, CancellationToken.None);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Does.Contain("target"));
        Assert.That(result.Steps, Has.Count.EqualTo(2));
        Assert.That(result.Steps[0].Outcome, Is.EqualTo(StepOutcome.Succeeded));
        Assert.That(result.Steps[1].Outcome, Is.EqualTo(StepOutcome.Failed));
        Assert.That(result.Steps[1].Name, Is.EqualTo("target"));
    }

    [Test]
    public void Construct_unknownFailStep_throwsAtConstruction()
    {
        var steps = new[] { Step("a"), Step("b") };
        var ex = Assert.Throws<InvalidOperationException>(
            () => new SimulatedStepExecutor(steps, PreviewSpeed.Fast, failAtStep: "nope"));
        Assert.That(ex!.Message, Does.Contain("nope"));
        Assert.That(ex.Message, Does.Contain("a"));
        Assert.That(ex.Message, Does.Contain("b"));
    }

    [Test]
    public async Task Execute_respectsCancellation()
    {
        var steps = new[] { Step("a"), Step("b"), Step("c") };
        var executor = new SimulatedStepExecutor(steps, PreviewSpeed.Slow, failAtStep: null);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var result = await executor.ExecuteAsync(BuildContext(), progress: null, cts.Token);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Does.Contain("cancel").IgnoreCase);
    }

    [Test]
    public async Task Execute_emptyStepList_succeedsWithNoAudit()
    {
        var executor = new SimulatedStepExecutor([], PreviewSpeed.Fast, failAtStep: null);
        var result = await executor.ExecuteAsync(BuildContext(), progress: null, CancellationToken.None);
        Assert.That(result.Success, Is.True);
        Assert.That(result.Steps, Is.Empty);
    }

    private sealed class FakeStep : IInstallStepExecution
    {
        public FakeStep(string name, InstallStage stage, int weight)
        {
            Name = name;
            Stage = stage;
            Weight = weight;
        }

        public string Name { get; }
        public InstallStage Stage { get; }
        public int Weight { get; }

        public Task<StepResult> ExecuteAsync(InstallContext context, IStepProgress progress, CancellationToken cancellationToken)
            => Task.FromResult(StepResult.Ok);
    }

    /// <summary>Minimal <see cref="IInstellaLogger"/> for tests — swallows writes.</summary>
    private sealed class CapturingLogger : IInstellaLogger
    {
        public bool IsEnabled(InstellaLogLevel level) => true;
        public void Trace(string message) { }
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
        public IDisposable Scope(string segment) => NullScope.Instance;

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
