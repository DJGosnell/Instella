using System.Net;

namespace Instella.Core.Wire;

/// <summary>
/// The transport rule for an Instella server URL, enforced by the builder, the installed
/// runtime, the SDK and the CLI: absolute, and <c>https</c> — or <c>http</c> to a loopback
/// host for local development. Any other <c>http</c> URL needs an explicit opt-in.
/// </summary>
public static class ServerUrlPolicy
{
    /// <summary>Returns null when <paramref name="url"/> is acceptable, otherwise the reason it is not.</summary>
    /// <param name="url">The server URL.</param>
    /// <param name="allowInsecure">The author called <c>AllowInsecureServer()</c> / passed <c>--allow-insecure</c>.</param>
    public static string? Check(string? url, bool allowInsecure)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "server URL is empty";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return $"server URL '{url}' is not an absolute URI";
        if (uri.Scheme == Uri.UriSchemeHttps)
            return null;
        if (uri.Scheme != Uri.UriSchemeHttp)
            return $"server URL '{url}' must use https";
        if (IsLoopback(uri) || allowInsecure)
            return null;
        return $"server URL '{url}' uses plain http; use https or a loopback host, or opt in with AllowInsecureServer() in an installer or --allow-insecure on the command line";
    }

    /// <summary>True when the URL is plain http to a non-loopback host (worth a warning even when allowed).</summary>
    public static bool IsInsecure(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttp && !IsLoopback(uri);

    private static bool IsLoopback(Uri uri)
    {
        if (uri.IsLoopback) return true;
        return IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip);
    }
}
