namespace Instella.Core.FileSystem;

/// <summary>
/// The one place that turns an untrusted relative path (from an archive, a patch, a
/// server response or an upload) into a file-system path. Every such call site goes
/// through here so none can be tricked into writing outside its root.
/// </summary>
internal static class SafePath
{
    /// <summary>
    /// Resolves <paramref name="relative"/> under <paramref name="root"/> and guarantees the
    /// result stays inside <paramref name="root"/>. Rejects absolute paths, drive and UNC
    /// roots, <c>.</c>/<c>..</c> and empty segments, NUL, <c>:</c> (NTFS alternate data
    /// streams), Windows reserved device names, and segments ending in '.' or ' '.
    /// </summary>
    /// <exception cref="UnsafePathException">The path is unsafe.</exception>
    public static string Combine(string root, string relative)
    {
        if (!TryNormalizeRelative(relative, out var normalized, out var reason))
            throw new UnsafePathException(relative, reason);

        var fullRoot = Path.GetFullPath(root);
        if (!Path.EndsInDirectorySeparator(fullRoot)) fullRoot += Path.DirectorySeparatorChar;

        var candidate = Path.GetFullPath(Path.Combine(fullRoot, normalized.Replace('/', Path.DirectorySeparatorChar)));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!candidate.StartsWith(fullRoot, comparison))
            throw new UnsafePathException(relative, "escapes the target directory");
        return candidate;
    }

    /// <summary>
    /// Validates <paramref name="relative"/> and produces its canonical wire form: forward
    /// slashes, no leading slash, no <c>.</c> or <c>..</c> segments. The rules apply on every
    /// platform, because a package built on Linux must still install on Windows.
    /// </summary>
    public static bool TryNormalizeRelative(string? relative, out string normalized, out string reason)
    {
        normalized = "";
        reason = "";
        if (string.IsNullOrWhiteSpace(relative)) { reason = "is empty"; return false; }
        if (relative.Contains('\0')) { reason = "contains NUL"; return false; }

        var p = relative.Replace('\\', '/');
        if (p.StartsWith('/') || Path.IsPathRooted(p) || (p.Length >= 2 && p[1] == ':'))
        {
            reason = "is absolute";
            return false;
        }

        var segments = p.Split('/');
        foreach (var s in segments)
        {
            if (s.Length == 0 || s == "." || s == "..") { reason = $"has illegal segment '{s}'"; return false; }
            if (s.Contains(':')) { reason = "contains ':'"; return false; }
            if (s.Any(char.IsControl)) { reason = "contains a control character"; return false; }
            if (IsWindowsReservedName(s)) { reason = $"'{s}' is a reserved device name"; return false; }
            if (s.EndsWith('.') || s.EndsWith(' ')) { reason = $"segment '{s}' ends in '.' or ' '"; return false; }
        }

        normalized = string.Join('/', segments);
        return true;
    }

    /// <summary>True when <paramref name="relative"/> passes <see cref="TryNormalizeRelative"/>.</summary>
    public static bool IsSafeRelative(string? relative) => TryNormalizeRelative(relative, out _, out _);

    private static bool IsWindowsReservedName(string segment)
    {
        var stem = segment.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
            || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
                && (char.IsAsciiDigit(stem[3]) || stem[3] is '¹' or '²' or '³'));
    }
}

/// <summary>An untrusted relative path was rejected by <see cref="SafePath"/>.</summary>
internal sealed class UnsafePathException(string path, string reason)
    : IOException($"Unsafe path '{path}': {reason}")
{
    /// <summary>The rejected path, as received.</summary>
    public string UnsafePath { get; } = path;

    /// <summary>Why it was rejected.</summary>
    public string Reason { get; } = reason;
}
