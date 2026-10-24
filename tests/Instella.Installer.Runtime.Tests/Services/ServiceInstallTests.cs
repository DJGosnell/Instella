using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.FileSystem;
using Instella.Core.Installation;
using Instella.Core.Manifest;
using Instella.Core.Platform;
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Testing;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Services;

/// <summary>
/// Windows services declared with <c>OnWindows(w =&gt; w.AddService(...))</c>, installed through the
/// harness on the fake service manager: first install, upgrade, repair, a dropped service, start
/// failures and their rollback, <c>StartFailureIsWarning</c>, a foreign service of the same name, a
/// missing executable and an install folder non-administrators can write.
/// </summary>
[TestFixture, Platform("Win")]
[NonParallelizable]
public class ServiceInstallTests
{
    private const string AppId = "com.example.app";
    private InMemoryFileSystem _fs = null!;
    private FakePlatformServices _platform = null!;
    private readonly List<InstellaTestHarness> _harnesses = [];
    private string _root = null!;
    private string _installPath = null!;

    [SetUp]
    public void SetUp()
    {
        _fs = new InMemoryFileSystem();
        _platform = new FakePlatformServices();
        _root = Path.Combine(Path.GetTempPath(), "instella-services", Guid.NewGuid().ToString("N"));
        _installPath = Path.Combine(_root, "App");
    }

    [TearDown]
    public async Task TearDown()
    {
        foreach (var h in _harnesses) await h.DisposeAsync();
        _harnesses.Clear();
    }

    private FakeServiceManager Scm => _platform.ServiceManager;

    private static Dictionary<string, string> Files(string version) => new()
    {
        ["App.exe"] = $"app {version}",
        ["bin/App.Worker.exe"] = $"worker {version}",
    };

    private InstellaTestHarness Installer(
        string version,
        Action<WindowsBuilder> windows,
        Dictionary<string, string>? files = null,
        Action<InstallerBuilder>? more = null,
        string appId = AppId)
    {
        var builder = InstellaInstaller.Create()
            .WithApp("App", appId, Version.Parse(version))
            .WithExecutableName("App.exe")
            .WithElevation(ElevationMode.SystemWide)
            .OnWindows(windows);
        more?.Invoke(builder);
        var harness = InstellaTestHarness.Create()
            .WithInstaller(builder.Build())
            .WithFileSystem(_fs)
            .WithPlatformServices(_platform)
            .WithElevationService(new FakeElevationService(isElevated: true))
            .WithPayload(files ?? Files(version))
            .Build();
        _harnesses.Add(harness);
        return harness;
    }

    private Task<int> Install(InstellaTestHarness harness, string? path = null) =>
        harness.RunFullWithArgsAsync(["--install", "--silent", "--path", path ?? _installPath]);

    private string? Live(string rel, string? root = null)
    {
        var path = Path.GetFullPath(Path.Combine(root ?? _installPath, rel));
        return _fs.Snapshot().TryGetValue(path, out var bytes) ? Encoding.UTF8.GetString(bytes) : null;
    }

    private Task<InstalledManifest?> Manifest() => new InstallManifestWriter(_fs).ReadAsync(_installPath, CancellationToken.None);

    private static string Log(InstellaTestHarness h) => string.Join("\n", h.LogSink.Entries.Select(e => e.Message));

    private string Binary(string rel, params string[] args) =>
        ServiceBinaryPath.Build(Path.Combine(_installPath, rel.Replace('/', Path.DirectorySeparatorChar)), args);

    private Task<ServiceConfigurationInfo> Query(string name) =>
        ((IServiceManager)Scm).QueryConfigurationAsync(name, CancellationToken.None).ContinueWith(t => t.Result.Value!);

    private static void SimpleService(WindowsBuilder w) => w.AddService("App.Svc", s => s.WithArguments("--service"));

    // ---- first install ----

    [Test]
    public async Task FirstInstall_RegistersTheService_StartsIt_AndRecordsItInTheManifest()
    {
        var exit = await Install(Installer("1.0", SimpleService));

        Assert.That(exit, Is.EqualTo(0));
        var svc = Scm.Find("App.Svc")!;
        Assert.That(svc, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(svc.BinaryPath, Is.EqualTo(Binary("App.exe", "--service")), "the default executable is the app's main executable");
            Assert.That(svc.DisplayName, Is.EqualTo("App.Svc"), "the display name defaults to the service name");
            Assert.That(svc.StartType, Is.EqualTo(ManifestService.Automatic));
            Assert.That(svc.AccountName, Is.Null, "LocalSystem is the default account");
            Assert.That(svc.State, Is.EqualTo(FakeServiceState.Running));
            Assert.That(Scm.Calls, Is.EqualTo(new[] { "create:App.Svc", "start:App.Svc" }));
        });

        var recorded = (await Manifest())!.Services!.Single();
        Assert.That(recorded.Name, Is.EqualTo("App.Svc"));
        Assert.That(recorded.ExecutablePath.Replace('\\', '/'), Is.EqualTo("App.exe"));
        Assert.That(recorded.AccountName, Is.Null);
        Assert.That(recorded.StartType, Is.EqualTo(ManifestService.Automatic));
        Assert.That(recorded.CreatedByInstella, Is.True);
    }

    [Test]
    public async Task FirstInstall_WritesEveryDeclaredSetting()
    {
        var exit = await Install(Installer("1.0", w => w.AddService("App.Worker", s => s
            .WithDisplayName("App worker")
            .WithDescription("Does the work")
            .WithExecutable("bin/App.Worker.exe")
            .WithArguments("--port", "8080")
            .WithStartType(ServiceStartMode.AutomaticDelayed)
            .DependsOn("Tcpip")
            .RestartOnFailure(TimeSpan.FromSeconds(30), TimeSpan.FromDays(1))
            .RunAs(ServiceAccount.VirtualAccount))));

        Assert.That(exit, Is.EqualTo(0));
        var info = await Query("App.Worker");
        Assert.Multiple(() =>
        {
            Assert.That(info.DisplayName, Is.EqualTo("App worker"));
            Assert.That(info.Description, Is.EqualTo("Does the work"));
            Assert.That(info.BinaryPath, Is.EqualTo(Binary("bin/App.Worker.exe", "--port", "8080")));
            Assert.That(info.StartType, Is.EqualTo(ServiceStartType.AutomaticDelayed));
            Assert.That(info.Dependencies, Is.EqualTo(new[] { "Tcpip" }));
            Assert.That(info.AccountName, Is.EqualTo(@"NT SERVICE\App.Worker"));
            Assert.That(info.SidType, Is.EqualTo(ServiceSidType.Unrestricted), "a virtual account gets the unrestricted service SID");
            Assert.That(info.FailureActions!.ResetPeriod, Is.EqualTo(TimeSpan.FromDays(1)));
            Assert.That(info.FailureActions.Actions.Select(a => (a.Type, a.Delay)),
                Has.All.EqualTo((ServiceFailureActionType.Restart, TimeSpan.FromSeconds(30))));
        });
        Assert.That((await Manifest())!.Services!.Single().AccountName, Is.EqualTo(@"NT SERVICE\App.Worker"));
    }

    [TestCase(nameof(ServiceAccount.LocalService), @"NT AUTHORITY\LocalService")]
    [TestCase(nameof(ServiceAccount.NetworkService), @"NT AUTHORITY\NetworkService")]
    public async Task TheBuiltInAccounts_AreWrittenByTheirWindowsNames(string account, string expected)
    {
        var chosen = account == nameof(ServiceAccount.LocalService) ? ServiceAccount.LocalService : ServiceAccount.NetworkService;
        Assert.That(await Install(Installer("1.0", w => w.AddService("App.Svc", s => s.RunAs(chosen)))), Is.EqualTo(0));
        Assert.That(Scm.Find("App.Svc")!.AccountName, Is.EqualTo(expected));
        Assert.That((await Query("App.Svc")).SidType, Is.EqualTo(ServiceSidType.None));
    }

    [Test]
    public async Task Services_StartInDependencyOrder_WhateverTheDeclarationOrder()
    {
        var exit = await Install(Installer("1.0", w => w
            .AddService("App.Worker", s => s.WithExecutable("bin/App.Worker.exe").DependsOn("App.Broker"))
            .AddService("App.Broker", _ => { })));

        Assert.That(exit, Is.EqualTo(0));
        var starts = Scm.Calls.Where(c => c.StartsWith("start:", StringComparison.Ordinal)).ToArray();
        Assert.That(starts, Is.EqualTo(new[] { "start:App.Broker", "start:App.Worker" }));
        Assert.That(Scm.Find("App.Worker")!.Dependencies, Is.EqualTo(new[] { "App.Broker" }));
    }

    [TestCase(ServiceStartMode.Manual, ManifestService.Manual)]
    [TestCase(ServiceStartMode.Disabled, ManifestService.Disabled)]
    public async Task AManualOrDisabledService_IsRegistered_ButNotStarted(ServiceStartMode mode, string recorded)
    {
        Assert.That(await Install(Installer("1.0", w => w.AddService("App.Svc", s => s.WithStartType(mode)))), Is.EqualTo(0));

        Assert.That(Scm.Find("App.Svc")!.State, Is.EqualTo(FakeServiceState.Stopped));
        Assert.That(Scm.Calls, Has.None.StartsWith("start:"));
        Assert.That((await Manifest())!.Services!.Single().StartType, Is.EqualTo(recorded));
    }

    [Test]
    public async Task StartAfterInstallFalse_RegistersAnAutomaticService_WithoutStartingIt()
    {
        Assert.That(await Install(Installer("1.0", w => w.AddService("App.Svc", s => s.StartAfterInstall(false)))), Is.EqualTo(0));
        Assert.That(Scm.Find("App.Svc")!.State, Is.EqualTo(FakeServiceState.Stopped));
    }

    [Test]
    public async Task AnInstallWithoutServices_NeverTouchesTheServiceManager()
    {
        var builder = InstellaInstaller.Create().WithApp("App", AppId, new Version(1, 0))
            .WithExecutableName("App.exe").WithElevation(ElevationMode.SystemWide).Build();
        await using var harness = InstellaTestHarness.Create().WithInstaller(builder).WithFileSystem(_fs)
            .WithPlatformServices(_platform).WithElevationService(new FakeElevationService(isElevated: true))
            .WithPayload(Files("1.0")).Build();

        Assert.That(await Install(harness), Is.EqualTo(0));
        Assert.That(Scm.Calls, Is.Empty);
        Assert.That((await Manifest())!.Services, Is.Null.Or.Empty);
    }

    // ---- upgrade and repair ----

    [Test]
    public async Task Upgrade_StopsTheServiceBeforeTheCommit_AppliesTheNewConfiguration_AndStartsItAfterTheUpgradeProgram()
    {
        Assert.That(await Install(Installer("1.0", SimpleService)), Is.EqualTo(0));
        var firstPid = Scm.Find("App.Svc")!.ProcessId;

        var seenBeforeStart = new List<(FakeServiceState State, string? Exe)>();
        var seenAfterStart = new List<FakeServiceState>();
        var v2 = Installer("2.0", w => w.AddService("App.Svc", s => s.WithArguments("--service", "--v2")), more: b => b
            .AddStep("observe-stopped", sb => sb
                .InStage(InstallStage.Finalize)
                .Before("start-services")
                .Execute((_, _, _) =>
                {
                    seenBeforeStart.Add((Scm.Find("App.Svc")!.State, Live("App.exe")));
                    return Task.FromResult(StepResult.Ok);
                })
                .NoRollbackNeeded("observes"))
            .AddStep("observe-running", sb => sb
                .InStage(InstallStage.Finalize)
                .Execute((_, _, _) =>
                {
                    seenAfterStart.Add(Scm.Find("App.Svc")!.State);
                    return Task.FromResult(StepResult.Ok);
                })
                .NoRollbackNeeded("observes")));

        Assert.That(await Install(v2), Is.EqualTo(0));

        var calls = Scm.Calls.Skip(2).ToList();
        Assert.That(calls, Is.EqualTo(new[] { "config:App.Svc", "stop:App.Svc", "start:App.Svc" }));
        Assert.That(seenBeforeStart, Is.EqualTo(new[] { (FakeServiceState.Stopped, (string?)"app 2.0") }),
            "a step that says Before(\"start-services\") runs after the commit, while the service is stopped");
        Assert.That(seenAfterStart, Is.EqualTo(new[] { FakeServiceState.Running }),
            "every other user Finalize step runs after start-services");
        var svc = Scm.Find("App.Svc")!;
        Assert.That(svc.BinaryPath, Is.EqualTo(Binary("App.exe", "--service", "--v2")));
        Assert.That(svc.State, Is.EqualTo(FakeServiceState.Running));
        Assert.That(svc.ProcessId, Is.Not.EqualTo(firstPid), "the service was restarted");
    }

    [Test]
    public async Task Repair_StopsAndRestartsTheService()
    {
        Assert.That(await Install(Installer("1.0", SimpleService)), Is.EqualTo(0));
        Assert.That(await Install(Installer("1.0", SimpleService)), Is.EqualTo(0));

        Assert.That(Scm.Calls.Skip(2), Is.EqualTo(new[] { "config:App.Svc", "stop:App.Svc", "start:App.Svc" }));
        Assert.That(Scm.Find("App.Svc")!.State, Is.EqualTo(FakeServiceState.Running));
        Assert.That((await Manifest())!.Services!.Single().Name, Is.EqualTo("App.Svc"));
    }

    [Test]
    public async Task Upgrade_RestartsAManualServiceThatWasRunning()
    {
        Assert.That(await Install(Installer("1.0", w => w.AddService("App.Svc", s => s.WithStartType(ServiceStartMode.Manual).StartAfterInstall(true)))), Is.EqualTo(0));
        Assert.That(Scm.Find("App.Svc")!.State, Is.EqualTo(FakeServiceState.Running));

        Assert.That(await Install(Installer("2.0", w => w.AddService("App.Svc", s => s.WithStartType(ServiceStartMode.Manual)))), Is.EqualTo(0));

        Assert.That(Scm.Find("App.Svc")!.State, Is.EqualTo(FakeServiceState.Running), "it was running before the upgrade");
    }

    [Test]
    public async Task Upgrade_StopsServicesInDependencyOrder_AndStartsThemInReverse()
    {
        void Both(WindowsBuilder w) => w
            .AddService("App.Broker", _ => { })
            .AddService("App.Worker", s => s.WithExecutable("bin/App.Worker.exe").DependsOn("App.Broker"));
        Assert.That(await Install(Installer("1.0", Both)), Is.EqualTo(0));
        var before = Scm.Calls.Count;

        Assert.That(await Install(Installer("2.0", Both)), Is.EqualTo(0));

        var stopsAndStarts = Scm.Calls.Skip(before).Where(c => !c.StartsWith("config:", StringComparison.Ordinal)).ToArray();
        Assert.That(stopsAndStarts, Is.EqualTo(new[] { "stop:App.Worker", "stop:App.Broker", "start:App.Broker", "start:App.Worker" }));
    }

    [Test]
    public async Task Upgrade_DoesNotAskToCloseTheRunningService_ItStopsItThroughTheServiceManager()
    {
        Assert.That(await Install(Installer("1.0", SimpleService)), Is.EqualTo(0));
        var v2 = Installer("2.0", SimpleService);

        // The harness reports a running fake service as a program holding its executable, as
        // Restart Manager would; a silent upgrade without --force-close exits 24 for any other program.
        Assert.That(await Install(v2), Is.EqualTo(0));
        Assert.That(Scm.Calls, Does.Contain("stop:App.Svc"));
        Assert.That(Scm.Find("App.Svc")!.State, Is.EqualTo(FakeServiceState.Running));
    }

    [Test]
    public async Task Upgrade_StillRefusesAnotherCopyOfTheServiceExecutable()
    {
        Assert.That(await Install(Installer("1.0", SimpleService)), Is.EqualTo(0));
        var v2 = Installer("2.0", SimpleService);
        v2.StartProcess(Path.Combine(_installPath, "App.exe"), closes: false);

        Assert.That(await Install(v2), Is.EqualTo((int)InstellaExitCode.UpdateAppCouldNotClose),
            "only the service's own process is ignored");
        Assert.That(Live("App.exe"), Is.EqualTo("app 1.0"));
        Assert.That(Scm.Find("App.Svc")!.State, Is.EqualTo(FakeServiceState.Running));
    }

    [Test]
    public async Task Upgrade_ThatNoLongerDeclaresAService_StopsAndDeletesIt()
    {
        Assert.That(await Install(Installer("1.0", w => w
            .AddService("App.Svc", _ => { })
            .AddService("App.Old", s => s.WithExecutable("bin/App.Worker.exe")))), Is.EqualTo(0));

        Assert.That(await Install(Installer("2.0", w => w.AddService("App.Svc", _ => { }))), Is.EqualTo(0));

        Assert.That(Scm.Find("App.Old"), Is.Null);
        Assert.That(Scm.Calls, Does.Contain("stop:App.Old").And.Contain("delete:App.Old"));
        Assert.That(Scm.Calls.ToList().IndexOf("stop:App.Old"), Is.LessThan(Scm.Calls.ToList().IndexOf("delete:App.Old")));
        Assert.That((await Manifest())!.Services!.Select(s => s.Name), Is.EqualTo(new[] { "App.Svc" }));
    }

    // ---- start failures ----

    [Test]
    public async Task FirstInstall_WhenTheServiceFailsToStart_Exits16_AndRollsEverythingBack()
    {
        Scm.SetStartBehavior("App.Svc", FakeServiceBehavior.Fail);
        var harness = Installer("1.0", SimpleService);

        var exit = await Install(harness);

        Assert.That(exit, Is.EqualTo((int)InstellaExitCode.InstallServiceFailed));
        Assert.That((int)InstellaExitCode.InstallServiceFailed, Is.EqualTo(16));
        Assert.That(Scm.Find("App.Svc"), Is.Null, "the service the install created is deleted");
        Assert.That(Live("App.exe"), Is.Null, "the files are rolled back");
        Assert.That(await Manifest(), Is.Null);
        Assert.That(Log(harness), Does.Contain("App.Svc").And.Contain("stopped while starting (exit code 1)"),
            "the log says which service failed and why");
    }

    [Test]
    public async Task AStartThatTimesOut_FailsTheInstall_AfterTheDeclaredStartTimeout()
    {
        Scm.SetStartBehavior("App.Svc", FakeServiceBehavior.Hang);
        var harness = Installer("1.0", w => w.AddService("App.Svc", s => s.StartTimeout(TimeSpan.FromSeconds(5))));

        Assert.That(await Install(harness), Is.EqualTo(16));
        Assert.That(Log(harness), Does.Contain("within 5 s"));
        Assert.That(Live("App.exe"), Is.Null);
    }

    [Test]
    public async Task TheDefaultStartTimeout_Is60Seconds()
    {
        Scm.SetStartBehavior("App.Svc", FakeServiceBehavior.Hang);
        var harness = Installer("1.0", SimpleService);

        Assert.That(await Install(harness), Is.EqualTo(16));
        Assert.That(Log(harness), Does.Contain("within 60 s"));
    }

    [Test]
    public async Task Upgrade_WhenTheNewVersionFailsToStart_RestoresTheOldFilesAndConfiguration_AndRestartsTheOldVersion()
    {
        Assert.That(await Install(Installer("1.0", SimpleService)), Is.EqualTo(0));
        // Only the new version's start fails: the step arms the failure, and its rollback (which
        // runs before stop-services' and register-services' rollbacks) disarms it again.
        var v2 = Installer("2.0", w => w.AddService("App.Svc", s => s.WithArguments("--service", "--v2")),
            more: b => b.AddStep("fail-start", sb => sb
                .InStage(InstallStage.Finalize)
                .After("app-upgrade")
                .Before("start-services")
                .Execute((_, _, _) =>
                {
                    Scm.SetStartBehavior("App.Svc", FakeServiceBehavior.Fail);
                    return Task.FromResult(StepResult.Ok);
                })
                .Rollback((_, _) =>
                {
                    Scm.SetStartBehavior("App.Svc", FakeServiceBehavior.Succeed);
                    return Task.CompletedTask;
                })));

        Assert.That(await Install(v2), Is.EqualTo((int)InstellaExitCode.InstallServiceFailed));

        Assert.That(Live("App.exe"), Is.EqualTo("app 1.0"), "the files are rolled back");
        Assert.That((await Manifest())!.Version, Is.EqualTo(new Version(1, 0)));
        var svc = Scm.Find("App.Svc")!;
        Assert.That(svc.State, Is.EqualTo(FakeServiceState.Running), "the previous version runs again");
        Assert.That(svc.BinaryPath, Is.EqualTo(Binary("App.exe", "--service")), "the previous configuration is restored");
        var calls = Scm.Calls.ToList();
        Assert.That(calls.LastIndexOf("config:App.Svc"), Is.LessThan(calls.LastIndexOf("start:App.Svc")),
            "the previous version is restarted after its configuration is restored");
    }

    [Test]
    public async Task Upgrade_WhenEvenThePreviousVersionCannotRestart_StillExits16()
    {
        Assert.That(await Install(Installer("1.0", SimpleService)), Is.EqualTo(0));
        Scm.SetStartBehavior("App.Svc", FakeServiceBehavior.Fail);
        var harness = Installer("2.0", w => w.AddService("App.Svc", s => s.WithArguments("--service", "--v2")));

        Assert.That(await Install(harness), Is.EqualTo((int)InstellaExitCode.InstallServiceFailed),
            "a failed start wins over rollback warnings, as a failed app-upgrade does");
        Assert.That(Live("App.exe"), Is.EqualTo("app 1.0"));
        Assert.That(Scm.Find("App.Svc")!.BinaryPath, Is.EqualTo(Binary("App.exe", "--service")));
    }

    [Test]
    public async Task Upgrade_ThatFails_RecreatesTheServiceItDropped()
    {
        Assert.That(await Install(Installer("1.0", w => w
            .AddService("App.Svc", _ => { })
            .AddService("App.Old", s => s.WithExecutable("bin/App.Worker.exe").WithArguments("--old")))), Is.EqualTo(0));
        var v2 = Installer("2.0", w => w.AddService("App.Svc", _ => { }), more: b => b.AddStep("boom", sb => sb
            .InStage(InstallStage.Finalize)
            .Execute((_, _, _) => Task.FromResult(StepResult.Fail("boom")))
            .NoRollbackNeeded("fails")));

        Assert.That(await Install(v2), Is.Not.EqualTo(0));

        var old = Scm.Find("App.Old")!;
        Assert.That(old, Is.Not.Null, "rollback re-creates the dropped service");
        Assert.That(old.BinaryPath, Is.EqualTo(Binary("bin/App.Worker.exe", "--old")));
        Assert.That(old.State, Is.EqualTo(FakeServiceState.Running), "it was running before the upgrade");
        Assert.That((await Manifest())!.Services!.Select(s => s.Name), Is.EquivalentTo(new[] { "App.Svc", "App.Old" }));
    }

    [Test]
    public async Task StartFailureIsWarning_InstallsAnyway_AndWarns()
    {
        Scm.SetStartBehavior("App.Svc", FakeServiceBehavior.Fail);
        var harness = Installer("1.0", w => w.AddService("App.Svc", s => s.StartFailureIsWarning()));

        Assert.That(await Install(harness), Is.EqualTo(0));

        Assert.That(Scm.Find("App.Svc")!.State, Is.EqualTo(FakeServiceState.Stopped));
        Assert.That(Live("App.exe"), Is.EqualTo("app 1.0"));
        Assert.That(harness.LogSink.Entries.Where(e => e.Level == Instella.Core.Logging.InstellaLogLevel.Warn).Select(e => e.Message),
            Has.Some.Contains("App.Svc"));
        Assert.That((await Manifest())!.Services!.Single().Name, Is.EqualTo("App.Svc"));
    }

    // ---- refusals before anything changes ----

    [Test]
    public async Task FirstInstall_RefusesAForeignServiceOfTheSameName_AndChangesNothing()
    {
        var otherPath = Path.Combine(_root, "Other");
        Assert.That(await Install(Installer("1.0", SimpleService, appId: "com.example.other"), otherPath), Is.EqualTo(0));
        var foreign = Scm.Find("App.Svc")!;
        var callsBefore = Scm.Calls.Count;
        var harness = Installer("1.0", SimpleService);

        var exit = await Install(harness);

        Assert.That(exit, Is.EqualTo((int)InstellaExitCode.InstallGeneralFailure));
        var after = Scm.Find("App.Svc")!;
        Assert.That((after.BinaryPath, after.State, after.ProcessId), Is.EqualTo((foreign.BinaryPath, foreign.State, foreign.ProcessId)),
            "the foreign service is untouched");
        Assert.That(Scm.Calls.Skip(callsBefore), Is.Empty, "queries only: nothing is created, changed, stopped or deleted");
        Assert.That(Live("App.exe"), Is.Null);
        Assert.That(await Manifest(), Is.Null);
        Assert.That(Log(harness), Does.Contain("App.Svc"));
    }

    [Test]
    public async Task APayloadWithoutTheServiceExecutable_IsRefused_WithExit10()
    {
        var harness = Installer("1.0", w => w.AddService("App.Svc", s => s.WithExecutable("bin/Missing.exe")));

        Assert.That(await Install(harness), Is.EqualTo((int)InstellaExitCode.InstallGeneralFailure));
        Assert.That(Scm.Find("App.Svc"), Is.Null);
        Assert.That(Scm.Calls, Is.Empty);
        Assert.That(Live("App.exe"), Is.Null);
        Assert.That(Log(harness), Does.Contain("bin/Missing.exe").Or.Contain(@"bin\Missing.exe"));
    }

    [Test]
    public async Task AnInstallFolderNonAdministratorsCanWrite_IsRefused_WithExit10()
    {
        _platform.MarkWritableByNonAdmins(_root);
        var harness = Installer("1.0", SimpleService);

        Assert.That(await Install(harness), Is.EqualTo((int)InstellaExitCode.InstallGeneralFailure));
        Assert.That(Scm.Calls, Is.Empty);
        Assert.That(Live("App.exe"), Is.Null);
        Assert.That(Log(harness), Does.Contain(_installPath).Or.Contain(_root), "the message names the folder");
    }

    [Test]
    public async Task AWritableFolder_DoesNotMatter_WithoutServices()
    {
        _platform.MarkWritableByNonAdmins(_root);
        var builder = InstellaInstaller.Create().WithApp("App", AppId, new Version(1, 0))
            .WithExecutableName("App.exe").WithElevation(ElevationMode.SystemWide).Build();
        await using var harness = InstellaTestHarness.Create().WithInstaller(builder).WithFileSystem(_fs)
            .WithPlatformServices(_platform).WithElevationService(new FakeElevationService(isElevated: true))
            .WithPayload(Files("1.0")).Build();

        Assert.That(await Install(harness), Is.EqualTo(0));
    }
}
