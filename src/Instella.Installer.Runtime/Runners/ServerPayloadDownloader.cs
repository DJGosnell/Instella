using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.FileSystem;
using Instella.Core.Logging;
using Instella.Core.Platform;
using Instella.Core.Trust;
using Instella.Core.Utilities;
using Instella.Core.Wire;
using Instella.Installer.Runtime.Builders;

namespace Instella.Installer.Runtime.Runners;

/// <summary>
/// Lite-installer payload download. Fetches the build's signed release manifest, verifies
/// it against the publisher keys compiled into this installer (pinned to exactly
/// <see cref="FrozenConfig.AppVersion"/>), downloads the full build, and checks every file
/// against the release before returning the archive. Nothing unverified reaches the caller.
/// </summary>
internal static class ServerPayloadDownloader
{
    public static Task<Stream> DownloadAsync(
        FrozenConfig config,
        string destinationPath,
        IInstellaLogger log,
        CancellationToken ct) =>
        DownloadAsync(config, destinationPath, log, http: null, ct);

    /// <param name="config">The installer configuration (server, package, version).</param>
    /// <param name="destinationPath">Where the downloaded archive is written.</param>
    /// <param name="log">Installer log.</param>
    /// <param name="http">Client to use; null creates (and disposes) a default one. Tests inject theirs.</param>
    /// <param name="ct">Cancellation token.</param>
    internal static async Task<Stream> DownloadAsync(
        FrozenConfig config,
        string destinationPath,
        IInstellaLogger log,
        HttpClient? http,
        CancellationToken ct)
    {
        var server = new Uri(config.ServerUrl, UriKind.Absolute);
        var platform = PlatformDetector.Current;
        var architecture = ArchitectureExtensions.Current;

        var ownsHttp = http is null;
        http ??= ServerHttp.Create(server, config.DownloadToken, "Instella-Installer/1.0", TimeSpan.FromMinutes(30));
        try
        {
            ReleaseManifest? release = null;
            if (config.PublisherKeys.Count > 0)
            {
                var releaseUrl = ApiRoutes.ForRelease(server, config.AppId, config.AppVersion, platform, architecture);
                log.Info($"install: fetching release manifest {releaseUrl}");
                var signed = await http.GetFromJsonAsync(releaseUrl, WireJsonContext.Default.SignedRelease, ct)
                    ?? throw new UpdateTrustException("server returned an empty release manifest");
                release = ReleaseVerifier.Verify(signed, new TrustPolicy(
                    config.PublisherKeys, config.AppId, PlatformStrings.Os(platform), PlatformStrings.Arch(architecture),
                    MustBeNewerThan: null, MustEqual: config.AppVersion));
            }
            else if (config.AllowUnsignedUpdates)
            {
                log.Warn("install: AllowUnsignedUpdates is set; the downloaded payload is not verified against a publisher key");
            }
            else
            {
                throw new UpdateTrustException("this installer has no publisher keys, so a server payload cannot be verified");
            }

            var url = ApiRoutes.ForDownloadBuild(server, config.AppId, config.AppVersion, platform, architecture);
            log.Info($"install: downloading {url}");
            try
            {
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                await using var outFile = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
                var body = await response.Content.ReadAsStreamAsync(ct);
                // A verified release bounds the download (the ZIP holds its files plus overhead);
                // an unsigned one has nothing to bound it with.
                await using var responseStream = release is null
                    ? body
                    : new LengthLimitedStream(body, FullBuildLimit(release), "the downloaded payload");
                await responseStream.CopyToAsync(outFile, ct);
            }
            catch (InvalidDataException ex)
            {
                TryDelete(destinationPath);
                throw new UpdateTrustException(ex.Message);
            }
            catch
            {
                TryDelete(destinationPath);
                throw;
            }

            var archive = new FileStream(destinationPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                if (release is not null)
                    await VerifyArchiveAsync(archive, release, ct);
                archive.Position = 0;
                return archive;
            }
            catch
            {
                await archive.DisposeAsync();
                throw;
            }
        }
        finally
        {
            if (ownsHttp) http.Dispose();
        }
    }

    /// <summary>The largest ZIP a verified release can arrive as: its files plus 64 KiB and 1 KiB per file.</summary>
    internal static long FullBuildLimit(ReleaseManifest release) =>
        release.Files.Sum(f => f.Size) + 64 * 1024 + 1024L * release.Files.Count;

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// The archive must contain exactly the release's files, each matching its signed
    /// size and SHA-256. Throws <see cref="UpdateTrustException"/> naming the first mismatch.
    /// </summary>
    internal static async Task VerifyArchiveAsync(Stream archive, ReleaseManifest release, CancellationToken ct)
    {
        var expected = release.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true);
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(entry.Name)) continue; // directory entry

            if (!SafePath.TryNormalizeRelative(entry.FullName, out var path, out _) || !expected.TryGetValue(path, out var file))
                throw new UpdateTrustException($"payload contains '{entry.FullName}', which the signed release does not list");
            if (!seen.Add(path))
                throw new UpdateTrustException($"payload contains '{path}' twice");
            if (entry.Length != file.Size)
                throw new UpdateTrustException($"'{path}' is {entry.Length} bytes; the signed release says {file.Size}");

            await using var s = entry.Open();
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(s, ct));
            if (!string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new UpdateTrustException($"'{path}' does not match the signed release (hash mismatch)");
        }

        var missing = expected.Keys.FirstOrDefault(k => !seen.Contains(k));
        if (missing is not null)
            throw new UpdateTrustException($"payload is missing '{missing}', which the signed release lists");
    }
}
