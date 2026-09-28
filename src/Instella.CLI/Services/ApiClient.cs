using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Net.Http.Json;
using Instella.Core.Platform;
using Instella.Core.Trust;
using Instella.Core.Wire;

namespace Instella.CLI.Services;

/// <summary>
/// Read and management calls against the Instella API. URLs come from
/// <see cref="ApiRoutes"/>; bodies are read with <see cref="WireJsonContext"/>.
/// </summary>
public class ApiClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly Uri _server;

    public ApiClient(string serverUrl, string apiKey)
        : this(new HttpClient { Timeout = TimeSpan.FromMinutes(5) }, serverUrl, apiKey)
    {
    }

    internal ApiClient(HttpClient httpClient, string serverUrl, string apiKey)
    {
        _server = new Uri(serverUrl, UriKind.Absolute);
        _httpClient = httpClient;
        if (!string.IsNullOrEmpty(apiKey))
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    public Task<ApiResult<PackageSummary[]>> ListPackagesAsync(CancellationToken ct = default) =>
        SendAsync(() => _httpClient.GetAsync(ApiRoutes.ForPackages(_server), ct), WireJsonContext.Default.PackageSummaryArray, ct);

    public Task<ApiResult<VersionSummary[]>> ListVersionsAsync(string packageId, CancellationToken ct = default) =>
        SendAsync(() => _httpClient.GetAsync(ApiRoutes.ForPackageVersions(_server, packageId), ct),
            WireJsonContext.Default.VersionSummaryArray, ct);

    public Task<ApiResult<MessageResponse>> DeleteVersionAsync(
        string packageId,
        string version,
        CancellationToken ct = default) =>
        SendAsync(() => _httpClient.DeleteAsync(ApiRoutes.ForPackageVersion(_server, packageId, version), ct),
            WireJsonContext.Default.MessageResponse, ct);

    /// <summary>The unsigned manifest of a draft build.</summary>
    public Task<ApiResult<DraftResponse>> GetDraftAsync(
        string packageId, Version version, TargetPlatform os, Architecture arch, CancellationToken ct = default) =>
        SendAsync(() => _httpClient.GetAsync(ApiRoutes.ForDraft(_server, packageId, version, os, arch), ct),
            WireJsonContext.Default.DraftResponse, ct);

    /// <summary>Publishes a draft build with its signed release; the answer says whether it went live or is pending.</summary>
    public Task<ApiResult<PublishDraftResponse>> PublishDraftAsync(
        string packageId, Version version, TargetPlatform os, Architecture arch, SignedRelease release, CancellationToken ct = default) =>
        SendAsync(() => _httpClient.PostAsJsonAsync(ApiRoutes.ForPublishDraft(_server, packageId, version, os, arch), release,
            WireJsonContext.Default.SignedRelease, ct), WireJsonContext.Default.PublishDraftResponse, ct);

    /// <summary>The package's drafts and releases pending approval (approve permission).</summary>
    public Task<ApiResult<UnpublishedReleaseSummary[]>> ListUnpublishedAsync(string packageId, CancellationToken ct = default) =>
        SendAsync(() => _httpClient.GetAsync(ApiRoutes.ForApprovals(_server, packageId), ct),
            WireJsonContext.Default.UnpublishedReleaseSummaryArray, ct);

    /// <summary>One draft or pending release with its manifest (approve permission).</summary>
    public Task<ApiResult<UnpublishedReleaseResponse>> GetUnpublishedAsync(
        string packageId, Version version, TargetPlatform os, Architecture arch, CancellationToken ct = default) =>
        SendAsync(() => _httpClient.GetAsync(ApiRoutes.ForApproval(_server, packageId, version, os, arch), ct),
            WireJsonContext.Default.UnpublishedReleaseResponse, ct);

    /// <summary>Approves the pending release whose manifest has <paramref name="manifestSha256"/>.</summary>
    public Task<ApiResult<MessageResponse>> ApproveAsync(
        string packageId, Version version, TargetPlatform os, Architecture arch, string manifestSha256, CancellationToken ct = default) =>
        SendAsync(() => _httpClient.PostAsJsonAsync(ApiRoutes.ForApproveRelease(_server, packageId, version, os, arch),
            new ApproveReleaseRequest { ManifestSha256 = manifestSha256 }, WireJsonContext.Default.ApproveReleaseRequest, ct),
            WireJsonContext.Default.MessageResponse, ct);

    /// <summary>Rejects (deletes) a pending release or a draft.</summary>
    public Task<ApiResult<MessageResponse>> RejectAsync(
        string packageId, Version version, TargetPlatform os, Architecture arch, string? manifestSha256, string? reason,
        CancellationToken ct = default) =>
        SendAsync(() => _httpClient.PostAsJsonAsync(ApiRoutes.ForRejectRelease(_server, packageId, version, os, arch),
            new RejectReleaseRequest { ManifestSha256 = manifestSha256, Reason = reason }, WireJsonContext.Default.RejectReleaseRequest, ct),
            WireJsonContext.Default.MessageResponse, ct);

    /// <summary>Sends a request; a transport failure becomes a failed result, not an exception.</summary>
    private static async Task<ApiResult<T>> SendAsync<T>(
        Func<Task<HttpResponseMessage>> send, JsonTypeInfo<T> typeInfo, CancellationToken ct)
    {
        try
        {
            using var response = await send();
            return await HandleResponseAsync(response, typeInfo, ct);
        }
        catch (HttpRequestException ex)
        {
            return ApiResult<T>.Failure($"cannot reach the server: {ex.Message}");
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            return ApiResult<T>.Failure($"the server did not respond in time: {ex.Message}");
        }
    }

    internal static async Task<ApiResult<T>> HandleResponseAsync<T>(
        HttpResponseMessage response, JsonTypeInfo<T> typeInfo, CancellationToken ct)
    {
        var content = await response.Content.ReadAsStringAsync(ct);

        if (response.IsSuccessStatusCode)
        {
            try
            {
                var data = JsonSerializer.Deserialize(content, typeInfo);
                return data is null
                    ? ApiResult<T>.Failure("Server returned an empty response", response.StatusCode)
                    : ApiResult<T>.Success(data);
            }
            catch (JsonException ex)
            {
                return ApiResult<T>.Failure($"Failed to parse response: {ex.Message}", response.StatusCode);
            }
        }

        try
        {
            var error = JsonSerializer.Deserialize(content, WireJsonContext.Default.ApiError);
            return ApiResult<T>.Failure(error?.Error ?? $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}", response.StatusCode);
        }
        catch (JsonException)
        {
            return ApiResult<T>.Failure($"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}", response.StatusCode);
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}

public class ApiResult<T>
{
    public bool IsSuccess { get; init; }
    public T? Data { get; init; }
    public string? Error { get; init; }

    /// <summary>HTTP status of the response, when one was received.</summary>
    public System.Net.HttpStatusCode? StatusCode { get; init; }

    public static ApiResult<T> Success(T data) => new() { IsSuccess = true, Data = data };
    public static ApiResult<T> Failure(string error, System.Net.HttpStatusCode? status = null) =>
        new() { IsSuccess = false, Error = error, StatusCode = status };
}
