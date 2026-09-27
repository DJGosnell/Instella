using System.Net;
using System.Net.Http.Headers;

namespace Instella.Core.Wire;

/// <summary>How clients talk to the update server.</summary>
internal static class ServerHttp
{
    /// <summary>
    /// The largest buffered response a client accepts (<c>HttpClient.MaxResponseContentBufferSize</c>):
    /// check-update, version lists, release and patch manifests are all far smaller. Streamed
    /// downloads are bounded by the signed sizes instead (<see cref="Utilities.LengthLimitedStream"/>).
    /// </summary>
    public const int MaxJsonResponseBytes = 4 * 1024 * 1024;

    /// <summary>
    /// The one way clients build an <see cref="HttpClient"/> for the update server: the user
    /// agent, the timeout, the response buffer cap, and the download token, which
    /// <see cref="ServerAuthHandler"/> adds only to requests for <paramref name="server"/> itself.
    /// </summary>
    /// <param name="server">The update server; null sends no token.</param>
    /// <param name="downloadToken">The download token (<c>idt_…</c>), or null.</param>
    /// <param name="userAgent">Product token, e.g. <c>Instella-Updater/1.0</c>.</param>
    /// <param name="timeout">Whole-request timeout.</param>
    /// <param name="inner">The handler to send through (tests); null creates a real one, owned by the client.</param>
    public static HttpClient Create(Uri? server, string? downloadToken, string userAgent, TimeSpan timeout,
        HttpMessageHandler? inner = null)
    {
        HttpMessageHandler handler = inner ?? new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        };
        if (server is not null && !string.IsNullOrEmpty(downloadToken))
            handler = new ServerAuthHandler(server, downloadToken) { InnerHandler = handler };
        var client = new HttpClient(handler, disposeHandler: inner is null)
        {
            Timeout = timeout,
            MaxResponseContentBufferSize = MaxJsonResponseBytes,
        };
        client.DefaultRequestHeaders.UserAgent.Add(ProductInfoHeaderValue.Parse(userAgent));
        return client;
    }
}

/// <summary>
/// Adds the download token to requests for the update server itself (same scheme, host and port),
/// and to nothing else. A presigned redirect to S3 is another host, and <see cref="SocketsHttpHandler"/>
/// drops <c>Authorization</c> on a cross-host redirect, so the token never leaves the server.
/// </summary>
internal sealed class ServerAuthHandler(Uri server, string token) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (IsServer(request.RequestUri, server))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return base.SendAsync(request, ct);
    }

    /// <summary>Whether <paramref name="uri"/> is on <paramref name="server"/> (scheme, host and port).</summary>
    internal static bool IsServer(Uri? uri, Uri server) =>
        uri is not null && Uri.Compare(uri, server, UriComponents.SchemeAndServer, UriFormat.Unescaped,
            StringComparison.OrdinalIgnoreCase) == 0;
}
