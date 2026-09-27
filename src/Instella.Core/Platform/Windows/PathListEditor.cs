namespace Instella.Core.Platform.Windows;

/// <summary>
/// Pure string editing of a <c>;</c>-separated PATH value. Entries are compared exactly
/// (case-insensitive, ignoring a trailing <c>\</c>), never by substring, and are never
/// expanded, so <c>%JAVA_HOME%\bin</c> survives an add/remove round trip unchanged.
/// </summary>
internal static class PathListEditor
{
    /// <summary>Appends <paramref name="directory"/> unless an equal entry is present.</summary>
    /// <returns>The new value, or null when nothing changes.</returns>
    public static string? Add(string current, string directory)
    {
        var entries = Split(current);
        if (entries.Any(e => EntryEquals(e, directory)))
            return null;
        entries.Add(directory);
        return string.Join(';', entries);
    }

    /// <summary>Removes every entry equal to <paramref name="directory"/>.</summary>
    /// <returns>The new value, or null when nothing changes.</returns>
    public static string? Remove(string current, string directory)
    {
        var entries = Split(current);
        var kept = entries.Where(e => !EntryEquals(e, directory)).ToList();
        return kept.Count == entries.Count ? null : string.Join(';', kept);
    }

    /// <summary>Exact entry comparison: trimmed, case-insensitive, trailing separator ignored.</summary>
    public static bool EntryEquals(string a, string b) =>
        string.Equals(Canonical(a), Canonical(b), StringComparison.OrdinalIgnoreCase);

    private static string Canonical(string entry)
    {
        var e = entry.Trim();
        return e.Length > 3 ? e.TrimEnd('\\') : e; // keep "C:\" intact
    }

    private static List<string> Split(string value) =>
        value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
