namespace Instella.Server.Extensions;

public static class HttpContextExtensions
{
    /// <summary>
    /// The client's IP address. Forwarded headers are honoured only through
    /// <c>UseForwardedHeaders</c>, which rewrites <see cref="ConnectionInfo.RemoteIpAddress"/> when,
    /// and only when, the immediate peer is a configured proxy. Headers from anyone
    /// else are ignored, so a client cannot pick the address its rate limit is keyed on.
    /// Normalised (<see cref="IpAddresses.Normalize"/>): one spelling for bans, rate limits and logs.
    /// </summary>
    public static string GetClientIpAddress(this HttpContext context) =>
        IpAddresses.Normalize(context.Connection.RemoteIpAddress);
}
