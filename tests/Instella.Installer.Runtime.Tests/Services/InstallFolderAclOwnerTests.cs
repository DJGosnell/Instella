using Instella.Installer.Runtime.Runners;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Services;

public class InstallFolderAclOwnerTests
{
    [NUnit.Framework.Test]
    public void AnInstallFolderOwnedByTheInstallingUser_InAParentTheUserCanWrite_IsReported()
    {
        if (!System.OperatingSystem.IsWindows()) NUnit.Framework.Assert.Ignore("Windows DACLs");
        var parent = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "instella-acl-probe", System.Guid.NewGuid().ToString("N"));
        var app = System.IO.Path.Combine(parent, "App");
        System.IO.Directory.CreateDirectory(app);
    #pragma warning disable CA1416
        var me = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var dir = new System.IO.DirectoryInfo(app);
        try
        {
            var security = new System.Security.AccessControl.DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            var inherit = System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit;
            security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null),
                System.Security.AccessControl.FileSystemRights.FullControl, inherit, System.Security.AccessControl.PropagationFlags.None,
                System.Security.AccessControl.AccessControlType.Allow));
            security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalSystemSid, null),
                System.Security.AccessControl.FileSystemRights.FullControl, inherit, System.Security.AccessControl.PropagationFlags.None,
                System.Security.AccessControl.AccessControlType.Allow));
            security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                me, System.Security.AccessControl.FileSystemRights.ReadAndExecute, inherit, System.Security.AccessControl.PropagationFlags.None,
                System.Security.AccessControl.AccessControlType.Allow));
            System.IO.FileSystemAclExtensions.SetAccessControl(dir, security);
    
            NUnit.Framework.Assert.That(Instella.Installer.Runtime.Core.Security.InstallFolderAcl.FindNonAdminWriter(app), NUnit.Framework.Is.Not.Null,
                "the installing user owns the folder and can delete/rename it through its parent, so it can swap the service exe");
        }
        finally
        {
            try
            {
                var restore = new System.Security.AccessControl.DirectorySecurity();
                restore.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(me,
                    System.Security.AccessControl.FileSystemRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
                System.IO.FileSystemAclExtensions.SetAccessControl(dir, restore);
                System.IO.Directory.Delete(parent, recursive: true);
            }
            catch { }
        }
    #pragma warning restore CA1416
    }
}
