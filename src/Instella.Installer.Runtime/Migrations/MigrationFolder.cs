using System;
using Instella.Core.FileSystem;

namespace Instella.Installer.Runtime.Migrations;

/// <summary>
/// A folder a migration acts on: a <see cref="KnownFolder"/> plus a relative path. Create one with
/// <c>Folder(root, relative)</c> inside an <see cref="InstallMigration"/>. The safety rules are
/// checked each time a condition or action uses it, against the state at that moment.
/// </summary>
public sealed class MigrationFolder
{
    internal MigrationFolder(KnownFolder root, string relative)
    {
        if (!Enum.IsDefined(root))
            throw new ArgumentOutOfRangeException(nameof(root), root, "not a known folder");
        RelativePath = MigrationPaths.NormalizeRelative(relative, nameof(relative));
        Root = root;
    }

    /// <summary>The known folder the path starts from.</summary>
    public KnownFolder Root { get; }

    /// <summary>The path under <see cref="Root"/>, with forward slashes (<c>ExampleApp</c>, <c>Vendor/ExampleApp</c>).</summary>
    public string RelativePath { get; }

    /// <summary><c>LocalAppData/ExampleApp</c>.</summary>
    public override string ToString() => $"{Root}/{RelativePath}";
}

/// <summary>Validation of the relative paths migrations are given.</summary>
internal static class MigrationPaths
{
    /// <summary>
    /// The canonical form of <paramref name="relative"/>: a non-empty relative path without
    /// <c>..</c>, drive letters, device names or wildcards (the <see cref="SafePath"/> rules).
    /// </summary>
    /// <exception cref="ArgumentException">The path is not safe.</exception>
    public static string NormalizeRelative(string? relative, string paramName)
    {
        if (relative is not null && relative.AsSpan().IndexOfAny('*', '?') >= 0)
            throw new ArgumentException($"'{relative}': wildcards are not allowed; name each file", paramName);
        if (!SafePath.TryNormalizeRelative(relative, out var normalized, out var reason))
            throw new ArgumentException($"'{relative}' {reason}; use a relative path under the known folder", paramName);
        return normalized;
    }
}
