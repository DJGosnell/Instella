using System;
using System.Linq;
using Instella.Core.Manifest;
using Instella.Installer.Runtime.Builders;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Services;

/// <summary>
/// <c>OnWindows(w =&gt; w.AddService(...))</c>: what <see cref="InstallerBuilder.Build"/> accepts and
/// refuses. <c>OnWindows</c> only runs its delegate on Windows, so these run there only.
/// </summary>
[TestFixture, Platform("Win")]
public class ServiceBuilderValidationTests
{
    private static InstallerBuilder App(ElevationMode elevation = ElevationMode.SystemWide) =>
        InstellaInstaller.Create()
            .WithApp("App", "com.example.app", new Version(1, 0))
            .WithExecutableName("App.exe")
            .WithElevation(elevation);

    private static string Refusal(InstallerBuilder builder) =>
        Assert.Throws<InvalidOperationException>(() => builder.Build())!.Message;

    [Test]
    public void SeveralServices_AreAccepted_AndFrozenInDeclarationOrder()
    {
        var installer = (InstellaInstallerImpl)App()
            .OnWindows(w => w
                .AddService("App.Worker", s => s
                    .WithDisplayName("App worker")
                    .WithDescription("Does the work")
                    .WithExecutable("bin/App.Worker.exe")
                    .WithArguments("--service", "--port", "8080")
                    .WithStartType(ServiceStartMode.AutomaticDelayed)
                    .DependsOn("App.Broker")
                    .RestartOnFailure(TimeSpan.FromSeconds(30), TimeSpan.FromDays(1))
                    .RunAs(ServiceAccount.VirtualAccount)
                    .StartTimeout(TimeSpan.FromSeconds(90))
                    .StopTimeout(TimeSpan.FromSeconds(10)))
                .AddService("App.Broker", s => s.RunAs(ServiceAccount.LocalService)))
            .Build();

        Assert.That(installer.ConfigForTests.Services.Select(s => s.Name), Is.EqualTo(new[] { "App.Worker", "App.Broker" }));
    }

    [Test]
    public void TheEmittedBuildManifest_NamesEachServiceAndItsExecutable()
    {
        var installer = (InstellaInstallerImpl)App()
            .OnWindows(w => w
                .AddService("App.Worker", s => s.WithExecutable("bin/App.Worker.exe"))
                .AddService("App.Main", _ => { }))
            .Build();

        var services = installer.ConfigForTests.ToManifest().Services!;
        Assert.That(services.Select(s => (s.Name, s.Executable)), Is.EqualTo(new (string, string?)[]
        {
            ("App.Worker", "bin/App.Worker.exe"),
            ("App.Main", null),
        }), "a null executable is the app's main executable");
    }

    [TestCase(ElevationMode.PerUser)]
    [TestCase(ElevationMode.UserChoice)]
    public void AServiceWithoutSystemWideElevation_IsRefused(ElevationMode elevation)
    {
        var message = Refusal(App(elevation).OnWindows(w => w.AddService("App.Svc", _ => { })));
        Assert.That(message, Does.Contain("App.Svc").And.Contain("SystemWide"));
    }

    [Test]
    public void TheDefaultElevation_IsRefusedForAService()
    {
        var builder = InstellaInstaller.Create()
            .WithApp("App", "com.example.app", new Version(1, 0))
            .OnWindows(w => w.AddService("App.Svc", _ => { }));
        Assert.That(Refusal(builder), Does.Contain("SystemWide"));
    }

    [TestCase("")]
    [TestCase("has space")]
    [TestCase("slash/name")]
    [TestCase("back\\slash")]
    [TestCase("ünicode")]
    public void AnInvalidServiceName_IsRefused(string name)
    {
        Assert.That(() => App().OnWindows(w => w.AddService(name, _ => { })).Build(),
            Throws.InstanceOf<InvalidOperationException>().Or.InstanceOf<ArgumentException>());
    }

    [Test]
    public void AServiceNameOf81Characters_IsRefused_And80IsAccepted()
    {
        Assert.That(Refusal(App().OnWindows(w => w.AddService(new string('a', 81), _ => { }))), Does.Contain("80"));
        Assert.That(() => App().OnWindows(w => w.AddService(new string('a', 80), _ => { })).Build(), Throws.Nothing);
    }

    [Test]
    public void ADuplicateServiceName_IsRefused_WhateverItsCase()
    {
        var message = Refusal(App().OnWindows(w => w
            .AddService("App.Svc", _ => { })
            .AddService("app.svc", _ => { })));
        Assert.That(message, Does.Contain("duplicate").IgnoreCase.And.Contain("app.svc").IgnoreCase);
    }

    [Test]
    public void ADependencyCycleAmongDeclaredServices_IsRefused()
    {
        var message = Refusal(App().OnWindows(w => w
            .AddService("A", s => s.DependsOn("B"))
            .AddService("B", s => s.DependsOn("C"))
            .AddService("C", s => s.DependsOn("A"))));
        Assert.That(message, Does.Contain("cycle").IgnoreCase);
    }

    [Test]
    public void AServiceThatDependsOnItself_IsRefused()
    {
        Assert.That(Refusal(App().OnWindows(w => w.AddService("A", s => s.DependsOn("A")))), Does.Contain("cycle").IgnoreCase);
    }

    [Test]
    public void ADependencyOnAServiceTheInstallerDoesNotDeclare_IsAccepted()
    {
        Assert.That(() => App().OnWindows(w => w.AddService("A", s => s.DependsOn("Tcpip", "EventLog"))).Build(), Throws.Nothing);
    }

    [TestCase("../outside.exe")]
    [TestCase("C:\\Windows\\System32\\cmd.exe")]
    [TestCase("/abs/svc.exe")]
    [TestCase("bin/../../svc.exe")]
    public void AnExecutableOutsideThePayload_IsRefused(string executable)
    {
        var message = Refusal(App().OnWindows(w => w.AddService("App.Svc", s => s.WithExecutable(executable))));
        Assert.That(message, Does.Contain("App.Svc"));
    }

    [TestCase("instella.exe")]
    [TestCase(".instella/instella.exe")]
    [TestCase(".instella-manifest.json")]
    public void AnInstellaOwnedExecutable_IsRefused(string executable)
    {
        var message = Refusal(App().OnWindows(w => w.AddService("App.Svc", s => s.WithExecutable(executable))));
        Assert.That(message, Does.Contain("App.Svc"));
    }

    [Test]
    public void StartFailureIsWarning_WithNothingToStart_IsRefused()
    {
        Assert.That(Refusal(App().OnWindows(w => w.AddService("App.Svc", s => s.StartAfterInstall(false).StartFailureIsWarning()))),
            Does.Contain("StartFailureIsWarning"));
        Assert.That(Refusal(App().OnWindows(w => w.AddService("App.Svc", s => s.WithStartType(ServiceStartMode.Manual).StartFailureIsWarning()))),
            Does.Contain("StartFailureIsWarning"), "a manual service is not started after install unless it says so");
        Assert.That(Refusal(App().OnWindows(w => w.AddService("App.Svc", s => s.WithStartType(ServiceStartMode.Disabled).StartFailureIsWarning()))),
            Does.Contain("StartFailureIsWarning"));
    }

    [Test]
    public void StartFailureIsWarning_OnAManualServiceThatIsStartedAfterInstall_IsAccepted()
    {
        Assert.That(() => App().OnWindows(w => w.AddService("App.Svc", s => s
            .WithStartType(ServiceStartMode.Manual).StartAfterInstall(true).StartFailureIsWarning())).Build(), Throws.Nothing);
    }

    [TestCase("register-services")]
    [TestCase("stop-services")]
    [TestCase("start-services")]
    public void AUserStepNamedLikeABuiltInServiceStep_IsRefused(string name)
    {
        var builder = App().AddStep(name, sb => sb
            .InStage(Instella.Core.Installation.InstallStage.Finalize)
            .Execute((_, _, _) => System.Threading.Tasks.Task.FromResult(Instella.Core.Installation.StepResult.Ok))
            .NoRollbackNeeded("test"));
        Assert.That(Refusal(builder), Does.Contain(name));
    }

    [Test]
    public void APointOfNoReturnBeforeStartServices_IsRefused()
    {
        var builder = App()
            .OnWindows(w => w.AddService("App.Svc", _ => { }))
            .AddStep("ponr", sb => sb
                .Execute((_, _, _) => System.Threading.Tasks.Task.FromResult(Instella.Core.Installation.StepResult.Ok))
                .NoRollbackNeeded("test")
                .PointOfNoReturn("test")
                .Before("start-services"));
        Assert.That(Refusal(builder), Does.Contain("start-services"));
    }

    [Test]
    public void ServiceBuilderArguments_AreCheckedWhenGiven()
    {
        Assert.Throws<ArgumentNullException>(() => App().OnWindows(w => w.AddService("App.Svc", null!)));
        Assert.Throws<ArgumentOutOfRangeException>(() => App().OnWindows(w => w.AddService("App.Svc", s => s.StartTimeout(TimeSpan.Zero))));
        Assert.Throws<ArgumentOutOfRangeException>(() => App().OnWindows(w => w.AddService("App.Svc", s => s.StopTimeout(TimeSpan.FromSeconds(-1)))));
        Assert.Throws<ArgumentOutOfRangeException>(() => App().OnWindows(w => w.AddService("App.Svc", s => s.RestartOnFailure(TimeSpan.FromSeconds(-1), TimeSpan.FromDays(1)))));
        Assert.Throws<ArgumentNullException>(() => App().OnWindows(w => w.AddService("App.Svc", s => s.RunAs(null!))));
    }
}
