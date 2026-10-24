using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.FileSystem;
using Instella.Core.Installation;
using Instella.Core.Platform;
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Testing;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Platform;

/// <summary>
/// The harness's in-memory service manager (<see cref="FakePlatformServices.ServiceManager"/>):
/// the state machine, configurable failures and timeouts, deletion semantics, and the public view.
/// </summary>
[TestFixture]
public sealed class FakeServiceManagerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static ServiceConfiguration Config(string name = "Svc", params string[] dependencies) => new()
    {
        Name = name,
        DisplayName = name + " display",
        Description = "A test service",
        ExecutablePath = @"C:\Program Files\App\svc.exe",
        Arguments = ["--run"],
        StartType = ServiceStartType.AutomaticDelayed,
        AccountName = @"NT AUTHORITY\LocalService",
        Dependencies = dependencies,
    };

    [Test]
    public async Task Create_RegistersAStoppedService_WithAQuotedBinaryPath()
    {
        var platform = new FakePlatformServices();
        IServiceManager services = platform.ServiceManager;

        var result = await services.CreateAsync(Config(), null, Ct);

        Assert.That(result.Outcome.Success, Is.True, result.Outcome.Error);
        var svc = platform.ServiceManager.Services.Single();
        Assert.Multiple(() =>
        {
            Assert.That(svc.Name, Is.EqualTo("Svc"));
            Assert.That(svc.DisplayName, Is.EqualTo("Svc display"));
            Assert.That(svc.Description, Is.EqualTo("A test service"));
            Assert.That(svc.BinaryPath, Is.EqualTo("\"C:\\Program Files\\App\\svc.exe\" --run"));
            Assert.That(svc.StartType, Is.EqualTo(ManifestService.AutomaticDelayed));
            Assert.That(svc.AccountName, Is.EqualTo(@"NT AUTHORITY\LocalService"));
            Assert.That(svc.State, Is.EqualTo(FakeServiceState.Stopped));
            Assert.That(svc.ProcessId, Is.EqualTo(0));
            Assert.That(svc.MarkedForDeletion, Is.False);
        });
    }

    [Test]
    public async Task Create_OfAnExistingName_IsAlreadyExists()
    {
        IServiceManager services = new FakePlatformServices().ServiceManager;
        await services.CreateAsync(Config(), null, Ct);

        var again = await services.CreateAsync(Config(), null, Ct);

        Assert.That(again.Outcome.Success, Is.False);
        Assert.That(again.Failure, Is.EqualTo(ServiceFailure.AlreadyExists));
    }

    [Test]
    public async Task QueryAndConfigure_ReadBackWhatWasWritten()
    {
        IServiceManager services = new FakePlatformServices().ServiceManager;
        await services.CreateAsync(Config(), null, Ct);

        var changed = await services.ChangeConfigurationAsync(Config() with
        {
            DisplayName = "Changed",
            StartType = ServiceStartType.Manual,
            SidType = ServiceSidType.Restricted,
            Dependencies = ["RpcSs"],
            FailureActions = new ServiceFailureActions(TimeSpan.FromHours(1),
                [new ServiceFailureAction(ServiceFailureActionType.Restart, TimeSpan.FromSeconds(10))]),
        }, null, Ct);
        var read = await services.QueryConfigurationAsync("svc", Ct);   // names are case-insensitive

        Assert.That(changed.Outcome.Success, Is.True, changed.Outcome.Error);
        Assert.That(read.Outcome.Success, Is.True, read.Outcome.Error);
        var config = read.Value!;
        Assert.Multiple(() =>
        {
            Assert.That(config.DisplayName, Is.EqualTo("Changed"));
            Assert.That(config.StartType, Is.EqualTo(ServiceStartType.Manual));
            Assert.That(config.SidType, Is.EqualTo(ServiceSidType.Restricted));
            Assert.That(config.Dependencies, Is.EqualTo(new[] { "RpcSs" }));
            Assert.That(config.FailureActions!.Actions.Single().Delay, Is.EqualTo(TimeSpan.FromSeconds(10)));
            Assert.That(config.BinaryPath, Is.EqualTo("\"C:\\Program Files\\App\\svc.exe\" --run"));
        });
    }

    [Test]
    public async Task AMissingService_IsNotFound_ForEveryOperation()
    {
        IServiceManager services = new FakePlatformServices().ServiceManager;

        await Assert.MultipleAsync(async () =>
        {
            Assert.That((await services.QueryStatusAsync("nope", Ct)).Failure, Is.EqualTo(ServiceFailure.NotFound));
            Assert.That((await services.QueryConfigurationAsync("nope", Ct)).Failure, Is.EqualTo(ServiceFailure.NotFound));
            Assert.That((await services.ChangeConfigurationAsync(Config("nope"), null, Ct)).Failure, Is.EqualTo(ServiceFailure.NotFound));
            Assert.That((await services.StartAsync("nope", Timeout, Ct)).Failure, Is.EqualTo(ServiceFailure.NotFound));
            Assert.That((await services.StopAsync("nope", Timeout, Ct)).Failure, Is.EqualTo(ServiceFailure.NotFound));
            Assert.That((await services.DeleteAsync("nope", Ct)).Failure, Is.EqualTo(ServiceFailure.NotFound));
        });
    }

    [Test]
    public async Task StartThenStop_WalksTheStateMachine()
    {
        var platform = new FakePlatformServices();
        IServiceManager services = platform.ServiceManager;
        await services.CreateAsync(Config(), null, Ct);

        var started = await services.StartAsync("Svc", Timeout, Ct);
        var running = (await services.QueryStatusAsync("Svc", Ct)).Value!;
        var stopped = await services.StopAsync("Svc", Timeout, Ct);
        var after = (await services.QueryStatusAsync("Svc", Ct)).Value!;

        Assert.That(started.Outcome.Success, Is.True, started.Outcome.Error);
        Assert.That(running.State, Is.EqualTo(ServiceState.Running));
        Assert.That(running.ProcessId, Is.GreaterThan(0));
        Assert.That(stopped.Outcome.Success, Is.True, stopped.Outcome.Error);
        Assert.That(after.State, Is.EqualTo(ServiceState.Stopped));
        Assert.That(after.ProcessId, Is.EqualTo(0));
    }

    [Test]
    public async Task AFailingStart_LeavesTheServiceStopped()
    {
        var platform = new FakePlatformServices();
        IServiceManager services = platform.ServiceManager;
        await services.CreateAsync(Config(), null, Ct);
        platform.ServiceManager.SetStartBehavior("Svc", FakeServiceBehavior.Fail);

        var started = await services.StartAsync("Svc", Timeout, Ct);

        Assert.That(started.Outcome.Success, Is.False);
        Assert.That(started.Failure, Is.EqualTo(ServiceFailure.Failed));
        Assert.That(platform.ServiceManager.Services.Single().State, Is.EqualTo(FakeServiceState.Stopped));
    }

    [Test]
    public async Task AHangingStart_TimesOut_StuckInStartPending()
    {
        var platform = new FakePlatformServices();
        IServiceManager services = platform.ServiceManager;
        await services.CreateAsync(Config(), null, Ct);
        platform.ServiceManager.SetStartBehavior("Svc", FakeServiceBehavior.Hang);

        var started = await services.StartAsync("Svc", Timeout, Ct);

        Assert.That(started.Failure, Is.EqualTo(ServiceFailure.Timeout));
        Assert.That(platform.ServiceManager.Services.Single().State, Is.EqualTo(FakeServiceState.StartPending));
        Assert.That((await services.QueryStatusAsync("Svc", Ct)).Value!.State, Is.EqualTo(ServiceState.StartPending));
    }

    [Test]
    public async Task FailingAndHangingStops_KeepOrStickTheState()
    {
        var platform = new FakePlatformServices();
        IServiceManager services = platform.ServiceManager;
        await services.CreateAsync(Config("A"), null, Ct);
        await services.CreateAsync(Config("B"), null, Ct);
        await services.StartAsync("A", Timeout, Ct);
        await services.StartAsync("B", Timeout, Ct);
        platform.ServiceManager.SetStopBehavior("A", FakeServiceBehavior.Fail);
        platform.ServiceManager.SetStopBehavior("B", FakeServiceBehavior.Hang);

        var a = await services.StopAsync("A", Timeout, Ct);
        var b = await services.StopAsync("B", Timeout, Ct);

        Assert.That(a.Failure, Is.EqualTo(ServiceFailure.Failed));
        Assert.That(b.Failure, Is.EqualTo(ServiceFailure.Timeout));
        Assert.That(platform.ServiceManager.Find("A")!.State, Is.EqualTo(FakeServiceState.Running));
        Assert.That(platform.ServiceManager.Find("B")!.State, Is.EqualTo(FakeServiceState.StopPending));
    }

    [Test]
    public async Task Stop_StopsRunningDependentsFirst()
    {
        var platform = new FakePlatformServices();
        IServiceManager services = platform.ServiceManager;
        await services.CreateAsync(Config("Core"), null, Ct);
        await services.CreateAsync(Config("Worker", "Core"), null, Ct);
        await services.StartAsync("Core", Timeout, Ct);
        await services.StartAsync("Worker", Timeout, Ct);

        var stopped = await services.StopAsync("Core", Timeout, Ct);

        Assert.That(stopped.Outcome.Success, Is.True, stopped.Outcome.Error);
        Assert.That(platform.ServiceManager.Find("Worker")!.State, Is.EqualTo(FakeServiceState.Stopped));
        var stops = platform.ServiceManager.Calls.Where(c => c.StartsWith("stop:", StringComparison.Ordinal)).ToArray();
        Assert.That(stops, Is.EqualTo(new[] { "stop:Worker", "stop:Core" }));
    }

    [Test]
    public async Task DeletingARunningService_MarksItForDeletion_UntilItStops()
    {
        var platform = new FakePlatformServices();
        IServiceManager services = platform.ServiceManager;
        await services.CreateAsync(Config(), null, Ct);
        await services.StartAsync("Svc", Timeout, Ct);

        var deleted = await services.DeleteAsync("Svc", Ct);
        Assert.That(deleted.Outcome.Success, Is.True, deleted.Outcome.Error);
        Assert.That(platform.ServiceManager.Find("Svc")!.MarkedForDeletion, Is.True);

        var recreate = await services.CreateAsync(Config(), null, Ct);
        Assert.That(recreate.Failure, Is.EqualTo(ServiceFailure.MarkedForDeletion));

        await services.StopAsync("Svc", Timeout, Ct);
        Assert.That(platform.ServiceManager.Find("Svc"), Is.Null);
        Assert.That((await services.QueryStatusAsync("Svc", Ct)).Failure, Is.EqualTo(ServiceFailure.NotFound));
    }

    [Test]
    public async Task DeletingAStoppedService_RemovesIt()
    {
        var platform = new FakePlatformServices();
        IServiceManager services = platform.ServiceManager;
        await services.CreateAsync(Config(), null, Ct);

        var deleted = await services.DeleteAsync("Svc", Ct);

        Assert.That(deleted.Outcome.Success, Is.True, deleted.Outcome.Error);
        Assert.That(platform.ServiceManager.Services, Is.Empty);
    }

    [Test]
    public async Task DenyAccess_FailsChangesWithAccessDenied()
    {
        var platform = new FakePlatformServices();
        IServiceManager services = platform.ServiceManager;
        await services.CreateAsync(Config("Existing"), null, Ct);
        platform.ServiceManager.DenyAccess = true;

        await Assert.MultipleAsync(async () =>
        {
            Assert.That((await services.CreateAsync(Config("New"), null, Ct)).Failure, Is.EqualTo(ServiceFailure.AccessDenied));
            Assert.That((await services.ChangeConfigurationAsync(Config("Existing"), null, Ct)).Failure, Is.EqualTo(ServiceFailure.AccessDenied));
            Assert.That((await services.StartAsync("Existing", Timeout, Ct)).Failure, Is.EqualTo(ServiceFailure.AccessDenied));
            Assert.That((await services.StopAsync("Existing", Timeout, Ct)).Failure, Is.EqualTo(ServiceFailure.AccessDenied));
            Assert.That((await services.DeleteAsync("Existing", Ct)).Failure, Is.EqualTo(ServiceFailure.AccessDenied));
        });
        Assert.That(platform.ServiceManager.Services.Select(s => s.Name), Is.EqualTo(new[] { "Existing" }));
    }

    [Test]
    public async Task APassword_IsZeroedAndNeverExposed()
    {
        var platform = new FakePlatformServices();
        IServiceManager services = platform.ServiceManager;
        var password = "s3cret!".ToCharArray();

        await services.CreateAsync(Config() with { AccountName = @".\svcuser" }, password, Ct);

        Assert.That(password, Is.All.EqualTo('\0'));
        var svc = platform.ServiceManager.Services.Single();
        Assert.That(svc.PasswordSupplied, Is.True);
        Assert.That(svc.ToString(), Does.Not.Contain("s3cret!"));
        Assert.That(string.Join("|", platform.ServiceManager.Calls), Does.Not.Contain("s3cret!"));
    }

    [Test]
    public void InstallerServices_CarryTheSeam_DefaultingToTheRealImplementation()
    {
        var platform = new FakePlatformServices();
        var injected = new InstallerServices(platform, new InMemoryFileSystem(), ServiceManager: platform.ServiceManager);
        var defaulted = new InstallerServices(platform, new InMemoryFileSystem());

        Assert.That(injected.ServiceManagerOrDefault, Is.SameAs(platform.ServiceManager));
        Assert.That(defaulted.ServiceManagerOrDefault, Is.Not.Null);
        Assert.That(defaulted.ServiceManagerOrDefault, Is.Not.InstanceOf<FakeServiceManager>());
    }

    [Test]
    public async Task Create_WithASelfDependency_IsRefusedAsOnWindows()
    {
        var platform = new FakePlatformServices();
        IServiceManager services = platform.ServiceManager;
        var config = new ServiceConfiguration
        {
            Name = "Loop",
            DisplayName = "Loop",
            ExecutablePath = @"C:\App\svc.exe",
            Dependencies = new[] { "Loop" },
        };

        var result = await services.CreateAsync(config, null, CancellationToken.None);

        // Windows: ERROR_CIRCULAR_DEPENDENCY (1059). Accepting it lets StopAsync recurse without end.
        Assert.That(result.Outcome.Success, Is.False,
            "a service that depends on itself must be refused; stopping it would overflow the stack");
    }

    [Test]
    public async Task ASelfDependency_IsRefused_LikeTheScm()
    {
        var platform = new FakePlatformServices();
        IServiceManager services = platform.ServiceManager;
        var created = await services.CreateAsync(new ServiceConfiguration
        {
            Name = "Loop",
            DisplayName = "Loop",
            ExecutablePath = @"C:\a\svc.exe",
            Dependencies = new[] { "Loop" },
        }, null, CancellationToken.None);
        Assert.That(created.Outcome.Success, Is.False,
            "the SCM refuses a circular dependency (1059); accepting it lets the fake's recursive stop overflow the stack");
    }

    [Test]
    public async Task AChangeThatClosesADependencyCycle_IsRefused_AndStopStillReturns()
    {
        var platform = new FakePlatformServices();
        IServiceManager services = platform.ServiceManager;
        await services.CreateAsync(Config("A"), null, Ct);
        await services.CreateAsync(Config("B", "a"), null, Ct);

        var changed = await services.ChangeConfigurationAsync(Config("A", "B"), null, Ct);

        Assert.That(changed.Outcome.Success, Is.False);
        Assert.That(changed.NativeError, Is.EqualTo(1059));
        Assert.That(platform.ServiceManager.Find("A")!.Dependencies, Is.Empty);
        await services.StartAsync("A", Timeout, Ct);
        await services.StartAsync("B", Timeout, Ct);
        var stopped = await services.StopAsync("A", Timeout, Ct);
        Assert.That(stopped.Outcome.Success, Is.True, stopped.Outcome.Error);
        Assert.That(platform.ServiceManager.Services.Select(s => s.State), Is.All.EqualTo(FakeServiceState.Stopped));
    }
}
