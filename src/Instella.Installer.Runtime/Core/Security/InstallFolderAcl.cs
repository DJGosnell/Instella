using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Instella.Installer.Runtime.Core.Security;

/// <summary>
/// Tells whether someone other than an administrator can change an install folder. A service
/// runs its executable from there, often as LocalSystem: a folder a user can write is a way for
/// that user to run code as the service.
/// </summary>
internal interface IInstallFolderAcl
{
    /// <summary>
    /// The account that can write or modify <paramref name="folder"/> without being an
    /// administrator, or null when nobody can. A folder that does not exist yet is judged by
    /// its nearest existing parent.
    /// </summary>
    string? FindNonAdminWriter(string folder);
}

/// <summary>
/// The real <see cref="IInstallFolderAcl"/>: reads the folder's DACL (or, for a folder that does
/// not exist yet, its nearest existing parent's inheritable entries) and reports the first allow
/// entry that grants write, append, write-attributes, write-EA, delete-child, write-DAC,
/// write-owner, generic write or generic all to Users, Authenticated Users, Everyone,
/// INTERACTIVE or the current user. CREATOR OWNER is ignored: an elevated installer's new
/// folders are owned by Administrators. Off Windows nothing is reported.
/// </summary>
internal sealed class InstallFolderAcl : IInstallFolderAcl
{
    /// <summary>The host's check.</summary>
    public static InstallFolderAcl Host { get; } = new();

    private const int WriteMask =
        0x0002        // FILE_ADD_FILE / WriteData
        | 0x0004      // FILE_ADD_SUBDIRECTORY / AppendData
        | 0x0010      // FILE_WRITE_EA
        | 0x0040      // FILE_DELETE_CHILD
        | 0x0100      // FILE_WRITE_ATTRIBUTES
        | 0x40000     // WRITE_DAC
        | 0x80000     // WRITE_OWNER
        | 0x10000000  // GENERIC_ALL
        | 0x40000000; // GENERIC_WRITE

    string? IInstallFolderAcl.FindNonAdminWriter(string folder) => FindNonAdminWriter(folder);

    /// <inheritdoc cref="IInstallFolderAcl.FindNonAdminWriter"/>
    public static string? FindNonAdminWriter(string folder)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        return OperatingSystem.IsWindows() ? FindOnWindows(folder) : null;
    }

    [SupportedOSPlatform("windows")]
    private static string? FindOnWindows(string folder)
    {
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var depth = 0;
        while (!Directory.Exists(path))
        {
            var parent = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(parent)) return null;
            path = parent;
            depth++;
        }

        var risky = new HashSet<SecurityIdentifier>
        {
            new(WellKnownSidType.BuiltinUsersSid, null),
            new(WellKnownSidType.AuthenticatedUserSid, null),
            new(WellKnownSidType.WorldSid, null),
            new(WellKnownSidType.InteractiveSid, null),
        };
        using (var identity = WindowsIdentity.GetCurrent())
            if (identity.User is { } user) risky.Add(user);

        var rules = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access)
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
        foreach (FileSystemAccessRule rule in rules)
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            if (rule.IdentityReference is not SecurityIdentifier sid || !risky.Contains(sid)) continue;
            if (depth == 0)
            {
                // The folder itself: an inherit-only entry applies to its children, not to it.
                if ((rule.PropagationFlags & PropagationFlags.InheritOnly) != 0) continue;
            }
            else
            {
                // A folder still to be created gets the parent's container-inheritable entries;
                // a no-propagate entry reaches only the first level.
                if ((rule.InheritanceFlags & InheritanceFlags.ContainerInherit) == 0) continue;
                if ((rule.PropagationFlags & PropagationFlags.NoPropagateInherit) != 0 && depth > 1) continue;
            }
            if (((int)rule.FileSystemRights & WriteMask) == 0) continue;
            return NameOf(sid);
        }
        return null;
    }

    [SupportedOSPlatform("windows")]
    private static string NameOf(SecurityIdentifier sid)
    {
        try
        {
            return sid.Translate(typeof(NTAccount)).Value;
        }
        catch (Exception ex) when (ex is IdentityNotMappedException or SystemException)
        {
            return sid.Value;
        }
    }
}
