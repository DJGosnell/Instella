using Instella.Installer.Runtime.Runners;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Services;

public class InstallFolderAclInheritOnlyTests
{
    [Test, Platform("Win"), System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void AFolderWhoseFilesUsersCanModify_IsReported()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "instella-acl-probe", System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        var info = new System.IO.DirectoryInfo(dir);
        var me = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        try
        {
            var security = new System.Security.AccessControl.DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null),
                System.Security.AccessControl.FileSystemRights.FullControl,
                System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                System.Security.AccessControl.PropagationFlags.None,
                System.Security.AccessControl.AccessControlType.Allow));
            security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinUsersSid, null),
                System.Security.AccessControl.FileSystemRights.Modify,
                System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                System.Security.AccessControl.PropagationFlags.InheritOnly,
                System.Security.AccessControl.AccessControlType.Allow));
            System.IO.FileSystemAclExtensions.SetAccessControl(info, security);
    
            NUnit.Framework.Assert.That(Instella.Installer.Runtime.Core.Security.InstallFolderAcl.FindNonAdminWriter(dir), NUnit.Framework.Is.Not.Null,
                "BUILTIN\\Users can modify every file in the folder, so a user could replace the service's executable");
        }
        finally
        {
            var reset = new System.Security.AccessControl.DirectorySecurity();
            reset.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            reset.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(me,
                System.Security.AccessControl.FileSystemRights.FullControl,
                System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                System.Security.AccessControl.PropagationFlags.None,
                System.Security.AccessControl.AccessControlType.Allow));
            try { System.IO.FileSystemAclExtensions.SetAccessControl(info, reset); System.IO.Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
