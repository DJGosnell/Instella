using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using Instella.Installer.Runtime.Core.Security;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Services;

/// <summary>An inherit-only entry on an existing folder still reaches the files installed in it.</summary>
public partial class InstallFolderAclTests
{
    [Test]
    public void AFolderWhoseFilesUsersCanModify_IsReported()
    {
        var dir = Path.Combine(_temp, "App");
        Directory.CreateDirectory(dir);
        Protect(dir,
            Allow(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit),
            Allow(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.Modify,
                InheritanceFlags.ObjectInherit, PropagationFlags.InheritOnly));
        try
        {
            Assert.That(InstallFolderAcl.FindNonAdminWriter(dir), Is.Not.Null,
                "BUILTIN\\Users can modify every file in the folder, so a user could replace the service's executable");
        }
        finally
        {
            Release(dir);
        }
    }
}
