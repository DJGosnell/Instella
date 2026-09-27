using System.Net.Http.Json;
using Instella.Core.Platform;
using Instella.Core.Trust;
using Instella.Core.Update;
using Instella.Core.Utilities;
using Instella.Core.Wire;

namespace Instella.Sdk.Internal;

/// <summary>
/// HTTP-based implementation of <see cref="IUpdateClient"/>. URLs come from
/// <see cref="ApiRoutes"/>; responses are read with <see cref="WireJsonContext"/>. An update is
/// reported only when its signed release verifies against the installation's trusted keys
///, so the updater is never launched for a release it would refuse.
/// </summary>
internal sealed class HttpUpdateClient : IUpdateClient
{
    private readonly HttpClient _http;

    public HttpUpdateClient()
        : this(CreateDefaultHttpClient())
    {
    }

    /// <param name="http">Client used for every request.</param>
    internal HttpUpdateClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<UpdateCheckResult> CheckAsync(InstellaInfo info, string channel, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(info.ServerUrl))
            return UpdateCheckResult.Failed("the installation has no update server");
        // The installed manifest is user-writable for per-user installs, so the URL rule the
        // builder enforced is checked again before every request.
        if (ServerUrlPolicy.Check(info.ServerUrl, info.AllowInsecureServer) is { } urlProblem)
            return UpdateCheckResult.Failed(urlProblem);

        var arch = info.Architecture ?? ArchitectureExtensions.Current;
        var url = ApiRoutes.ForCheckUpdate(
            new Uri(info.ServerUrl, UriKind.Absolute), info.AppId, info.Version, info.Platform, arch, channel);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(info.DownloadToken))
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", info.DownloadToken);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return UpdateCheckResult.Failed($"Server returned {(int)response.StatusCode}: {errorContent}");
        }

        var dto = await response.Content.ReadFromJsonAsync(WireJsonContext.Default.CheckUpdateResponse, ct)
            .ConfigureAwait(false);
        if (dto is null)
            return UpdateCheckResult.Failed("Invalid response from server");
        if (!dto.UpdateAvailable || dto.Version is null)
            return UpdateCheckResult.NoUpdate();
        if (!AppVersions.TryParse(dto.Version, out var version))
            return UpdateCheckResult.Failed($"Server reported an unparseable version '{dto.Version}'");

        if (Verify(info, dto, version, channel) is { } trustError)
            return UpdateCheckResult.Failed($"The offered update was rejected: {trustError}");

        return UpdateCheckResult.Available(new UpdateInfo
        {
            Version = version,
            Changelog = dto.Changelog ?? "",
            FullSize = dto.FullSize ?? 0,
            PatchAvailable = dto.PatchAvailable,
            PatchSize = dto.PatchSize,
            PatchSha256 = dto.PatchSha256,
            Mandatory = dto.Mandatory,
            Channel = channel,
        });
    }

    /// <summary>
    /// The embedded signed release must verify against the installed manifest's keys, be
    /// strictly newer than the installed version and be on the requested channel. Returns why not, or null.
    /// </summary>
    private static string? Verify(InstellaInfo info, CheckUpdateResponse dto, Version offered, string channel)
    {
        if (info.TrustedKeys.Count == 0)
            return info.AllowUnsignedUpdates ? null : "the installation trusts no publisher keys";
        if (dto.Release is null)
            return "the server sent no signed release";
        try
        {
            _ = ReleaseVerifier.Verify(dto.Release, new TrustPolicy(
                info.TrustedKeys, info.AppId, PlatformStrings.Os(info.Platform),
                PlatformStrings.Arch(info.Architecture ?? ArchitectureExtensions.Current),
                MustBeNewerThan: info.Version, MustEqual: offered) { Channel = channel });
            return null;
        }
        catch (UpdateTrustException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// One client for every check. The token is set per request, for the installation's own
    /// server, so a <see cref="InstellaClient.DownloadTokenOverride"/> takes effect at once.
    /// </summary>
    private static HttpClient CreateDefaultHttpClient() =>
        ServerHttp.Create(server: null, downloadToken: null, "Instella-SDK/1.0", TimeSpan.FromSeconds(60));
}
