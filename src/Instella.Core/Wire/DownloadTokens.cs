using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace Instella.Core.Wire;

/// <summary>
/// Download tokens (<c>idt_…</c>): download-only credentials for one private package, compiled
/// into installers. <c>idt_</c> plus base64url of 32 random bytes, 47 characters. The prefix
/// tells a token from an API key (88 characters of standard base64) in the one
/// <c>Authorization: Bearer</c> header, and makes a leaked token easy to recognise.
/// </summary>
internal static class DownloadTokens
{
    /// <summary>What every token starts with.</summary>
    public const string Prefix = "idt_";

    /// <summary>Length of a token.</summary>
    public const int Length = 47;

    /// <summary>A new random token.</summary>
    public static string NewToken() => Prefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>Whether <paramref name="value"/> has a token's shape (not whether it is valid).</summary>
    public static bool LooksLikeToken([NotNullWhen(true)] string? value) =>
        value is { Length: Length } && value.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>The stored form: lowercase-hex SHA-256 of the whole token.</summary>
    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>The part shown in the admin UI: the first 8 characters after the prefix.</summary>
    public static string DisplayPrefix(string token) => token.Substring(Prefix.Length, 8);
}
