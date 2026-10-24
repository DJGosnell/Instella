using System.IO;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Services;

/// <summary>Custom and gMSA service accounts are documented: the services guide, the security model, llm.md and the changelog.</summary>
[TestFixture]
public class ServiceAccountDocsTests
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
    public void TheGuide_HasAnAccountsSection_CoveringAccountsFlagsTheVariableAndTheLogonRight()
    {
        var guide = Read("docs/windows-services.md");
        Assert.That(guide, Does.Contain("## Accounts"));
        Assert.That(guide, Does.Contain("ManagedServiceAccount").And.Contain("FromInstaller"));
        Assert.That(guide, Does.Contain("--service-account").And.Contain("--service-password-file"));
        Assert.That(guide, Does.Contain("INSTELLA_SERVICE_PASSWORD").And.Contain("WithServicePasswordVariable"));
        Assert.That(guide, Does.Contain("SeServiceLogonRight"));
        Assert.That(guide, Does.Contain("no `--service-password`").IgnoreCase, "the guide says why there is no password flag");
    }

    [Test]
    public void TheSecurityModel_NotesCredentialHandling()
    {
        var security = Read("docs/security-model.md");
        Assert.That(security, Does.Contain("INSTELLA_SERVICE_PASSWORD").And.Contain("PasswordInput"));
        Assert.That(security, Does.Contain("SeServiceLogonRight"));
    }

    [Test]
    public void LlmMd_ListsTheReservedFlagsAndThePasswordWidget()
    {
        var llm = Read("llm.md");
        Assert.That(llm, Does.Contain("--service-account").And.Contain("--service-password-file"));
        Assert.That(llm, Does.Contain("PasswordInput"));
        Assert.That(llm, Does.Contain("ManagedServiceAccount").And.Contain("FromInstaller"));
    }

    [Test]
    public void TheChangelog_MentionsTheAccountsAndTheNewReservedFlags()
    {
        var changelog = Read("CHANGELOG.md");
        Assert.That(changelog, Does.Contain("FromInstaller").And.Contain("--service-account").And.Contain("--service-password-file"));
    }
}
