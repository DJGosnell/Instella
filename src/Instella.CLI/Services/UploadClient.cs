using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Instella.Core.Utilities;
using Instella.Core.Platform;
using Instella.Core.Trust;
using Instella.Core.Wire;

namespace Instella.CLI.Services;

/// <summary>
/// Client for session-based file uploads to the Instella server.
/// Uses a start/file/complete pattern for atomic uploads with deduplication.
/// </summary>
public sealed class UploadClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly Uri _server;

    public UploadClient(string serverUrl, string apiKey)
        : this(new HttpClient { Timeout = TimeSpan.FromMinutes(30) }, serverUrl, apiKey)
    {
    }

    internal UploadClient(HttpClient httpClient, string serverUrl, string apiKey)
    {
        _server = new Uri(serverUrl, UriKind.Absolute);
        _httpClient = httpClient;
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    /// <summary>
    /// Uploads a directory of files to the server using session-based API.
    /// </summary>
    public async Task<UploadResult> UploadVersionAsync(
        UploadRequest request,
        IProgress<UploadProgress>? progress = null,
        CancellationToken ct = default)
    {
        // Step 1: hash everything locally, describe it and sign it before anything is sent. A
        // broken signing key or sign command then fails in seconds, before a single byte is uploaded.
        var files = EnumerateFiles(request.SourceDirectory);
        var releaseFiles = new List<ReleaseFile>(files.Count);
        foreach (var file in files)
        {
            await using var stream = File.OpenRead(file.FullPath);
            releaseFiles.Add(new ReleaseFile(file.RelativePath, file.Size, await Checksum.ComputeSHA256Async(stream, ct),
                IsExecutable(file.FullPath)));
        }
        var releaseInstallers = new List<ReleaseInstaller>(request.Installers.Count);
        foreach (var installer in request.Installers)
        {
            await using var stream = File.OpenRead(installer.Path);
            releaseInstallers.Add(new ReleaseInstaller(installer.Kind, Path.GetFileName(installer.Path),
                new System.IO.FileInfo(installer.Path).Length, await Checksum.ComputeSHA256Async(stream, ct)));
        }

        var manifest = new ReleaseManifest
        {
            FormatVersion = ReleaseManifest.CurrentFormatVersion,
            AppId = request.PackageId,
            Version = request.Version,
            Os = PlatformStrings.Os(request.Platform),
            Arch = PlatformStrings.Arch(request.Architecture),
            Channel = request.Channel,
            CreatedAt = DateTimeOffset.UtcNow,
            Files = releaseFiles,
            TrustedKeys = request.RotateToKeys,
            Installers = releaseInstallers.Count > 0 ? releaseInstallers : null,
        };
        SignedRelease? release = null;
        string? draftManifest = null;
        try
        {
            if (request.Draft)
                draftManifest = Convert.ToBase64String(ReleaseSigner.Serialize(manifest));
            else if (request.SignWith is { } signWith)
                release = await signWith(ReleaseSigner.Serialize(manifest), ct);
            else if (request.SigningKey is { } signingKey)
                release = ReleaseSigner.Sign(manifest, signingKey);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new UploadResult(false, $"signing failed: {ex.Message}", null) { SigningFailed = true };
        }

        // Step 2: the session, the files, the installers, and the completion with the signature.
        var sessionResult = await StartSessionAsync(request, ct);
        if (!sessionResult.IsSuccess || sessionResult.Data == null)
        {
            return new UploadResult(false, sessionResult.Error ?? "Failed to start upload session", null)
            {
                StatusCode = sessionResult.StatusCode,
            };
        }

        var sessionId = sessionResult.Data.SessionId;

        try
        {
            var totalFiles = files.Count;
            var totalBytes = files.Sum(f => f.Size);
            var uploadedFiles = 0;
            var uploadedBytes = 0L;

            for (var i = 0; i < files.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var file = files[i];

                progress?.Report(new UploadProgress(
                    uploadedFiles, totalFiles, uploadedBytes, totalBytes, file.RelativePath));

                var fileResult = await UploadFileAsync(sessionId, file, releaseFiles[i].Sha256, ct);
                if (!fileResult.IsSuccess)
                {
                    await CancelSessionAsync(sessionId, CancellationToken.None);
                    return new UploadResult(false, $"Failed to upload {file.RelativePath}: {fileResult.Error}", null)
                    {
                        StatusCode = fileResult.StatusCode,
                    };
                }

                uploadedFiles++;
                uploadedBytes += file.Size;
            }

            for (var i = 0; i < request.Installers.Count; i++)
            {
                var installer = request.Installers[i];
                var installerResult = await UploadInstallerAsync(sessionId, installer, releaseInstallers[i], ct);
                if (!installerResult.IsSuccess)
                {
                    await CancelSessionAsync(sessionId, CancellationToken.None);
                    return new UploadResult(false, $"Failed to upload the {installer.Kind} installer: {installerResult.Error}", null)
                    {
                        StatusCode = installerResult.StatusCode,
                    };
                }
            }

            progress?.Report(new UploadProgress(
                uploadedFiles, totalFiles, uploadedBytes, totalBytes, "Completing upload..."));

            var completeResult = await CompleteSessionAsync(sessionId, request.Changelog, release, draftManifest, ct);
            if (!completeResult.IsSuccess)
            {
                return new UploadResult(false, completeResult.Error ?? "Failed to complete upload session", null)
                {
                    StatusCode = completeResult.StatusCode,
                };
            }

            var versionUrl = ApiRoutes.ForPackageVersion(_server, request.PackageId, request.Version.ToString()).AbsoluteUri;
            return new UploadResult(true, null, versionUrl)
            {
                State = completeResult.Data?.State,
                PublishAfter = completeResult.Data?.PublishAfter,
            };
        }
        catch (OperationCanceledException)
        {
            await CancelSessionAsync(sessionId, CancellationToken.None);
            return new UploadResult(false, "Upload cancelled", null);
        }
        catch (Exception ex)
        {
            await CancelSessionAsync(sessionId, CancellationToken.None);
            return new UploadResult(false, $"Upload failed: {ex.Message}", null);
        }
    }

    private async Task<ApiResult<StartUploadResponse>> StartSessionAsync(
        UploadRequest request,
        CancellationToken ct)
    {
        var body = new StartUploadRequest
        {
            PackageId = request.PackageId,
            Version = request.Version.ToString(),
            Channel = request.Channel,
            Os = PlatformStrings.Os(request.Platform),
            Arch = PlatformStrings.Arch(request.Architecture),
        };

        using var response = await _httpClient.PostAsJsonAsync(
            ApiRoutes.ForUploadStart(_server), body, WireJsonContext.Default.StartUploadRequest, ct);
        return await ApiClient.HandleResponseAsync(response, WireJsonContext.Default.StartUploadResponse, ct);
    }

    private async Task<ApiResult<UploadFileResponse>> UploadFileAsync(
        Guid sessionId,
        FileInfo file,
        string sha256,
        CancellationToken ct)
    {
        await using var fileStream = File.OpenRead(file.FullPath);
        using var streamContent = new StreamContent(fileStream);
        streamContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        var url = ApiRoutes.ForUploadFile(_server, sessionId, file.RelativePath, sha256);
        using var response = await _httpClient.PostAsync(url, streamContent, ct);
        return await ApiClient.HandleResponseAsync(response, WireJsonContext.Default.UploadFileResponse, ct);
    }

    private async Task<ApiResult<UploadFileResponse>> UploadInstallerAsync(
        Guid sessionId,
        InstallerUpload installer,
        ReleaseInstaller described,
        CancellationToken ct)
    {
        await using var fileStream = File.OpenRead(installer.Path);
        using var streamContent = new StreamContent(fileStream);
        streamContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        var url = ApiRoutes.ForUploadInstaller(_server, sessionId, installer.Kind, described.FileName, described.Sha256);
        using var response = await _httpClient.PostAsync(url, streamContent, ct);
        return await ApiClient.HandleResponseAsync(response, WireJsonContext.Default.UploadFileResponse, ct);
    }

    private async Task<ApiResult<CompleteUploadResponse>> CompleteSessionAsync(
        Guid sessionId,
        string? changelog,
        SignedRelease? release,
        string? draftManifest,
        CancellationToken ct)
    {
        using var response = await _httpClient.PostAsJsonAsync(
            ApiRoutes.ForUploadComplete(_server, sessionId),
            new CompleteUploadRequest { Changelog = changelog, Release = release, DraftManifest = draftManifest },
            WireJsonContext.Default.CompleteUploadRequest,
            ct);
        return await ApiClient.HandleResponseAsync(response, WireJsonContext.Default.CompleteUploadResponse, ct);
    }

    private async Task CancelSessionAsync(Guid sessionId, CancellationToken ct)
    {
        try
        {
            using var _ = await _httpClient.DeleteAsync(ApiRoutes.ForUploadCancel(_server, sessionId), ct);
        }
        catch
        {
            // Ignore cancellation errors
        }
    }

    /// <summary>
    /// Whether a file should be marked executable on install. POSIX hosts read the file's
    /// own execute bits; Windows has none to read, so builds for Linux/macOS should be
    /// uploaded from a POSIX host (or CI runner) to keep them.
    /// </summary>
    private static bool IsExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) return false;
        var mode = File.GetUnixFileMode(path);
        return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
    }

    private static List<FileInfo> EnumerateFiles(string sourceDirectory)
    {
        var files = new List<FileInfo>();
        var basePath = Path.GetFullPath(sourceDirectory);

        foreach (var fullPath in Directory.EnumerateFiles(basePath, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(basePath, fullPath).Replace('\\', '/');
            var size = new System.IO.FileInfo(fullPath).Length;
            files.Add(new FileInfo(fullPath, relativePath, size));
        }

        return files.OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private sealed record FileInfo(string FullPath, string RelativePath, long Size);
}

// Request and result types for upload operations

public sealed record UploadRequest
{
    public required string PackageId { get; init; }
    public required Version Version { get; init; }
    public required string SourceDirectory { get; init; }
    public required string Channel { get; init; }
    public required TargetPlatform Platform { get; init; }
    public required Architecture Architecture { get; init; }
    public string? Changelog { get; init; }

    /// <summary>Publisher private key; null (and no <see cref="SignWith"/>) uploads an unsigned build.</summary>
    public ECDsa? SigningKey { get; init; }

    /// <summary>Signs the exact manifest bytes some other way (<c>--sign-command</c>); wins over <see cref="SigningKey"/>.</summary>
    public Func<byte[], CancellationToken, Task<SignedRelease>>? SignWith { get; init; }

    /// <summary>Optional key rotation list embedded in the signed release (see docs/signing-and-keys.md).</summary>
    public IReadOnlyList<PublisherKey>? RotateToKeys { get; init; }

    /// <summary>
    /// Upload unsigned and keep the build hidden until <c>instella publish</c> signs it (the
    /// manifest to sign is stored with the build). No signing key is used.
    /// </summary>
    public bool Draft { get; init; }

    /// <summary>Installers published with the build (listed in the signed release).</summary>
    public IReadOnlyList<InstallerUpload> Installers { get; init; } = [];
}

/// <summary>An installer to publish with a build.</summary>
/// <param name="Kind"><see cref="InstallerKinds.Online"/> or <see cref="InstallerKinds.Offline"/>.</param>
/// <param name="Path">Local file; its name is the download name.</param>
public sealed record InstallerUpload(string Kind, string Path);

public readonly record struct UploadProgress(
    int FilesUploaded,
    int TotalFiles,
    long BytesUploaded,
    long TotalBytes,
    string CurrentFile);

public sealed record UploadResult(bool Success, string? Error, string? VersionUrl)
{
    /// <summary>HTTP status of the failing call, when there was one.</summary>
    public System.Net.HttpStatusCode? StatusCode { get; init; }

    /// <summary>
    /// The build's state on the server (<see cref="ReleaseStates"/>): published, pending approval, or a
    /// draft. Null from an older server.
    /// </summary>
    public string? State { get; init; }

    /// <summary>When a delayed release goes live unless rejected (UTC).</summary>
    public DateTime? PublishAfter { get; init; }

    /// <summary>Signing failed; nothing was uploaded (exit <c>Signing</c>, 4).</summary>
    public bool SigningFailed { get; init; }
}
