using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Installation;
using Instella.Core.Logging;
using Instella.Core.Manifest;
using Instella.Core.Platform;
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Runtime.Installation;
using NUnit.Framework;

namespace Instella.Installer.Testing.Tests;

[TestFixture]
public class InstellaTestHarnessTests
{
    [Test]
    public void Build_with_defaults_yields_populated_context()
    {
        var harness = InstellaTestHarness.Create().Build();

        Assert.That(harness.Context.AppName, Is.EqualTo("FakeApp"));
        Assert.That(harness.Context.AppId, Is.EqualTo("com.instella.tests.fake"));
        Assert.That(harness.Context.AppVersion, Is.EqualTo(new Version(1, 0, 0)));
        Assert.That(harness.Context.Mode, Is.EqualTo(InstellaMode()));
        Assert.That(harness.Context.Scope, Is.EqualTo(InstallationScope.PerUser));
        Assert.That(harness.Context.FileSystem, Is.SameAs(harness.FileSystem));
        Assert.That(harness.Context.Platform, Is.SameAs(harness.PlatformServices));
        Assert.That(harness.Context.Log, Is.SameAs(harness.Logger));
    }

    [Test]
    public void Builder_chain_sets_all_fields()
    {
        var sink = new RecordingSink();
        var harness = InstellaTestHarness.Create()
            .WithAppId("com.example")
            .WithAppName("Example")
            .WithVersion(new Version(2, 3, 4))
            .WithMode(InstallerMode.Update)
            .WithInstallPath("/opt/example")
            .WithCliArgs("--silent", "--path", "/opt/example")
            .WithScope(InstallationScope.SystemWide)
            .WithElevation(ElevationMode.SystemWide)
            .WithServerUrl("https://u.example.test")
            .WithLogSink(sink)
            .Build();

        Assert.That(harness.Context.AppId, Is.EqualTo("com.example"));
        Assert.That(harness.Context.AppName, Is.EqualTo("Example"));
        Assert.That(harness.Context.AppVersion, Is.EqualTo(new Version(2, 3, 4)));
        Assert.That(harness.Context.Mode, Is.EqualTo(InstallerMode.Update));
        Assert.That(harness.Context.InstallPath, Is.EqualTo("/opt/example"));
        Assert.That(harness.Context.Scope, Is.EqualTo(InstallationScope.SystemWide));
        Assert.That(harness.Context.Options.Elevation, Is.EqualTo(ElevationMode.SystemWide));
        Assert.That(harness.Context.Manifest.ServerUrl, Is.EqualTo("https://u.example.test"));
        Assert.That(harness.LogSink, Is.SameAs(sink));
    }

    [Test]
    public async Task RunStepAsync_executes_a_single_step_and_returns_result()
    {
        var harness = InstellaTestHarness.Create().Build();

        var result = await harness.RunStepAsync(new PassingStep());

        Assert.That(result.Success, Is.True);
        Assert.That(result.Steps.Count, Is.EqualTo(1));
        Assert.That(result.Steps[0].Name, Is.EqualTo("passing"));
        Assert.That(result.Steps[0].Outcome, Is.EqualTo(StepOutcome.Succeeded));
    }

    [Test]
    public async Task RunStepAsync_captures_step_log_output()
    {
        var harness = InstellaTestHarness.Create().Build();
        await harness.RunStepAsync(new LoggingStep());

        var messages = harness.LogSink.Entries.Select(e => e.Message).ToArray();
        Assert.That(messages.Any(m => m.Contains("[step:logging] inside step")), Is.True,
            $"expected a scoped 'inside step' message. actual: {string.Join(" | ", messages)}");
    }

    [Test]
    public async Task RunStepAsync_failing_step_returns_failed_result_with_error()
    {
        var harness = InstellaTestHarness.Create().Build();

        var result = await harness.RunStepAsync(new FailingStep("boom"));

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Is.EqualTo("boom"));
    }

    [Test]
    public async Task RunStepAsync_routes_registry_writes_through_fake_registry()
    {
        var harness = InstellaTestHarness.Create().Build();

        await harness.RunStepAsync(new RegistryStep(
            RegistryHive.CurrentUser, "Software\\Harness", "Enabled",
            InstellaRegistryValueKind.DWord, 1));

        Assert.That(harness.Registry.Get(RegistryHive.CurrentUser, "Software\\Harness", "Enabled"), Is.EqualTo(1));
        Assert.That(harness.Registry.Snapshot().Count, Is.EqualTo(1));
    }

    [Test]
    public async Task RunInstallAsync_executes_all_supplied_steps()
    {
        var harness = InstellaTestHarness.Create().Build();

        var result = await harness.RunInstallAsync(new IInstallStepExecution[]
        {
            new PassingStep("a"),
            new PassingStep("b"),
            new PassingStep("c"),
        });

        Assert.That(result.Success, Is.True);
        Assert.That(result.Steps.Count, Is.EqualTo(3));
        Assert.That(result.Steps.Select(s => s.Name).ToArray(), Is.EqualTo(new[] { "a", "b", "c" }));
    }

    [Test]
    public void RunFullAsync_without_installer_throws()
    {
        var harness = InstellaTestHarness.Create().Build();
        Assert.ThrowsAsync<InvalidOperationException>(() => harness.RunFullAsync());
    }

    [Test]
    public async Task RunFullAsync_invokes_installer_with_cli_args()
    {
        var installer = new CapturingInstaller();
        var harness = InstellaTestHarness.Create()
            .WithCliArgs("--silent", "--path", "C:\\install")
            .WithInstaller(installer)
            .Build();

        var exit = await harness.RunFullAsync();

        Assert.That(exit, Is.EqualTo(42));
        Assert.That(installer.CapturedArgs, Is.EqualTo(new[] { "--silent", "--path", "C:\\install" }));
    }

    [Test]
    public void WithPageState_exposes_seeded_values_through_PageStates()
    {
        var harness = InstellaTestHarness.Create()
            .WithPageState("welcome", state =>
            {
                state.Set("agree", true);
                state.Set("userName", "Alice");
            })
            .Build();

        Assert.That(harness.PageStates.ContainsKey("welcome"), Is.True);
        Assert.That(harness.PageStates["welcome"].Bool("agree"), Is.True);
        Assert.That(harness.PageStates["welcome"].Text("userName"), Is.EqualTo("Alice"));
    }

    [Test]
    public async Task DisposeAsync_releases_payload_archive()
    {
        var harness = InstellaTestHarness.Create().Build();
        var stream = new MemoryStream(new byte[] { 1, 2, 3 });
        harness.Context.PayloadArchive = stream;

        await harness.DisposeAsync();

        Assert.That(harness.Context.PayloadArchive, Is.Null);
        Assert.Throws<ObjectDisposedException>(() => _ = stream.Length);
    }

    private static InstallerMode InstellaMode() => InstallerMode.FirstInstall;

    private sealed class PassingStep : IInstallStepExecution
    {
        public PassingStep(string name = "passing") => Name = name;
        public string Name { get; }
        public InstallStage Stage => InstallStage.Register;
        public Task<StepResult> ExecuteAsync(InstallContext context, IStepProgress progress, CancellationToken cancellationToken)
            => Task.FromResult(StepResult.Ok);
    }

    private sealed class LoggingStep : IInstallStepExecution
    {
        public string Name => "logging";
        public InstallStage Stage => InstallStage.Register;
        public Task<StepResult> ExecuteAsync(InstallContext context, IStepProgress progress, CancellationToken cancellationToken)
        {
            context.Log.Info("inside step");
            return Task.FromResult(StepResult.Ok);
        }
    }

    private sealed class FailingStep : IInstallStepExecution
    {
        private readonly string _reason;
        public FailingStep(string reason) => _reason = reason;
        public string Name => "failing";
        public InstallStage Stage => InstallStage.Register;
        public Task<StepResult> ExecuteAsync(InstallContext context, IStepProgress progress, CancellationToken cancellationToken)
            => Task.FromResult(StepResult.Fail(_reason));
    }

    private sealed class RegistryStep : IInstallStepExecution
    {
        private readonly RegistryHive _hive;
        private readonly string _key;
        private readonly string _valueName;
        private readonly InstellaRegistryValueKind _kind;
        private readonly object _value;

        public RegistryStep(RegistryHive hive, string key, string valueName, InstellaRegistryValueKind kind, object value)
        {
            _hive = hive;
            _key = key;
            _valueName = valueName;
            _kind = kind;
            _value = value;
        }

        public string Name => "registry";
        public InstallStage Stage => InstallStage.Register;

        public async Task<StepResult> ExecuteAsync(InstallContext context, IStepProgress progress, CancellationToken cancellationToken)
        {
            var ok = await context.Platform.WriteRegistryValueAsync(
                _hive, _key, _valueName, _kind, _value,
                perUser: context.Scope == InstallationScope.PerUser, cancellationToken);
            return ok.Success ? StepResult.Ok : StepResult.Fail($"registry write failed: {ok.Error}");
        }
    }

    private sealed class CapturingInstaller : IInstellaInstaller
    {
        public string[]? CapturedArgs { get; private set; }

        public Task<int> RunAsync(string[] args, CancellationToken ct = default)
        {
            CapturedArgs = args;
            return Task.FromResult(42);
        }
    }
}
