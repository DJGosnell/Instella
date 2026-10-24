using System.Text.Json;
using Instella.Core.Installation;
using Instella.Core.Platform;
using Instella.Core.Update;
using Instella.Sdk;
using Instella.Sdk.Internal;
using NUnit.Framework;

namespace Instella.Sdk.Tests;

/// <summary>
/// Self-update started from inside a Windows service (<see cref="InstellaClient.StartServiceUpdateAsync"/>):
/// the checks before anything starts, the updater arguments, and the refusal of the desktop calls
/// inside a service. Everything the OS answers goes through <see cref="IServiceUpdateSystem"/>.
/// </summary>
[TestFixture]
public class ServiceUpdateTests
{
    private const int UpdaterPid = 9876;
    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Directory.CreateTempSubdirectory("instella-sdk-svc-").FullName;
        File.WriteAllBytes(Path.Combine(_root, InstellaOwnedPaths.StubFileName), []);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_root, recursive: true);

    [Test]
    public async Task Refuses_WhenTheManifestHasNoServicesField()
    {
        // An installer older than service support wrote no `services`: its stub ignores
        // --service-update and would not stop the service.
        var system = new FakeSystem();

        var result = await ServiceUpdateLauncher.StartAsync(Update(), Info(services: null), system, CancellationToken.None);

        AssertRefused(result, system, "services");
    }

    [Test]
    public async Task Refuses_WhenNoServiceIsRecorded()
    {
        var system = new FakeSystem();

        var result = await ServiceUpdateLauncher.StartAsync(Update(), Info(services: []), system, CancellationToken.None);

        AssertRefused(result, system, "service");
    }

    [Test]
    public async Task Refuses_WhenNotElevated()
    {
        var system = new FakeSystem { IsElevated = false };

        var result = await ServiceUpdateLauncher.StartAsync(Update(), Info(), system, CancellationToken.None);

        AssertRefused(result, system, "elevated");
    }

    [Test]
    public async Task Refuses_WhenTheInstallRootCannotBeWritten_NamingIt()
    {
        var system = new FakeSystem { WriteError = "Access to the path is denied." };

        var result = await ServiceUpdateLauncher.StartAsync(Update(), Info(), system, CancellationToken.None);

        AssertRefused(result, system, "cannot write");
        Assert.That(result.RefusalReason, Does.Contain(_root));
        Assert.That(system.WriteChecks, Does.Contain(_root));
    }

    [Test]
    public async Task Refuses_WhenARecordedServiceCannotBeStoppedAndStarted_NamingIt()
    {
        var system = new FakeSystem();
        system.ServiceErrors["App.Worker"] = "access denied";

        var result = await ServiceUpdateLauncher.StartAsync(Update(), Info(services: ["App.Svc", "App.Worker"]), system, CancellationToken.None);

        AssertRefused(result, system, "App.Worker");
        Assert.That(system.ServiceChecks, Is.EquivalentTo(new[] { "App.Svc", "App.Worker" }), "every recorded service is checked");
    }

    [Test]
    public async Task Refuses_InAJobThatForbidsBreakaway()
    {
        // The updater would be killed with the service's job when the service stops.
        var system = new FakeSystem { Job = JobBreakaway.Forbidden };

        var result = await ServiceUpdateLauncher.StartAsync(Update(), Info(), system, CancellationToken.None);

        AssertRefused(result, system, "job");
    }

    [Test]
    public async Task Refuses_WhenTheStubIsMissing()
    {
        File.Delete(Path.Combine(_root, InstellaOwnedPaths.StubFileName));
        var system = new FakeSystem();

        var result = await ServiceUpdateLauncher.StartAsync(Update(), Info(), system, CancellationToken.None);

        AssertRefused(result, system, InstellaOwnedPaths.StubFileName);
    }

    [Test]
    public async Task Starts_TheStubSilently_WithServiceUpdate_WithoutParentPid_OrRestart()
    {
        var system = new FakeSystem();
        var update = Update();

        var result = await ServiceUpdateLauncher.StartAsync(update, Info(), system, CancellationToken.None);

        Assert.That(result.IsStarted, Is.True, result.RefusalReason);
        Assert.That(result.ProcessId, Is.EqualTo(UpdaterPid));
        Assert.That(result.RefusalReason, Is.Null);
        var started = system.Started.Single();
        Assert.That(started.Executable, Is.EqualTo(Path.Combine(_root, InstellaOwnedPaths.StubFileName)));
        Assert.That(started.WorkingDirectory, Is.EqualTo(_root));
        Assert.That(started.Breakaway, Is.False, "not in a job: nothing to break away from");
        Assert.That(started.Arguments, Does.Contain("--service-update").And.Contain("--silent").And.Not.Contain("--parent-pid"));

        var args = UpdaterArgs.Parse(started.Arguments);
        Assert.That(args, Is.Not.Null);
        Assert.That(args!.ServiceUpdate, Is.True);
        Assert.That(args.Silent, Is.True, "session 0 has no desktop");
        Assert.That(args.ParentPid, Is.Null, "the updater stops the service through the SCM, it does not wait for this process");
        Assert.That(args.Restart, Is.False, "the updater starts the stopped services again; it never launches the app in session 0");
        Assert.That(args.AppPath, Is.EqualTo(_root));
        Assert.That(args.AppExecutable, Is.EqualTo("Svc.exe"));
        Assert.That(args.FromVersion, Is.EqualTo(new Version(1, 0, 0)));
        Assert.That(args.ToVersion, Is.EqualTo(new Version(1, 1, 0)));
        Assert.That(args.Channel, Is.EqualTo("beta"));
        Assert.That(args.UsePatch, Is.True);
        Assert.That(args.PatchSha256, Is.EqualTo(update.PatchSha256));
        Assert.That(ServiceUpdateLauncher.BuildArgs(update, Info()), Is.EqualTo(args), "BuildArgs is what gets started");
    }

    [Test]
    public async Task Starts_WithBreakaway_InAJobThatAllowsIt()
    {
        var system = new FakeSystem { Job = JobBreakaway.Allowed };

        var result = await ServiceUpdateLauncher.StartAsync(Update(), Info(), system, CancellationToken.None);

        Assert.That(result.IsStarted, Is.True, result.RefusalReason);
        Assert.That(system.Started.Single().Breakaway, Is.True);
    }

    [Test]
    public async Task IsRunningAsService_ComparesTheServicesProcessIds()
    {
        var system = new FakeSystem { CurrentProcessId = 100 };
        system.ServicePids["App.Worker"] = 100;

        Assert.That(await ServiceUpdateLauncher.IsRunningAsServiceAsync(Info(services: ["App.Svc", "App.Worker"]), system, CancellationToken.None), Is.True);
        Assert.That(await ServiceUpdateLauncher.IsRunningAsServiceAsync(Info(services: ["App.Svc"]), system, CancellationToken.None), Is.False);
        Assert.That(await ServiceUpdateLauncher.IsRunningAsServiceAsync(Info(services: null), system, CancellationToken.None), Is.False);
    }

    [Test]
    public void DesktopUpdater_InsideAService_ThrowsPointingToStartServiceUpdateAsync()
    {
        var system = new FakeSystem { CurrentProcessId = 100 };
        system.ServicePids["App.Svc"] = 100;

        var ex = Assert.ThrowsAsync<InvalidOperationException>(() =>
            UpdaterLauncher.StartAsync(Update(), new UpdateOptions(), Info(), system, CancellationToken.None));

        Assert.That(ex!.Message, Does.Contain(nameof(InstellaClient.StartServiceUpdateAsync)));
        Assert.That(system.Started, Is.Empty);
    }

    [Test]
    public void ManifestLoader_ReadsTheRecordedServiceNames_AndNullWithoutTheField()
    {
        WriteManifest([new ManifestService("App.Svc", "Svc.exe", null, ManifestService.Automatic, true)]);
        Assert.That(ManifestLoader.TryLoad(_root, out var reason)!.ServiceNames, Is.EqualTo(new[] { "App.Svc" }), reason);

        WriteManifest(null);
        Assert.That(ManifestLoader.TryLoad(_root, out reason)!.ServiceNames, Is.Null, reason);
    }

    [Test]
    public async Task StartServiceUpdateAsync_IsPublic_AndRefusesWhenNotInstalled()
    {
        // The test host has no installed manifest: a refusal with the reason, not an exception.
        Func<UpdateInfo, CancellationToken, Task<ServiceUpdateStartResult>> call = InstellaClient.StartServiceUpdateAsync;

        var result = await call(Update(), CancellationToken.None);

        Assert.That(result.IsStarted, Is.False);
        Assert.That(result.ProcessId, Is.Null);
        Assert.That(result.RefusalReason, Does.Contain("not installed by Instella"));
        Assert.ThrowsAsync<ArgumentNullException>(() => InstellaClient.StartServiceUpdateAsync(null!));
    }

    // ---- helpers ----

    private static void AssertRefused(ServiceUpdateStartResult result, FakeSystem system, string reasonContains)
    {
        Assert.That(result.IsStarted, Is.False);
        Assert.That(result.ProcessId, Is.Null);
        Assert.That(result.RefusalReason, Does.Contain(reasonContains).IgnoreCase);
        Assert.That(system.Started, Is.Empty, "nothing is started after a refusal");
    }

    private static UpdateInfo Update() => new()
    {
        Version = new Version(1, 1), Changelog = "", FullSize = 1, PatchAvailable = true,
        PatchSha256 = new string('c', 64), Channel = "beta",
    };

    /// <summary>A machine installation recording one service, App.Svc.</summary>
    private InstellaInfo Info() => Info(services: ["App.Svc"]);

    /// <summary>Null <paramref name="services"/>: the manifest has no <c>services</c> field.</summary>
    private InstellaInfo Info(IReadOnlyList<string>? services) => new()
    {
        AppName = "Svc", AppId = "com.svc", Version = new Version(1, 0), InstallRoot = _root, Channel = "beta",
        ExecutableName = "Svc.exe", ServiceNames = services,
    };

    private void WriteManifest(IReadOnlyList<ManifestService>? services)
    {
        var manifest = new InstalledManifest
        {
            AppName = "Svc", AppId = "com.svc", Version = new Version(1, 0), InstallDirectory = _root,
            ExecutableName = "Svc.exe", InstalledAt = DateTime.UtcNow, InstalledPerUser = false,
            Platform = TargetPlatform.Windows, ServerUrl = "https://updates.example.com", Files = [], Services = services,
        };
        File.WriteAllBytes(Path.Combine(_root, InstellaOwnedPaths.InstalledManifest),
            JsonSerializer.SerializeToUtf8Bytes(manifest, InstalledManifestJsonContext.Default.InstalledManifest));
    }

    private sealed class FakeSystem : IServiceUpdateSystem
    {
        public int CurrentProcessId { get; init; } = 4242;
        public bool IsElevated { get; init; } = true;
        public string? WriteError { get; init; }
        public JobBreakaway Job { get; init; } = JobBreakaway.NotInJob;
        public Dictionary<string, string> ServiceErrors { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> ServicePids { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> WriteChecks { get; } = [];
        public List<string> ServiceChecks { get; } = [];
        public List<(string Executable, IReadOnlyList<string> Arguments, string WorkingDirectory, bool Breakaway)> Started { get; } = [];

        public string? CheckCanWrite(string directory)
        {
            WriteChecks.Add(directory);
            return WriteError;
        }

        public Task<string?> CheckCanStopAndStartAsync(string serviceName, CancellationToken ct)
        {
            ServiceChecks.Add(serviceName);
            return Task.FromResult(ServiceErrors.GetValueOrDefault(serviceName));
        }

        public Task<int?> GetServiceProcessIdAsync(string serviceName, CancellationToken ct) =>
            Task.FromResult(ServicePids.TryGetValue(serviceName, out var pid) ? pid : (int?)null);

        public JobBreakaway GetJobBreakaway() => Job;

        public int StartDetached(string executable, IReadOnlyList<string> arguments, string workingDirectory, bool breakawayFromJob)
        {
            Started.Add((executable, arguments, workingDirectory, breakawayFromJob));
            return UpdaterPid;
        }
    }
}
