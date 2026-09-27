using System.IO.Compression;
using System.Net.Http.Json;
using Instella.Core.Platform;
using Instella.Core.Trust;
using Instella.Core.Update;
using Instella.Core.Utilities;
using Instella.Core.Wire;

namespace Instella.Installer.Runtime.Core.Update;

/// <summary>
/// HTTP-based implementation for downloading update content from the server.
/// Every URL is built by <see cref="ApiRoutes"/>.
/// </summary>
internal sealed class HttpUpdateDownloader : IUpdateDownloader, IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Uri _server;
    private readonly string _packageId;
    private readonly Version _fromVersion;
    private readonly Version _toVersion;
    private readonly TargetPlatform _platform;
    private readonly Architecture _architecture;

    private readonly string? _expectedPatchSha256;
    private FileStream? _patchArchiveFile;
    private ZipArchive? _patchArchive;
    private string? _patchArchiveFailed;
    private long? _releaseBytes;
    private int _releaseFiles;

    /// <param name="serverUrl">Server base URL.</param>
    /// <param name="packageId">Package id.</param>
    /// <param name="fromVersion">Installed version.</param>
    /// <param name="toVersion">Target version.</param>
    /// <param name="platform">Installed OS.</param>
    /// <param name="architecture">Architecture the installation was installed as (from the installed manifest).</param>
    /// <param name="expectedPatchSha256">SHA-256 the patch archive must have; a mismatch disables patching.</param>
    /// <param name="downloadToken">The installation's download token, sent to <paramref name="serverUrl"/> only.</param>
    public HttpUpdateDownloader(
        string serverUrl,
        string packageId,
        Version fromVersion,
        Version toVersion,
        TargetPlatform platform,
        Architecture architecture,
        string? expectedPatchSha256 = null,
        string? downloadToken = null)
        : this(ServerHttp.Create(new Uri(serverUrl, UriKind.Absolute), downloadToken, "Instella-Updater/1.0", TimeSpan.FromMinutes(30)),
            ownsHttp: true, serverUrl, packageId, fromVersion, toVersion, platform, architecture, expectedPatchSha256)
    {
    }

    internal HttpUpdateDownloader(
        HttpClient http,
        bool ownsHttp,
        string serverUrl,
        string packageId,
        Version fromVersion,
        Version toVersion,
        TargetPlatform platform,
        Architecture architecture,
        string? expectedPatchSha256 = null)
    {
        _expectedPatchSha256 = expectedPatchSha256;
        _http = http;
        _ownsHttp = ownsHttp;
        _server = new Uri(serverUrl, UriKind.Absolute);
        _packageId = packageId;
        _fromVersion = fromVersion;
        _toVersion = toVersion;
        _platform = platform;
        _architecture = architecture;
    }

    /// <summary>Test seam: waits before retrying a 429.</summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    /// <summary>How often a 429 is retried, and the longest wait between tries.</summary>
    internal const int MaxRateLimitRetries = 3;
    internal static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A GET that retries "429 Too Many Requests" up to <see cref="MaxRateLimitRetries"/> times,
    /// waiting as long as <c>Retry-After</c> says (at most 30 s each; 1, 2, 4 s without it). Many
    /// installs behind one NAT address share the server's per-address limit.
    /// </summary>
    private async Task<HttpResponseMessage> GetAsync(Uri url, HttpCompletionOption completion, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var response = await _http.GetAsync(url, completion, ct);
            if (response.StatusCode != System.Net.HttpStatusCode.TooManyRequests || attempt == MaxRateLimitRetries)
                return response;
            var wait = RetryAfter(response, attempt);
            response.Dispose();
            await Delay(wait, ct);
        }
    }

    private static TimeSpan RetryAfter(HttpResponseMessage response, int attempt)
    {
        var header = response.Headers.RetryAfter;
        var wait = header?.Delta
            ?? (header?.Date is { } date ? date - DateTimeOffset.UtcNow : TimeSpan.FromSeconds(1 << attempt));
        return wait < TimeSpan.Zero ? TimeSpan.Zero : wait > MaxRetryAfter ? MaxRetryAfter : wait;
    }

    /// <summary>
    /// Opens the <c>{patchSha256}.patch</c> entry of the patch archive. The archive
    /// (<c>manifest.json</c> plus one entry per patched file, as the server writes it) is
    /// downloaded once, on first use, and kept for the lifetime of this downloader.
    /// </summary>
    public async Task<Stream> OpenPatchEntryAsync(string patchSha256, long maxBytes, CancellationToken ct)
    {
        // A bad archive is downloaded once: every later patched file falls back straight away.
        if (_patchArchiveFailed is { } why)
            throw new InvalidDataException($"patch archive unavailable ({why})");
        if (_patchArchive is null)
        {
            try
            {
                await OpenPatchArchiveAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _patchArchiveFailed = ex.Message;
                throw;
            }
        }

        var entry = _patchArchive!.GetEntry($"{patchSha256}.patch")
            ?? throw new InvalidOperationException($"Patch archive has no entry for patch {patchSha256}");
        if (entry.Length > maxBytes)
            throw new InvalidDataException($"patch {patchSha256} is {entry.Length} bytes, more than the {maxBytes} allowed");

        // Copy out: ZIP entry streams are not seekable, and BSPatch needs a seekable patch.
        var buffer = new MemoryStream();
        await using (var entryStream = new LengthLimitedStream(entry.Open(), entry.Length, $"patch {patchSha256}"))
            await entryStream.CopyToAsync(buffer, ct);
        buffer.Position = 0;
        return buffer;
    }

    /// <inheritdoc />
    public void SetReleaseSize(long totalBytes, int fileCount)
    {
        _releaseBytes = totalBytes;
        _releaseFiles = fileCount;
    }

    /// <summary>
    /// Downloads the patch archive (<c>manifest.json</c> plus one entry per patched file, as the
    /// server writes it) into a self-deleting temp file. The server discards patches of 90 % of
    /// the full size or more, so a genuine archive is always below the release's size + 1 MiB.
    /// </summary>
    private async Task OpenPatchArchiveAsync(CancellationToken ct)
    {
        {
            var url = ApiRoutes.ForPatch(_server, _packageId, _fromVersion, _toVersion, _platform, _architecture);
            using var response = await GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var file = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite,
                FileShare.None, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
            try
            {
                var raw = await response.Content.ReadAsStreamAsync(ct);
                await using (var body = _releaseBytes is { } total
                                 ? new LengthLimitedStream(raw, total + 1024 * 1024, "the patch archive")
                                 : raw)
                    await body.CopyToAsync(file, ct);

                // Catches corruption early; trust comes from hashing every patched
                // output against the signed release, so a mismatch only disables patching.
                if (_expectedPatchSha256 is { } expected)
                {
                    file.Position = 0;
                    var actual = Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(file, ct));
                    if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("the patch archive does not match the hash check-update reported");
                }

                file.Position = 0;
                _patchArchive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
                _patchArchiveFile = file;
            }
            catch
            {
                await file.DisposeAsync();
                throw;
            }
        }
    }

    /// <inheritdoc />
    public async Task<SignedRelease?> GetReleaseAsync(CancellationToken ct)
    {
        var url = ApiRoutes.ForRelease(_server, _packageId, _toVersion, _platform, _architecture);
        using var response = await GetAsync(url, HttpCompletionOption.ResponseContentRead, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync(WireJsonContext.Default.SignedRelease, ct)
            ?? throw new InvalidOperationException("server returned an empty release manifest");
    }

    /// <inheritdoc />
    public async Task<Stream> DownloadFileAsync(string relativePath, CancellationToken ct)
    {
        var url = ApiRoutes.ForDownloadFile(_server, _packageId, _toVersion, _platform, _architecture, relativePath);
        var response = await GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStreamAsync(ct);
    }

    /// <inheritdoc />
    public async Task<PatchManifest?> DownloadPatchManifestAsync(CancellationToken ct)
    {
        var url = ApiRoutes.ForPatchManifest(_server, _packageId, _fromVersion, _toVersion, _platform, _architecture);
        using var response = await GetAsync(url, HttpCompletionOption.ResponseContentRead, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync(WireJsonContext.Default.PatchManifest, ct);
    }

    /// <inheritdoc />
    public async Task<Stream> DownloadFullBuildAsync(CancellationToken ct)
    {
        var url = ApiRoutes.ForDownloadBuild(_server, _packageId, _toVersion, _platform, _architecture);
        using var response = await GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        // ZipArchive needs a seekable stream; spool to a self-deleting temp file.
        var file = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite,
            FileShare.None, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        try
        {
            var raw = await response.Content.ReadAsStreamAsync(ct);
            await using (var body = _releaseBytes is { } total
                             ? new LengthLimitedStream(raw, total + 64 * 1024 + 1024L * _releaseFiles, "the full build")
                             : raw)
                await body.CopyToAsync(file, ct);
            file.Position = 0;
            return file;
        }
        catch
        {
            await file.DisposeAsync();
            throw;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _patchArchive?.Dispose();
        _patchArchiveFile?.Dispose();
        if (_ownsHttp) _http.Dispose();
    }
}
