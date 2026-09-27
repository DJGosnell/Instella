using System;
using System.Linq;
using Instella.Core.Installation;
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Runtime.Runners;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Installation;

/// <summary>
/// Pins the step names / stages that
/// <see cref="PreviewStepLists"/> produces per mode. The names are part of
/// the observable surface via <c>--preview-fail=&lt;step-name&gt;</c>, so a
/// rename here silently breaks CLI help output.
/// </summary>
[TestFixture]
public sealed class PreviewStepListsTests
{
    private static FrozenConfig Config()
    {
        var b = new InstallerBuilder();
        b.WithApp("Preview Test", "com.example.preview", new Version(1, 0, 0));
        var installer = (InstellaInstallerImpl)b.Build();
        return installer.ConfigForTests;
    }

    [Test]
    public void Build_install_returnsOfflineInstallSteps()
    {
        var steps = PreviewStepLists.Build(Config(), InstallerMode.FirstInstall);
        var names = steps.Select(s => s.Name).ToList();

        Assert.That(names, Contains.Item("prerequisites"));
        Assert.That(names, Contains.Item("extract-payload"));
        Assert.That(names, Contains.Item("write-manifest"));
    }

    [TestCase(InstallerMode.FirstInstall)]
    [TestCase(InstallerMode.Upgrade)]
    [TestCase(InstallerMode.Repair)]
    public void Build_installFamily_sameStepList(InstallerMode mode)
    {
        var install = PreviewStepLists.Build(Config(), InstallerMode.FirstInstall).Select(s => s.Name).ToArray();
        var other = PreviewStepLists.Build(Config(), mode).Select(s => s.Name).ToArray();
        Assert.That(other, Is.EqualTo(install));
    }

    [Test]
    public void Build_update_returnsSyntheticUpdateSteps()
    {
        var steps = PreviewStepLists.Build(Config(), InstallerMode.Update);
        Assert.That(steps.Select(s => s.Name), Is.EqualTo(new[]
        {
            "download-update", "extract-update", "replace-files", "finalize-update",
        }));
    }

    [Test]
    public void Build_uninstall_returnsSyntheticUninstallSteps()
    {
        var steps = PreviewStepLists.Build(Config(), InstallerMode.Uninstall);
        Assert.That(steps.Select(s => s.Name), Is.EqualTo(new[]
        {
            "remove-shortcuts", "remove-file-associations", "remove-path-entry",
            "remove-auto-start", "unregister-uninstall-entry", "delete-files",
        }));
    }

    [Test]
    public void Build_manageAndCleanup_returnsEmptyList()
    {
        Assert.That(PreviewStepLists.Build(Config(), InstallerMode.Manage), Is.Empty);
        Assert.That(PreviewStepLists.Build(Config(), InstallerMode.Cleanup), Is.Empty);
    }

    [Test]
    public void Build_install_mergesUserSteps()
    {
        var b = new InstallerBuilder();
        b.WithApp("Preview Test", "com.example.preview", new Version(1, 0, 0));
        b.AddStep("custom-user-step", s => s
            .InStage(InstallStage.Register)
            .Execute((ctx, p, ct) => System.Threading.Tasks.Task.FromResult(StepResult.Ok))
            .NoRollbackNeeded("test"));
        var installer = (InstellaInstallerImpl)b.Build();

        var steps = PreviewStepLists.Build(installer.ConfigForTests, InstallerMode.FirstInstall);

        Assert.That(steps.Select(s => s.Name), Contains.Item("custom-user-step"));
    }
}
