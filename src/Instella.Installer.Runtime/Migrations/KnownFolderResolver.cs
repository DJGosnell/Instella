using System;
using System.Collections.Generic;
using Instella.Core.Installation;

namespace Instella.Installer.Runtime.Migrations;

/// <summary>
/// Turns a <see cref="KnownFolder"/> into a path for the scope being installed. The lookup is a
/// seam: the host's uses <see cref="Environment.GetFolderPath(Environment.SpecialFolder)"/>; tests
/// and <c>MigrationHarness</c> pass fake roots.
/// </summary>
internal sealed class KnownFolderResolver
{
    private readonly Func<KnownFolder, bool, string?> _lookup;

    /// <param name="lookup">(folder, machine scope) → the folder's path, or null when there is none.
    /// Never asked for <see cref="KnownFolder.InstallFolder"/>, nor for a per-user folder in machine scope.</param>
    public KnownFolderResolver(Func<KnownFolder, bool, string?> lookup)
    {
        _lookup = lookup;
    }

    /// <summary>The host's folders.</summary>
    public static KnownFolderResolver Host { get; } = new(HostLookup);

    /// <summary>Folders that belong to the user running the installer.</summary>
    public static bool IsPerUser(KnownFolder folder) => folder is KnownFolder.LocalAppData or KnownFolder.RoamingAppData;

    /// <summary>Why <paramref name="folder"/> cannot be used in <paramref name="scope"/>, or null.</summary>
    public static string? UnavailableReason(KnownFolder folder, InstallationScope scope) =>
        IsPerUser(folder) && scope == InstallationScope.SystemWide
            ? $"{folder} is a per-user folder and this is a machine-wide install, which may run as another account"
            : null;

    /// <summary>The path of <paramref name="folder"/>, or null when it is unavailable in this scope or on this platform.</summary>
    public string? Resolve(KnownFolder folder, InstallationScope scope, string installPath)
    {
        if (folder == KnownFolder.InstallFolder) return installPath;
        if (UnavailableReason(folder, scope) is not null) return null;
        var path = _lookup(folder, scope == InstallationScope.SystemWide);
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    /// <summary>Every known folder's root that resolves in <paramref name="scope"/>, other than the install folder.</summary>
    public IReadOnlyList<string> Roots(InstallationScope scope)
    {
        var roots = new List<string>();
        foreach (var folder in Enum.GetValues<KnownFolder>())
        {
            if (folder == KnownFolder.InstallFolder) continue;
            if (Resolve(folder, scope, "") is { } path) roots.Add(path);
        }
        return roots;
    }

    private static string? HostLookup(KnownFolder folder, bool machine) => folder switch
    {
        KnownFolder.LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        KnownFolder.RoamingAppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        KnownFolder.StartMenuPrograms => Environment.GetFolderPath(machine ? Environment.SpecialFolder.CommonPrograms : Environment.SpecialFolder.Programs),
        KnownFolder.ProgramFiles => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        KnownFolder.ProgramFilesX86 => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        KnownFolder.ProgramData => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        _ => null,
    };
}
