using System.Diagnostics.CodeAnalysis;

namespace Instella.Core.Wire;

/// <summary>
/// Release channel names: lowercase letters, digits and '-', 1–32 characters, starting and
/// ending with a letter or digit. The builder, the CLI, the SDK, the installer and the server
/// all check names with this one rule.
/// </summary>
public static class ChannelNames
{
    /// <summary>The default channel.</summary>
    public const string Stable = "stable";

    /// <summary>Longest allowed channel name.</summary>
    public const int MaxLength = 32;

    /// <summary>The rule, as an error message.</summary>
    public const string Rule =
        "a channel name is 1-32 characters: lowercase letters, digits and '-', starting and ending with a letter or digit";

    /// <summary>
    /// Trims <paramref name="value"/> and lowercases it (invariant culture), then checks the
    /// rule. <paramref name="channel"/> is the normalised name when this returns true.
    /// </summary>
    public static bool TryNormalize(string? value, [NotNullWhen(true)] out string? channel)
    {
        channel = null;
        if (value is null) return false;
        var s = value.Trim().ToLowerInvariant();
        if (s.Length is 0 or > MaxLength) return false;
        // A loop, not a regex: trivially AOT-safe and fast.
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            var alnum = c is (>= 'a' and <= 'z') or (>= '0' and <= '9');
            if (!alnum && !(c == '-' && i > 0 && i < s.Length - 1)) return false;
        }
        channel = s;
        return true;
    }

    /// <summary>Whether <paramref name="value"/> normalises to a valid channel name.</summary>
    public static bool IsValid(string? value) => TryNormalize(value, out _);
}
