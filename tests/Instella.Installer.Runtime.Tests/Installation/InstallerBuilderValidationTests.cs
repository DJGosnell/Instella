using System;
using Instella.Core.Installation;
using Instella.Installer.Runtime.Builders;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Installation;

[TestFixture]
public sealed class InstallerBuilderValidationTests
{
    [Test]
    public void Build_withoutWithApp_throws()
    {
        var builder = InstellaInstaller.Create();
        var ex = Assert.Throws<InvalidOperationException>(() => builder.Build());
        Assert.That(ex!.Message, Does.Contain("WithApp"));
    }

    [Test]
    public void WithApp_AssemblyVersion_IsRecordedWithoutAZeroRevision()
    {
        var config = ((InstellaInstallerImpl)InstellaInstaller.Create()
            .WithApp("TestApp", "com.test.app", new Version(1, 2, 0, 0))
            .Build()).ConfigForTests;
        Assert.That(config.AppVersion, Is.EqualTo(new Version(1, 2, 0)));
        Assert.That(config.AppVersion.ToString(), Is.EqualTo("1.2.0"), "matches `instella upload --version 1.2.0`");
    }

    // ---- download tokens ----

    [TestCase("abc")]
    [TestCase("idt_short")]
    [TestCase("Bearer idt_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void WithDownloadToken_RefusesSomethingThatIsNotAToken(string token)
    {
        var ex = Assert.Throws<ArgumentException>(() => new InstallerBuilder().WithDownloadToken(token));
        Assert.That(ex!.Message, Does.StartWith("not a download token (idt_"));
    }

    [Test]
    public void WithDownloadToken_NeedsWithServer()
    {
        var b = new InstallerBuilder().WithApp("App", "com.app", new Version(1, 0, 0)).WithDownloadToken("idt_" + new string('A', 43));

        var ex = Assert.Throws<InvalidOperationException>(() => b.Build());
        Assert.That(ex!.Message, Does.Contain("WithDownloadToken(...) needs WithServer(...)"));
    }

    [Test]
    public void Build_withOnlyWithApp_succeeds()
    {
        var installer = InstellaInstaller.Create()
            .WithApp("TestApp", "com.test.app", new Version(1, 0, 0))
            .Build();
        Assert.That(installer, Is.Not.Null);
    }

    [Test]
    public void Build_rejectsDuplicateStepNames()
    {
        var builder = InstellaInstaller.Create()
            .WithApp("TestApp", "com.test.app", new Version(1, 0, 0))
            .AddStep("mine", sb => sb
                .InStage(InstallStage.Register)
                .Execute((_, _, _) => System.Threading.Tasks.Task.FromResult(StepResult.Ok))
                .NoRollbackNeeded("test"))
            .AddStep("mine", sb => sb
                .InStage(InstallStage.Register)
                .Execute((_, _, _) => System.Threading.Tasks.Task.FromResult(StepResult.Ok))
                .NoRollbackNeeded("test"));

        var ex = Assert.Throws<InvalidOperationException>(() => builder.Build());
        Assert.That(ex!.Message, Does.Contain("duplicate step name"));
    }

    [Test]
    public void Build_rejectsReservedCliFlag_silent()
    {
        var builder = InstellaInstaller.Create()
            .WithApp("TestApp", "com.test.app", new Version(1, 0, 0))
            .AddCliFlag<bool>("--silent");

        var ex = Assert.Throws<InvalidOperationException>(() => builder.Build());
        Assert.That(ex!.Message, Does.Contain("reserved"));
    }

    [Test]
    public void Build_rejectsReservedCliFlag_noHyphenPrefix()
    {
        // User may leave off the leading "--"; validator normalizes.
        var builder = InstellaInstaller.Create()
            .WithApp("TestApp", "com.test.app", new Version(1, 0, 0))
            .AddCliFlag<string>("path", defaultValue: "");

        var ex = Assert.Throws<InvalidOperationException>(() => builder.Build());
        Assert.That(ex!.Message, Does.Contain("--path"));
    }

    [Test]
    public void Build_allowsNonReservedCliFlag()
    {
        Assert.DoesNotThrow(() => InstellaInstaller.Create()
            .WithApp("TestApp", "com.test.app", new Version(1, 0, 0))
            .AddCliFlag<string>("--license-key")
            .Build());
    }

    [Test]
    public void Build_allowsMultipleDistinctSteps()
    {
        Assert.DoesNotThrow(() => InstellaInstaller.Create()
            .WithApp("TestApp", "com.test.app", new Version(1, 0, 0))
            .AddStep("step-a", sb => sb
                .Execute((_, _, _) => System.Threading.Tasks.Task.FromResult(StepResult.Ok))
                .NoRollbackNeeded("test"))
            .AddStep("step-b", sb => sb
                .Execute((_, _, _) => System.Threading.Tasks.Task.FromResult(StepResult.Ok))
                .NoRollbackNeeded("test"))
            .Build());
    }

    [Test]
    public void Create_yieldsIndependentBuilders()
    {
        var a = InstellaInstaller.Create();
        var b = InstellaInstaller.Create();
        Assert.That(a, Is.Not.SameAs(b));
    }

    [Test]
    public void WithApp_rejectsEmptyName()
    {
        Assert.Throws<ArgumentException>(() =>
            InstellaInstaller.Create().WithApp("", "id", new Version(1, 0)));
    }

    [Test]
    public void WithApp_rejectsNullVersion()
    {
        Assert.Throws<ArgumentNullException>(() =>
            InstellaInstaller.Create().WithApp("Name", "id", null!));
    }
}
