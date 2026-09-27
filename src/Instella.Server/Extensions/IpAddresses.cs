using System.Diagnostics.CodeAnalysis;
using System.Net;

namespace Instella.Server.Extensions;

/// <summary>
/// One spelling per client address: an IPv4 client seen through a dual-stack socket
/// (<c>::ffff:198.51.100.7</c>) is the same client as <c>198.51.100.7</c>, for bans, rate limits
/// and the logs alike.
/// </summary>
public static class IpAddresses
{
    /// <summary>
    /// <c>"unknown"</c> for null; the IPv4 form of an IPv4-mapped IPv6 address; otherwise the
    /// standard text form (compact IPv6).
    /// </summary>
    public static string Normalize(IPAddress? ip) =>
        ip is null ? "unknown"
        : ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4().ToString()
        : ip.ToString();

    /// <summary>Parses <paramref name="text"/> and normalises it; false when it is not an IP address.</summary>
    public static bool TryNormalize(string? text, [NotNullWhen(true)] out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(text) || !IPAddress.TryParse(text.Trim(), out var ip)) return false;
        normalized = Normalize(ip);
        return true;
    }
}
