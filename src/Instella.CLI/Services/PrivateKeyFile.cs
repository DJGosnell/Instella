using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace Instella.CLI.Services;

/// <summary>
/// Writes a private key file that is never readable by others, not even for a moment: on
/// Unix it is created with mode 0600; on Windows it is created with a protected ACL granting only
/// the current user and SYSTEM (nothing inherited from the folder).
/// </summary>
internal static class PrivateKeyFile
{
    /// <summary>Writes <paramref name="content"/> to a new file at <paramref name="path"/> (an existing file is replaced).</summary>
    public static void Write(string path, string content)
    {
        if (File.Exists(path)) File.Delete(path);
        var bytes = Encoding.UTF8.GetBytes(content);
        using var stream = OperatingSystem.IsWindows()
            ? CreateProtectedOnWindows(path)
            : new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            });
        stream.Write(bytes);
    }

    [SupportedOSPlatform("windows")]
    private static FileStream CreateProtectedOnWindows(string path)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl, AccessControlType.Allow));
        return new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.Write | FileSystemRights.ReadPermissions,
            FileShare.None, 4096, FileOptions.None, security);
    }

    /// <summary>The git work tree <paramref name="path"/> is inside (a <c>.git</c> folder or file above it), or null.</summary>
    public static string? GitWorkTreeOf(string path)
    {
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(path))!); dir is not null; dir = dir.Parent)
        {
            var git = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(git) || File.Exists(git)) return dir.FullName;
        }
        return null;
    }
}
