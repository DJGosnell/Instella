using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Instella.Core.Utilities;

/// <summary>
/// One spelling per app version. <see cref="Version"/> treats <c>1.3</c>, <c>1.3.0</c> and
/// <c>1.3.0.0</c> as three different values (with <c>1.3 &lt; 1.3.0</c>), so the same release
/// spelled two ways would pass an anti-downgrade check or become two versions on the server.
/// Every parse and comparison of an app version goes through here.
/// </summary>
/// <remarks>
/// The canonical form has three parts, <c>Major.Minor.Build</c>, plus a fourth only when the
/// revision is greater than zero. A missing build or revision (<c>-1</c>) counts as 0.
/// </remarks>
internal static class AppVersions
{
    /// <summary>Three parts, plus a fourth only when the revision is non-zero.</summary>
    public static Version Normalize(Version v)
    {
        var build = Math.Max(v.Build, 0);
        return v.Revision > 0
            ? new Version(v.Major, v.Minor, build, v.Revision)
            : new Version(v.Major, v.Minor, build);
    }

    /// <summary>Parses "1.2", "1.2.3" or "1.2.3.4" (no prefix, no suffix, no whitespace) into canonical form.</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out Version? version)
    {
        version = null;
        if (string.IsNullOrEmpty(text) || text.Length > 50 || text.Trim() != text) return false;
        // Version.TryParse accepts "+1" and inner whitespace; digits and dots only.
        foreach (var c in text)
            if (c is not ((>= '0' and <= '9') or '.')) return false;
        if (!Version.TryParse(text, out var parsed)) return false;
        version = Normalize(parsed);
        return true;
    }

    /// <summary>The canonical string: <c>1.3</c> and <c>1.3.0.0</c> both give <c>1.3.0</c>.</summary>
    public static string ToCanonicalString(Version v) => Normalize(v).ToString();

    /// <summary>Compares by canonical form, so <c>1.3</c> equals <c>1.3.0</c>.</summary>
    public static int Compare(Version a, Version b) => Normalize(a).CompareTo(Normalize(b));

    /// <summary>Whether two versions have the same canonical form.</summary>
    public static bool Equal(Version a, Version b) => Compare(a, b) == 0;

    /// <summary>Fixed-width string that sorts like the version (for SQL ORDER BY): 43 characters.</summary>
    public static string ToSortKey(Version v)
    {
        var n = Normalize(v);
        return string.Create(CultureInfo.InvariantCulture,
            $"{n.Major:D10}.{n.Minor:D10}.{n.Build:D10}.{Math.Max(n.Revision, 0):D10}");
    }
}
