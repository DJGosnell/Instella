using Instella.Core.Platform;
using NUnit.Framework;

namespace Instella.Core.Tests;

/// <summary>
/// The service-manager seam's platform-neutral parts: the quoted binary path, the Win32 error
/// classification, and the off-Windows implementation that fails every call without throwing.
/// </summary>
[TestFixture]
internal class ServiceManagerTests
{
    [Test]
    public void BinaryPath_QuotesAnExecutableWithSpaces()
    {
        Assert.That(ServiceBinaryPath.Build(@"C:\Program Files\My App\svc.exe", []),
            Is.EqualTo("\"C:\\Program Files\\My App\\svc.exe\""));
    }

    [Test]
    public void BinaryPath_QuotesTheExecutableEvenWithoutSpaces()
    {
        Assert.That(ServiceBinaryPath.Build(@"C:\Apps\svc.exe", []), Is.EqualTo("\"C:\\Apps\\svc.exe\""));
    }

    [Test]
    public void BinaryPath_ArgumentsFollowTheQuotedExecutable_QuotedLikeACommandLine()
    {
        Assert.That(ServiceBinaryPath.Build(@"C:\Program Files\App\svc.exe", ["--port", "80", "a b"]),
            Is.EqualTo("\"C:\\Program Files\\App\\svc.exe\" --port 80 \"a b\""));
    }

    [Test]
    public void BinaryPath_RefusesAnExecutableContainingAQuote()
    {
        Assert.Throws<ArgumentException>(() => ServiceBinaryPath.Build("C:\\a\" b\\svc.exe", []));
    }

    [TestCase(1060, ServiceFailure.NotFound)]          // ERROR_SERVICE_DOES_NOT_EXIST
    [TestCase(5, ServiceFailure.AccessDenied)]         // ERROR_ACCESS_DENIED
    [TestCase(1072, ServiceFailure.MarkedForDeletion)] // ERROR_SERVICE_MARKED_FOR_DELETE
    [TestCase(1073, ServiceFailure.AlreadyExists)]     // ERROR_SERVICE_EXISTS
    [TestCase(1053, ServiceFailure.Timeout)]           // ERROR_SERVICE_REQUEST_TIMEOUT
    [TestCase(1058, ServiceFailure.Failed)]            // ERROR_SERVICE_DISABLED
    public void Win32Errors_AreToldApart(int win32Error, ServiceFailure expected)
    {
        Assert.That(ServiceErrors.Classify(win32Error), Is.EqualTo(expected));
    }

    [TestCase(TargetPlatform.Linux)]
    [TestCase(TargetPlatform.MacOS)]
    public async Task OffWindows_EveryOperationFails_NamingThePlatform(TargetPlatform platform)
    {
        var services = ServiceManagerFactory.Create(platform);
        var config = new ServiceConfiguration
        {
            Name = "svc",
            DisplayName = "Service",
            ExecutablePath = "/opt/app/svc",
        };
        var ct = CancellationToken.None;
        var name = platform.ToString();

        var results = new List<(string Op, PlatformResult Outcome, ServiceFailure Failure)>();
        var r1 = await services.CreateAsync(config, null, ct);
        results.Add(("create", r1.Outcome, r1.Failure));
        var r2 = await services.ChangeConfigurationAsync(config, null, ct);
        results.Add(("config", r2.Outcome, r2.Failure));
        var r3 = await services.QueryConfigurationAsync("svc", ct);
        results.Add(("query-config", r3.Outcome, r3.Failure));
        var r4 = await services.QueryStatusAsync("svc", ct);
        results.Add(("query-status", r4.Outcome, r4.Failure));
        var r5 = await services.StartAsync("svc", TimeSpan.FromSeconds(1), ct);
        results.Add(("start", r5.Outcome, r5.Failure));
        var r6 = await services.StopAsync("svc", TimeSpan.FromSeconds(1), ct);
        results.Add(("stop", r6.Outcome, r6.Failure));
        var r7 = await services.DeleteAsync("svc", ct);
        results.Add(("delete", r7.Outcome, r7.Failure));

        Assert.Multiple(() =>
        {
            foreach (var (op, outcome, failure) in results)
            {
                Assert.That(outcome.Success, Is.False, op);
                Assert.That(outcome.Error, Does.Contain(name), op);
                Assert.That(failure, Is.EqualTo(ServiceFailure.NotSupported), op);
            }
            Assert.That(r3.Value, Is.Null);
            Assert.That(r4.Value, Is.Null);
        });
    }

    [Test]
    public async Task OffWindows_ThePasswordBufferIsZeroedAnyway()
    {
        var services = ServiceManagerFactory.Create(TargetPlatform.Linux);
        var password = "s3cret!".ToCharArray();
        await services.CreateAsync(new ServiceConfiguration
        {
            Name = "svc",
            DisplayName = "Service",
            ExecutablePath = "/opt/app/svc",
            AccountName = @".\svcuser",
        }, password, CancellationToken.None);

        Assert.That(password, Is.All.EqualTo('\0'));
    }
}

/// <summary>
/// The real SCM, read-only and without elevation: built-in services are queried, never changed.
/// Exercises the native QUERY_SERVICE_CONFIG / SERVICE_STATUS_PROCESS layouts on the host.
/// </summary>
[TestFixture]
[Platform("Win")]
public class WindowsServiceManagerTests
{
    private static IServiceManager Services => ServiceManagerFactory.Create(TargetPlatform.Windows);

    [Test]
    public async Task AMissingService_IsNotFound_NotAccessDenied()
    {
        var result = await Services.QueryStatusAsync("Instella-NoSuchService-" + Guid.NewGuid().ToString("N"), CancellationToken.None);

        Assert.That(result.Outcome.Success, Is.False);
        Assert.That(result.Failure, Is.EqualTo(ServiceFailure.NotFound));
        Assert.That(result.NativeError, Is.EqualTo(1060));
    }

    [Test]
    public async Task ABuiltInService_ReadsBackItsConfiguration()
    {
        var result = await Services.QueryConfigurationAsync("RpcSs", CancellationToken.None);

        Assert.That(result.Outcome.Success, Is.True, result.Outcome.Error);
        var config = result.Value!;
        Assert.Multiple(() =>
        {
            Assert.That(config.Name, Is.EqualTo("RpcSs").IgnoreCase);
            Assert.That(config.DisplayName, Is.Not.Empty);
            Assert.That(config.Description, Is.Not.Null.And.Not.Empty);
            Assert.That(config.BinaryPath, Does.Contain("svchost.exe").IgnoreCase);
            Assert.That(config.StartType, Is.EqualTo(ServiceStartType.Automatic));
            Assert.That(config.AccountName, Is.EqualTo(@"NT AUTHORITY\NetworkService").IgnoreCase);
            Assert.That(config.Dependencies, Is.SupersetOf(new[] { "RpcEptMapper", "DcomLaunch" }).Using((IEqualityComparer<string>)StringComparer.OrdinalIgnoreCase));
        });
    }

    [Test]
    public async Task ARunningBuiltInService_ReportsRunningAndItsProcessId()
    {
        var result = await Services.QueryStatusAsync("RpcSs", CancellationToken.None);

        Assert.That(result.Outcome.Success, Is.True, result.Outcome.Error);
        Assert.That(result.Value!.State, Is.EqualTo(ServiceState.Running));
        Assert.That(result.Value.ProcessId, Is.GreaterThan(0));
    }
}

/// <summary>
/// Creates, configures, starts, stops and deletes a throwaway service built from
/// <c>Instella.TestServiceHost</c>, copied into a folder whose path has spaces. Needs an elevated
/// Windows session; run it by name.
/// </summary>
[TestFixture]
[Explicit("creates a real Windows service; needs an elevated session")]
[Platform("Win")]
public class WindowsServiceManagerElevatedTests
{
    [Test]
    public async Task AThrowawayService_GoesThroughItsWholeLife()
    {
        if (!IsElevated()) Assert.Ignore("needs an elevated session");

        var source = AppContext.BaseDirectory;
        var folder = Path.Combine(Path.GetTempPath(), "Instella Service Test " + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        foreach (var file in Directory.GetFiles(source, "Instella.TestServiceHost.*"))
            File.Copy(file, Path.Combine(folder, Path.GetFileName(file)));
        var exe = Path.Combine(folder, "Instella.TestServiceHost.exe");

        var name = "InstellaTest" + Guid.NewGuid().ToString("N")[..8];
        var services = ServiceManagerFactory.Create(TargetPlatform.Windows);
        var ct = CancellationToken.None;
        var timeout = TimeSpan.FromSeconds(30);

        var config = new ServiceConfiguration
        {
            Name = name,
            DisplayName = "Instella test service",
            Description = "Throwaway service created by Instella's tests.",
            ExecutablePath = exe,
            Arguments = ["--mode", "a b"],
            StartType = ServiceStartType.Manual,
        };

        try
        {
            var created = await services.CreateAsync(config, null, ct);
            Assert.That(created.Outcome.Success, Is.True, created.Outcome.Error);

            var again = await services.CreateAsync(config, null, ct);
            Assert.That(again.Failure, Is.EqualTo(ServiceFailure.AlreadyExists));

            var read = (await services.QueryConfigurationAsync(name, ct)).Value!;
            Assert.That(read.BinaryPath, Is.EqualTo($"\"{exe}\" --mode \"a b\""));
            Assert.That(read.BinaryPath, Does.StartWith("\""));
            Assert.That(read.StartType, Is.EqualTo(ServiceStartType.Manual));
            Assert.That(read.AccountName, Is.EqualTo("LocalSystem").IgnoreCase);

            var changed = await services.ChangeConfigurationAsync(config with
            {
                DisplayName = "Instella test service (changed)",
                Description = "Changed.",
                StartType = ServiceStartType.AutomaticDelayed,
                AccountName = @"NT AUTHORITY\LocalService",
                Dependencies = ["RpcSs"],
                SidType = ServiceSidType.Unrestricted,
                FailureActions = new ServiceFailureActions(TimeSpan.FromDays(1),
                [
                    new ServiceFailureAction(ServiceFailureActionType.Restart, TimeSpan.FromSeconds(5)),
                    new ServiceFailureAction(ServiceFailureActionType.Restart, TimeSpan.FromSeconds(30)),
                    new ServiceFailureAction(ServiceFailureActionType.None, TimeSpan.Zero),
                ]),
            }, null, ct);
            Assert.That(changed.Outcome.Success, Is.True, changed.Outcome.Error);

            read = (await services.QueryConfigurationAsync(name, ct)).Value!;
            Assert.Multiple(() =>
            {
                Assert.That(read.DisplayName, Is.EqualTo("Instella test service (changed)"));
                Assert.That(read.Description, Is.EqualTo("Changed."));
                Assert.That(read.StartType, Is.EqualTo(ServiceStartType.AutomaticDelayed));
                Assert.That(read.AccountName, Is.EqualTo(@"NT AUTHORITY\LocalService").IgnoreCase);
                Assert.That(read.Dependencies, Is.EqualTo(new[] { "RpcSs" }));
                Assert.That(read.SidType, Is.EqualTo(ServiceSidType.Unrestricted));
                Assert.That(read.FailureActions!.ResetPeriod, Is.EqualTo(TimeSpan.FromDays(1)));
                Assert.That(read.FailureActions.Actions.Select(a => a.Type),
                    Is.EqualTo(new[] { ServiceFailureActionType.Restart, ServiceFailureActionType.Restart, ServiceFailureActionType.None }));
                Assert.That(read.FailureActions.Actions[1].Delay, Is.EqualTo(TimeSpan.FromSeconds(30)));
                Assert.That(read.BinaryPath, Is.EqualTo($"\"{exe}\" --mode \"a b\""));
            });

            var started = await services.StartAsync(name, timeout, ct);
            Assert.That(started.Outcome.Success, Is.True, started.Outcome.Error);
            var status = (await services.QueryStatusAsync(name, ct)).Value!;
            Assert.That(status.State, Is.EqualTo(ServiceState.Running));
            Assert.That(status.ProcessId, Is.GreaterThan(0));

            var stopped = await services.StopAsync(name, timeout, ct);
            Assert.That(stopped.Outcome.Success, Is.True, stopped.Outcome.Error);
            Assert.That((await services.QueryStatusAsync(name, ct)).Value!.State, Is.EqualTo(ServiceState.Stopped));

            var deleted = await services.DeleteAsync(name, ct);
            Assert.That(deleted.Outcome.Success, Is.True, deleted.Outcome.Error);
            Assert.That((await services.QueryStatusAsync(name, ct)).Failure, Is.EqualTo(ServiceFailure.NotFound));
        }
        finally
        {
            await services.StopAsync(name, timeout, ct);
            await services.DeleteAsync(name, ct);
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static bool IsElevated()
    {
#pragma warning disable CA1416 // [Platform("Win")]
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
#pragma warning restore CA1416
    }
}
