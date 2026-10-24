using System;
using System.IO;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Instella.Installer.Runtime.Core.Security;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Services;

/// <summary>
/// The real install-folder check a service install runs: a folder whose DACL lets Users,
/// Authenticated Users, Everyone, INTERACTIVE or the installing user's own SID write or modify it is
/// refused; Program Files passes. A folder that does not exist yet is judged by its nearest existing
/// parent, whose inheritable entries it will get.
/// </summary>
[TestFixture, Platform("Win"), SupportedOSPlatform("windows")]
public partial class InstallFolderAclTests
{
    private string _temp = null!;

    [SetUp]
    public void SetUp()
    {
        _temp = Path.Combine(Path.GetTempPath(), "instella-acl", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_temp, recursive: true); } catch { /* best effort */ }
    }

    private static string ProgramFiles => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

    [Test]
    public void ProgramFiles_Passes()
    {
        Assert.That(InstallFolderAcl.FindNonAdminWriter(ProgramFiles), Is.Null);
    }

    [Test]
    public void AFolderThatDoesNotExistYetUnderProgramFiles_Passes()
    {
        var path = Path.Combine(ProgramFiles, "Instella-acl-test-" + Guid.NewGuid().ToString("N"), "App");
        Assert.That(InstallFolderAcl.FindNonAdminWriter(path), Is.Null);
    }

    [Test]
    public void AFolderUnderTheUsersTemp_IsWritableByTheInstallingUser_AndIsReported()
    {
        var reason = InstallFolderAcl.FindNonAdminWriter(_temp);
        Assert.That(reason, Is.Not.Null.And.Not.Empty, "the user's own SID (or CREATOR OWNER) may write there");
    }

    [Test]
    public void AFolderThatDoesNotExistYetUnderTheUsersTemp_IsReported()
    {
        Assert.That(InstallFolderAcl.FindNonAdminWriter(Path.Combine(_temp, "not-yet", "App")), Is.Not.Null);
    }

    private static SecurityIdentifier Me => WindowsIdentity.GetCurrent().User!;

    private static FileSystemAccessRule Allow(IdentityReference who, FileSystemRights rights, InheritanceFlags inheritance,
        PropagationFlags propagation = PropagationFlags.None) =>
        new(who, rights, inheritance, propagation, AccessControlType.Allow);

    /// <summary>Replaces the folder's DACL with <paramref name="rules"/> only, nothing inherited.</summary>
    private static void Protect(string folder, params FileSystemAccessRule[] rules)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var rule in rules) security.AddAccessRule(rule);
        new DirectoryInfo(folder).SetAccessControl(security);
    }

    /// <summary>Gives the folder back to the current user so TearDown can delete it.</summary>
    private static void Release(string folder)
    {
        try
        {
            Protect(folder, Allow(Me, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit));
        }
        catch (Exception) { /* best effort */ }
    }
}
