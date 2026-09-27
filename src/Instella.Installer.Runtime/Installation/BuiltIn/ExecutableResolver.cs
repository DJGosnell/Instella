using System;
using System.IO;
using System.Linq;
using Instella.Core.FileSystem;
using Instella.Core.Installation;
using Instella.Core.Manifest;

namespace Instella.Installer.Runtime.Installation.BuiltIn;

/// <summary>
/// The app's main executable and icon under the install path. Both are resolved at build
/// time: the build writes <c>executableName</c> into the manifest and embeds the
/// icon as <c>.instella/app.ico</c>, so nothing is guessed from the files on disk.
/// </summary>
internal static class ExecutableResolver
{
    /// <summary>
    /// <c>{InstallPath}/{ExecutableName}</c>. Without an executable name (a build that could
    /// not tell, which warns INSTELLA0103) it falls back to <c>{AppName}.exe</c>. On Windows a
    /// name without <c>.exe</c> (<c>WithExecutableName("QuickNotes")</c>) gets it appended:
    /// otherwise every shortcut, association and auto-start entry points at a missing file.
    /// </summary>
    public static string Resolve(InstallContext context)
    {
        if (context.ExecutablePath is { } known) return known;
        var name = string.IsNullOrWhiteSpace(context.Manifest.ExecutableName)
            ? $"{context.Manifest.AppName}.exe"
            : context.Manifest.ExecutableName!;
        return SafePath.Combine(context.InstallPath, WithPlatformExtension(name));
    }

    /// <summary>Appends <c>.exe</c> on Windows when <paramref name="name"/> lacks it.</summary>
    internal static string WithPlatformExtension(string name) =>
        OperatingSystem.IsWindows() && !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? name + ".exe"
            : name;

    /// <summary>
    /// Full path of the app icon: a payload-relative <c>IconPath</c> resolves under the
    /// install path; null when the installer has no icon, or when the embedded icon is not
    /// part of this installation (a lite installer's server payload never carries it). The
    /// decision uses what this install extracted (still staged during the transaction) or the
    /// installation it replaces, so the path is right before the commit puts the file there.
    /// </summary>
    public static string? Icon(InstallContext context) => ResolveIcon(context, context.Manifest.IconPath);

    /// <summary>
    /// A file association's icon: its own payload-relative or absolute path, else the app icon.
    /// A path that is not a safe payload-relative path falls back to the app icon.
    /// </summary>
    public static string? AssociationIcon(InstallContext context, FileAssociation association) =>
        ResolveIcon(context, association.IconPath) ?? Icon(context);

    private static string? ResolveIcon(InstallContext context, string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        // An absolute icon must exist on this machine; the shortcut creator does not check.
        if (Path.IsPathRooted(path)) return context.FileSystem.Exists(path) ? path : null;
        if (!SafePath.TryNormalizeRelative(path, out var relative, out _)) return null;
        if (string.Equals(relative, InstellaOwnedPaths.AppIcon, StringComparison.OrdinalIgnoreCase)
            && !Carries(context, relative))
            return null;
        return SafePath.Combine(context.InstallPath, relative);
    }

    /// <summary>Whether this install, or the one it replaces, put <paramref name="relative"/> in place.</summary>
    private static bool Carries(InstallContext context, string relative) =>
        context.ExtractedFiles.Concat(context.ExistingInstallation?.Files ?? [])
            .Any(f => string.Equals(f.RelativePath.Replace('\\', '/'), relative, StringComparison.OrdinalIgnoreCase));

    /// <summary>The previous installation's main executable, for restoring its registrations.</summary>
    public static string PreviousExecutable(InstallContext context) =>
        context.ExistingInstallation is { } prev
            ? Path.Combine(prev.InstallDirectory, prev.ExecutableName)
            : Resolve(context);
}
