using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using Instella.Core.Installation;
using Instella.Core.Platform;
using Instella.Installer.Runtime.Builders;

namespace Instella.Installer.Runtime.Runners;

/// <summary>Default install locations per scope, and the one normalisation of install paths.</summary>
internal static class InstallPaths
{
    /// <summary>The folder the running stub lives in (tests override it).</summary>
    internal static Func<string?> StubDirectory { get; set; } =
        () => Path.GetDirectoryName(Environment.ProcessPath);

    /// <summary>
    /// Normalises an install path: refuses empty text and device paths (<c>\\?\</c>,
    /// <c>\\.\</c>); with <paramref name="requireRooted"/> refuses a path that is not fully
    /// qualified, otherwise resolves it against the current directory; resolves <c>..</c>;
    /// strips trailing separators except on a root such as <c>C:\</c>.
    /// </summary>
    public static bool TryNormalize(string? raw, bool requireRooted,
        [NotNullWhen(true)] out string? path, [NotNullWhen(false)] out string? problem)
    {
        path = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            problem = "the install path is empty";
            return false;
        }
        if (raw.StartsWith(@"\\?\", StringComparison.Ordinal) || raw.StartsWith(@"\\.\", StringComparison.Ordinal)
            || raw.StartsWith("//?/", StringComparison.Ordinal) || raw.StartsWith("//./", StringComparison.Ordinal))
        {
            problem = $"'{raw}' is a device path; use an ordinary folder path";
            return false;
        }
        if (requireRooted && !Path.IsPathFullyQualified(raw))
        {
            problem = $"'{raw}' is not a full folder path";
            return false;
        }

        string full;
        try
        {
            full = Path.GetFullPath(raw);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            problem = $"'{raw}' is not a valid folder path: {ex.Message}";
            return false;
        }

        var root = Path.GetPathRoot(full) ?? "";
        var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        path = trimmed.Length < root.Length ? root : trimmed;
        if (path.Length == 0) path = root;
        problem = null;
        return true;
    }

    /// <summary>Whether two normalised paths name the same folder (case-insensitive on Windows).</summary>
    public static bool SameFolder(string a, string b) => string.Equals(
        a, b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>
    /// Per-user: the platform's per-user programs folder + app name. Machine: Program Files
    /// + publisher (when set) + app name; an x86 build on x64 Windows gets Program Files (x86).
    /// </summary>
    public static string Default(FrozenConfig config, IPlatformServices platform, InstallationScope scope)
    {
        if (scope == InstallationScope.PerUser || string.IsNullOrWhiteSpace(config.Publisher))
            return platform.GetDefaultInstallPath(config.AppName, perUser: scope == InstallationScope.PerUser);

        var invalid = Path.GetInvalidFileNameChars();
        var publisher = new string(config.Publisher.Where(c => !invalid.Contains(c)).ToArray()).Trim().TrimEnd('.');
        return platform.GetDefaultInstallPath(
            publisher.Length > 0 ? Path.Combine(publisher, config.AppName) : config.AppName, perUser: false);
    }

    /// <summary>The elevation mode that pins <paramref name="scope"/> for <c>InstallContextFactory.ResolveScope</c>.</summary>
    public static Instella.Core.Manifest.ElevationMode ElevationFor(InstallationScope scope) =>
        scope == InstallationScope.PerUser
            ? Instella.Core.Manifest.ElevationMode.PerUser
            : Instella.Core.Manifest.ElevationMode.SystemWide;
}
