using System;
using System.IO;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Services;

/// <summary>The Windows services feature is documented: its own guide, llm.md and the changelog.</summary>
[TestFixture]
public class ServiceDocsTests
{
    private static string Repo()
    {
        var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Instella.sln")))
            dir = dir.Parent;
        Assert.That(dir, Is.Not.Null, "the repository root (Instella.sln) was not found");
        return dir!.FullName;
    }

    private static string Read(string relative)
    {
        var path = Path.Combine(Repo(), relative);
        Assert.That(File.Exists(path), Is.True, $"{relative} exists");
        return File.ReadAllText(path);
    }

    [Test]
    public void TheGuide_CoversTheBuilderTheStepsExit16AndSecurity()
    {
        var guide = Read("docs/windows-services.md");
        Assert.That(guide, Does.Contain("AddService").And.Contain("ServiceBuilder").And.Contain("RunAs")
            .And.Contain("StartFailureIsWarning"));
        Assert.That(guide, Does.Contain("register-services").And.Contain("stop-services").And.Contain("start-services"));
        Assert.That(guide, Does.Contain("InstallServiceFailed").And.Contain("16"));
        Assert.That(guide, Does.Contain("SystemWide"));
        Assert.That(guide, Does.Contain("## Security").IgnoreCase);
    }

    [Test]
    public void LlmMd_DescribesTheBuilderTheStepsAndExit16()
    {
        var llm = Read("llm.md");
        Assert.That(llm, Does.Contain("AddService("));
        Assert.That(llm, Does.Contain("`register-services`").And.Contain("`stop-services`").And.Contain("`start-services`"));
        Assert.That(llm, Does.Contain("InstallServiceFailed"));
        Assert.That(llm, Does.Contain("docs/windows-services.md"));
    }

    [Test]
    public void AddServicesApiDoc_DoesNotClaimUninstallRemovesTheService()
    {
        var builders = Read("src/Instella.Installer.Runtime/Builders/PlatformBuilders.cs");
        Assert.That(builders, Does.Not.Contain("uninstall removes it"),
            "uninstall does not stop or delete services yet (docs/windows-services.md, Limits)");
        Assert.That(builders, Does.Contain("uninstall leaves the service registered"));
    }

    [Test]
    public void TheChangelog_MentionsWindowsServices()
    {
        Assert.That(Read("CHANGELOG.md"), Does.Contain("AddService"));
    }
}
