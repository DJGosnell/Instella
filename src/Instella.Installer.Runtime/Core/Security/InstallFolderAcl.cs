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
/// entry that grants write, append, write-attributes, write-EA, delete, delete-child, write-DAC,
/// write-owner, generic write or generic all to Users, Authenticated Users, Everyone,
/// INTERACTIVE or the current user. An existing folder's inherit-only entries count, since they
/// reach the files installed in it. The folder is also reported when one of those accounts owns
/// it, or owns or has delete-child on the folder holding it (and could swap it for its own).
/// CREATOR OWNER is ignored: an elevated installer's new folders are owned by Administrators.
/// Off Windows nothing is reported.
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
        | 0x10000     // DELETE
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

        var security = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);

        // The owner may always change the permissions, so a folder the user owns is theirs to open up.
        if (OwnerOf(security) is { } owner && risky.Contains(owner)) return NameOf(owner);

        foreach (var (sid, rule) in RiskyAllows(security, risky))
        {
            bool reaches;
            if (depth == 0)
            {
                // The folder and what it holds: an inherit-only entry still reaches the files
                // (the service's exe among them) and the folders extracted into it.
                reaches = (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0
                    || rule.InheritanceFlags != InheritanceFlags.None;
            }
            else if ((rule.PropagationFlags & PropagationFlags.NoPropagateInherit) != 0)
            {
                // A no-propagate entry reaches only the first level, and only a container entry
                // reaches a folder.
                reaches = depth == 1 && (rule.InheritanceFlags & InheritanceFlags.ContainerInherit) != 0;
            }
            else
            {
                // A folder still to be created gets the container-inheritable entries; object-only
                // ones come along as inherit-only and reach the files inside it.
                reaches = rule.InheritanceFlags != InheritanceFlags.None;
            }
            if (reaches && ((int)rule.FileSystemRights & WriteMask) != 0) return NameOf(sid);

            // Delete-child on the folder that holds the install folder lets the user rename or
            // delete the install folder and put their own in its place.
            if (depth == 1 && AppliesToItself(rule) && ((int)rule.FileSystemRights & DeleteChildMask) != 0)
                return NameOf(sid);
        }

        if (depth == 0 && Path.GetDirectoryName(path) is { Length: > 0 } holder && Directory.Exists(holder))
        {
            var parentSecurity = new DirectoryInfo(holder).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
            if (OwnerOf(parentSecurity) is { } parentOwner && risky.Contains(parentOwner)) return NameOf(parentOwner);
            foreach (var (sid, rule) in RiskyAllows(parentSecurity, risky))
                if (AppliesToItself(rule) && ((int)rule.FileSystemRights & DeleteChildMask) != 0)
                    return NameOf(sid);
        }
        return null;
    }

    private const int DeleteChildMask =
        0x0040        // FILE_DELETE_CHILD
        | 0x10000000; // GENERIC_ALL

    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier? OwnerOf(DirectorySecurity security)
    {
        try
        {
            return security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        }
        catch (Exception ex) when (ex is IdentityNotMappedException or SystemException)
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<(SecurityIdentifier Sid, FileSystemAccessRule Rule)> RiskyAllows(
        DirectorySecurity security, HashSet<SecurityIdentifier> risky)
    {
        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            if (rule.IdentityReference is SecurityIdentifier sid && risky.Contains(sid)) yield return (sid, rule);
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool AppliesToItself(FileSystemAccessRule rule) =>
        (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0;

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
