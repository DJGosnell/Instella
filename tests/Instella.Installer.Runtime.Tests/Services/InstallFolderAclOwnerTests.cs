using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using Instella.Installer.Runtime.Core.Security;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Services;

/// <summary>A folder the installing user owns can have its permissions changed by that user, whatever its DACL says.</summary>
public partial class InstallFolderAclTests
{
    [Test]
    public void AnInstallFolderOwnedByTheInstallingUser_InAParentTheUserCanWrite_IsReported()
    {
        var app = Path.Combine(_temp, "App");
        Directory.CreateDirectory(app);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        Protect(app,
            Allow(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inherit),
            Allow(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit),
            Allow(Me, FileSystemRights.ReadAndExecute, inherit));
        try
        {
            Assert.That(InstallFolderAcl.FindNonAdminWriter(app), Is.Not.Null,
                "the installing user owns the folder and can delete/rename it through its parent, so it can swap the service exe");
        }
        finally
        {
            Release(app);
        }
    }
}
