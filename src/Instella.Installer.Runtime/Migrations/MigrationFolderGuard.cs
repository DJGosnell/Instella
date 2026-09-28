using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Instella.Core.FileSystem;
using Instella.Core.Installation;
using Instella.Installer.Runtime.Core;
using Instella.Installer.Runtime.Runners;

namespace Instella.Installer.Runtime.Migrations;

/// <summary>A resolved folder path, or why it may not be used.</summary>
internal readonly record struct FolderResolution(string? Path, string? Refusal);

/// <summary>
/// The migration safety rules for paths. Every path is a known folder plus a relative path,
/// normalised; conditions only need that. Actions also refuse: the install folder, anything inside
/// or above it, volume roots and protected folders (and their ancestors), and folders holding
/// another Instella installation.
/// </summary>
internal static class MigrationFolderGuard
{
    /// <summary>Folders under a known folder that belong to Windows or to every app, never one app.</summary>
    private static readonly (KnownFolder Root, string Relative)[] SharedSubfolders =
    [
        (KnownFolder.LocalAppData, "Programs"),
        (KnownFolder.LocalAppData, "Microsoft"),
        (KnownFolder.LocalAppData, "Packages"),
        (KnownFolder.LocalAppData, "Temp"),
        (KnownFolder.RoamingAppData, "Microsoft"),
    ];

    /// <summary>Resolves <paramref name="folder"/>; with <paramref name="forAction"/> also applies the action refusals.</summary>
    public static FolderResolution Resolve(MigrationFolder folder, MigrationContext context, bool forAction) =>
        Resolve(folder.Root, folder.RelativePath, context, forAction);

    /// <summary>Resolves <paramref name="relative"/> (already validated) under <paramref name="root"/>.</summary>
    public static FolderResolution Resolve(KnownFolder root, string relative, MigrationContext context, bool forAction)
    {
        var rootPath = context.GetFolderPath(root);
        if (rootPath is null)
            return new(null, KnownFolderResolver.UnavailableReason(root, context.Scope) ?? $"{root} does not exist on this platform");

        string combined;
        try
        {
            combined = SafePath.Combine(rootPath, relative);
        }
        catch (UnsafePathException ex)
        {
            return new(null, ex.Message);
        }
        if (!InstallPaths.TryNormalize(combined, requireRooted: true, out var path, out var problem))
            return new(null, problem);
        if (!forAction) return new(path, null);

        var display = $"{root}/{relative}";
        if (root == KnownFolder.InstallFolder)
            return new(null, $"{display}: the folder being installed is for conditions only; the install itself owns what is in it");
        if (PathGuards.RefusalFor(path, ExtraProtected(context)) is { } refusal)
            return new(null, $"{display}: {refusal}");
        if (InstallPaths.TryNormalize(context.InstallPath, requireRooted: false, out var install, out _))
        {
            if (PathGuards.IsSameOrInside(path, install))
                return new(null, $"{display} is the folder being installed, or inside it");
            if (PathGuards.IsSameOrInside(install, path))
                return new(null, $"{display} contains the folder being installed");
        }
        if (OtherInstallation(path, context) is { } installation)
            return new(null, $"{display}: {installation}; uninstall it instead");
        return new(path, null);
    }

    /// <summary>
    /// Why <paramref name="path"/> touches another Instella installation, or null: the folder
    /// holds one, is inside one (an ancestor holds the manifest), or has one anywhere beneath it
    /// (actions take nested file names and close programs from the whole tree). A tree that cannot
    /// be listed counts as touching one: the rule cannot be checked, so the action is refused.
    /// </summary>
    private static string? OtherInstallation(string path, MigrationContext context)
    {
        var fs = context.FileSystem;
        for (var dir = path; !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
        {
            if (fs.Exists(Path.Combine(dir, InstellaOwnedPaths.InstalledManifest)))
                return dir == path
                    ? $"it holds an Instella installation ({InstellaOwnedPaths.InstalledManifest})"
                    : $"it is inside the Instella installation in '{dir}'";
        }

        if (!fs.DirectoryExists(path)) return null;
        try
        {
            var nested = fs.EnumerateFiles(path, InstellaOwnedPaths.InstalledManifest, recursive: true).FirstOrDefault();
            return nested is null ? null : $"it contains the Instella installation in '{Path.GetDirectoryName(nested)}'";
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return $"it cannot be checked for Instella installations ({ex.Message})";
        }
    }

    private static IEnumerable<string> ExtraProtected(MigrationContext context)
    {
        foreach (var r in context.Runtime.Folders.Roots(context.Scope))
            yield return r;
        foreach (var (root, relative) in SharedSubfolders)
            if (context.GetFolderPath(root) is { } rootPath)
                yield return Path.Combine(rootPath, relative);
    }
}
