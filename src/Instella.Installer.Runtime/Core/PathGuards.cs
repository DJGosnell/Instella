using System;
using System.Collections.Generic;
using System.IO;

namespace Instella.Installer.Runtime.Core;

/// <summary>
/// The folders Instella must never delete from wholesale: volume roots, the well-known shell
/// folders, and any ancestor of one. Shared by <c>--cleanup</c> and install migrations so both
/// refuse the same targets.
/// </summary>
internal static class PathGuards
{
    private static StringComparison Comparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>The user's and the machine's well-known folders, and the temp folder.</summary>
    public static IReadOnlyList<string> ProtectedFolders()
    {
        Environment.SpecialFolder[] folders =
        [
            Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.Desktop, Environment.SpecialFolder.MyDocuments,
            Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.Windows,
            Environment.SpecialFolder.System, Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.Programs, Environment.SpecialFolder.CommonPrograms,
        ];
        var list = new List<string>(folders.Length + 1);
        foreach (var f in folders)
            list.Add(Environment.GetFolderPath(f));
        list.Add(Path.GetTempPath());
        return list;
    }

    /// <summary>
    /// Why nothing may be deleted from <paramref name="fullPath"/> as a whole, or null when it may:
    /// never a volume root, nor one of <see cref="ProtectedFolders"/> or
    /// <paramref name="extraProtected"/>, nor an ancestor of one.
    /// </summary>
    public static string? RefusalFor(string fullPath, IEnumerable<string> extraProtected)
    {
        var full = Trim(fullPath);
        var volumeRoot = Trim(Path.GetPathRoot(full) ?? "");
        if (full.Length == 0 || volumeRoot.Equals(full, Comparison))
            return $"'{fullPath}' is a volume root";

        foreach (var special in Concat(ProtectedFolders(), extraProtected))
        {
            var s = Trim(special);
            if (s.Length > 0 && (s.Equals(full, Comparison) || s.StartsWith(full + Path.DirectorySeparatorChar, Comparison)))
                return $"'{fullPath}' is the protected folder '{s}' or one of its ancestors";
        }
        return null;
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="root"/> or inside it (separator-aware).</summary>
    public static bool IsSameOrInside(string path, string root)
    {
        var p = Trim(path);
        var r = Trim(root);
        return r.Length > 0 && (p.Equals(r, Comparison) || p.StartsWith(r + Path.DirectorySeparatorChar, Comparison));
    }

    private static string Trim(string path) => path.TrimEnd('\\', '/');

    private static IEnumerable<string> Concat(IEnumerable<string> a, IEnumerable<string> b)
    {
        foreach (var x in a) yield return x;
        foreach (var x in b) yield return x;
    }
}
