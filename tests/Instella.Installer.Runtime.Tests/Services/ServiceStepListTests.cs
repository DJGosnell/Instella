using System;
using System.Linq;
using Instella.Core.Installation;
using Instella.Core.Manifest;
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Runtime.Installation;
using Instella.Installer.Runtime.Runners;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Services;

/// <summary>
/// Where the built-in service steps sit in the install pipeline, as preview mode lists it:
/// <c>register-services</c> with the Register steps, <c>stop-services</c> between
/// <c>write-manifest</c> and <c>commit-transaction</c>, <c>start-services</c> right after
/// <c>app-upgrade</c>. Without a declared service the list is what it was.
/// </summary>
[TestFixture, Platform("Win")]
public class ServiceStepListTests
{
    private static FrozenConfig Config(Action<InstallerBuilder>? configure = null)
    {
        var b = InstellaInstaller.Create()
            .WithApp("App", "com.example.app", new Version(1, 0))
            .WithExecutableName("App.exe")
            .WithElevation(ElevationMode.SystemWide);
        configure?.Invoke(b);
        return ((InstellaInstallerImpl)b.Build()).ConfigForTests;
    }

    private static FrozenConfig WithService() => Config(b => b.OnWindows(w => w.AddService("App.Svc", _ => { })));

    [TestCase(InstallerMode.FirstInstall)]
    [TestCase(InstallerMode.Upgrade)]
    [TestCase(InstallerMode.Repair)]
    public void ThePreviewList_PlacesTheServiceSteps(InstallerMode mode)
    {
        var names = PreviewStepLists.Build(WithService(), mode).Select(s => s.Name).ToList();
        int At(string name) => names.IndexOf(name);

        Assert.That(names, Does.Contain("register-services").And.Contain("stop-services").And.Contain("start-services"));
        Assert.That(At("register-services"), Is.GreaterThan(At("extract-payload")));
        Assert.That(At("register-services"), Is.LessThan(At("write-manifest")));
        Assert.That(At("stop-services"), Is.GreaterThan(At("write-manifest")));
        Assert.That(At("stop-services"), Is.LessThan(At("commit-transaction")));
        Assert.That(At("start-services"), Is.EqualTo(At("app-upgrade") + 1), "start-services follows app-upgrade at once");
    }

    [Test]
    public void ThePreviewList_PutsUserFinalizeStepsAfterStartServices()
    {
        var config = Config(b => b
            .OnWindows(w => w.AddService("App.Svc", _ => { }))
            .AddStep("user-finalize", sb => sb
                .InStage(InstallStage.Finalize)
                .Execute((_, _, _) => System.Threading.Tasks.Task.FromResult(StepResult.Ok))
                .NoRollbackNeeded("test")));
        var names = PreviewStepLists.Build(config, InstallerMode.FirstInstall).Select(s => s.Name).ToList();

        Assert.That(names.IndexOf("user-finalize"), Is.GreaterThan(names.IndexOf("start-services")));
    }

    [TestCase("register-services")]
    [TestCase("stop-services")]
    [TestCase("start-services")]
    public void PreviewFail_AcceptsTheServiceSteps(string step)
    {
        Assert.That(() => new SimulatedStepExecutor(PreviewStepLists.Build(WithService(), InstallerMode.Upgrade), PreviewSpeed.Fast, step),
            Throws.Nothing, "--preview-fail can name a service step");
    }

    [Test]
    public void TheServiceSteps_HaveDisplayNames()
    {
        var map = StepDisplayNames.Map(PreviewStepLists.Build(WithService(), InstallerMode.Upgrade));
        foreach (var step in new[] { "register-services", "stop-services", "start-services" })
            Assert.That(map[step], Is.Not.EqualTo(step), $"{step} has a display name of its own");
    }

    [Test]
    public void WithoutServices_NoServiceStepIsListed()
    {
        var names = PreviewStepLists.Build(Config(), InstallerMode.FirstInstall).Select(s => s.Name).ToList();
        Assert.That(names, Has.None.EndsWith("-services"));
    }
}
