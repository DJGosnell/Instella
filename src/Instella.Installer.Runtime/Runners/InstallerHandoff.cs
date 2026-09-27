using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Logging;
using Instella.Core.Platform;
using Instella.Core.Platform.Windows;
using Instella.Core.Trust;
using Instella.Core.Utilities;
using Instella.Core.Wire;
using Instella.Installer.Runtime.Builders;

namespace Instella.Installer.Runtime.Runners;

/// <summary>A version on the server that has an online installer for this platform.</summary>
/// <param name="Version">The version.</param>
/// <param name="Changelog">Its changelog, if any.</param>
/// <param name="ReleasedAt">When it was released (UTC).</param>
/// <param name="InstallerSize">Size of its online installer in bytes.</param>
internal sealed record AvailableVersion(Version Version, string? Changelog, DateTime ReleasedAt, long InstallerSize);

/// <summary>An online installer whose size and SHA-256 come from a verified, signed release.</summary>
internal sealed record VerifiedInstaller(Version Version, ReleaseInstaller Installer);

/// <summary>
/// A downloaded installer that matched its signed entry, held open with a handle that denies
/// writers from before the final hash until the installer has run: a non-elevated process
/// of the same user could otherwise swap the file in <c>%TEMP%</c> before an elevated run.
/// Windows opens an image for execution with read sharing, so the child still starts.
/// </summary>
internal sealed class VerifiedInstallerFile(string path, FileStream locked) : IAsyncDisposable
{
    public string Path { get; } = path;

    public ValueTask DisposeAsync() => locked.DisposeAsync();
}

/// <summary>
/// The downloaded installer is not signed by the running installer's Authenticode signer.
/// A leaked publisher key alone is then not enough to hand over to an attacker's installer.
/// </summary>
internal sealed class InstallerSignerMismatchException(string message) : Exception(message);

/// <summary>
/// Hands the installation over to another version's online installer. An installer only ever
/// installs its own version, because the install process itself (pages, prerequisites, steps,
/// the uninstaller stub) is compiled into it; choosing another version means running that
/// version's installer. The installer is taken only when it matches the entry in that
/// version's release, signed by a key compiled into this installer.
/// </summary>
internal static class InstallerHandoff
{
    /// <summary>Passed to the installer handed over to, so it does not offer a newer version again.</summary>
    public const string NoNewerCheckFlag = "--no-newer-check";

    /// <summary>
    /// Non-deprecated versions on <paramref name="channel"/> that have an online installer for
    /// this OS and architecture, newest first.
    /// </summary>
    public static async Task<IReadOnlyList<AvailableVersion>> ListAsync(
        FrozenConfig config, HttpClient http, string channel, CancellationToken ct)
    {
        var os = PlatformStrings.Os(PlatformDetector.Current);
        var arch = PlatformStrings.Arch(ArchitectureExtensions.Current);
        var versions = await http.GetFromJsonAsync(
            ApiRoutes.ForPackageVersions(new Uri(config.ServerUrl, UriKind.Absolute), config.AppId),
            WireJsonContext.Default.VersionSummaryArray, ct) ?? [];

        var result = new List<AvailableVersion>();
        foreach (var v in versions)
        {
            if (v.IsDeprecated || !string.Equals(v.Channel ?? "stable", channel, StringComparison.OrdinalIgnoreCase)) continue;
            if (!AppVersions.TryParse(v.VersionString, out var version)) continue;
            var installer = v.Builds
                .Where(b => b.Os == os && b.Arch == arch)
                .SelectMany(b => b.Installers)
                .FirstOrDefault(i => i.Kind == InstallerKinds.Online);
            if (installer is null) continue;
            result.Add(new AvailableVersion(version, v.Changelog, v.ReleasedAt, installer.Size));
        }
        return result.OrderByDescending(v => v.Version).ToList();
    }

    /// <summary>
    /// Fetches <paramref name="version"/>'s signed release, verifies it against this installer's
    /// publisher keys, and returns its online installer entry.
    /// </summary>
    /// <exception cref="UpdateTrustException">The release does not verify or lists no online installer.</exception>
    public static async Task<VerifiedInstaller> VerifyAsync(
        FrozenConfig config, HttpClient http, Version version, string channel, CancellationToken ct)
    {
        if (config.PublisherKeys.Count == 0)
            throw new UpdateTrustException("this installer has no publisher keys, so another version's installer cannot be verified");

        var platform = PlatformDetector.Current;
        var architecture = ArchitectureExtensions.Current;
        var signed = await http.GetFromJsonAsync(
            ApiRoutes.ForRelease(new Uri(config.ServerUrl, UriKind.Absolute), config.AppId, version, platform, architecture),
            WireJsonContext.Default.SignedRelease, ct)
            ?? throw new UpdateTrustException("server returned an empty release manifest");
        // The channel the versions were listed from is part of what the signature must say.
        var release = ReleaseVerifier.Verify(signed, new TrustPolicy(
            config.PublisherKeys, config.AppId, PlatformStrings.Os(platform), PlatformStrings.Arch(architecture),
            MustBeNewerThan: null, MustEqual: version) { Channel = channel });

        var installer = release.Installers?.FirstOrDefault(i => i.Kind == InstallerKinds.Online)
            ?? throw new UpdateTrustException($"the signed release of {version} lists no online installer");
        if (!InstallerKinds.IsValidFileName(installer.FileName))
            throw new UpdateTrustException($"the signed release of {version} names its installer '{installer.FileName}'");
        return new VerifiedInstaller(version, installer);
    }

    /// <summary>
    /// Downloads the verified installer into a new folder under <c>%TEMP%\Instella\handoff</c>,
    /// then, under a handle that denies writers, checks its size and SHA-256 again and (when this
    /// installer is Authenticode-signed) that it carries the same signer. The returned file must be
    /// disposed after the installer has run.
    /// </summary>
    /// <exception cref="UpdateTrustException">The download does not match the signed entry.</exception>
    /// <exception cref="InstallerSignerMismatchException">The download is not signed by this installer's signer.</exception>
    public static async Task<VerifiedInstallerFile> DownloadAsync(
        FrozenConfig config, HttpClient http, VerifiedInstaller verified, CancellationToken ct,
        IAuthenticodeReader? authenticode = null, string? selfPath = null, IInstellaLogger? log = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "Instella", "handoff", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, verified.Installer.FileName);
        var url = ApiRoutes.ForDownloadInstaller(new Uri(config.ServerUrl, UriKind.Absolute), config.AppId, verified.Version,
            PlatformDetector.Current, ArchitectureExtensions.Current, InstallerKinds.Online);
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            string hash;
            long size = 0;
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    size += read;
                    if (size > verified.Installer.Size)
                        throw new UpdateTrustException($"the downloaded installer is larger than the signed {verified.Installer.Size} bytes");
                    hasher.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                }
                hash = Convert.ToHexStringLower(hasher.GetHashAndReset());
            }
            if (size != verified.Installer.Size || !string.Equals(hash, verified.Installer.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new UpdateTrustException($"the downloaded installer does not match the signed release of {verified.Version}");

            // From here on no writer can open the file: re-hash exactly what will run.
            var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(locked, ct));
                if (locked.Length != verified.Installer.Size || !string.Equals(actual, verified.Installer.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new UpdateTrustException($"the downloaded installer changed after it was verified ({verified.Version})");
                CheckSigner(path, authenticode, selfPath, log);
                return new VerifiedInstallerFile(path, locked);
            }
            catch
            {
                await locked.DisposeAsync();
                throw;
            }
        }
        catch
        {
            TryDeleteDirectory(dir);
            throw;
        }
    }

    /// <summary>
    /// When this installer is Authenticode-signed, the downloaded one must pass
    /// <c>WinVerifyTrust</c> and have a leaf signer with the same subject (thumbprints change at
    /// every renewal; the subject is the publisher's identity). An unsigned installer (a
    /// development build) skips the check.
    /// </summary>
    internal static void CheckSigner(string path, IAuthenticodeReader? authenticode, string? selfPath, IInstellaLogger? log)
    {
        authenticode ??= OperatingSystem.IsWindows() ? Authenticode.Instance : NoAuthenticodeReader.Instance;
        selfPath ??= Environment.ProcessPath;
        var self = selfPath is null ? default : authenticode.Read(selfPath);
        if (!self.IsValid || string.IsNullOrEmpty(self.SignerSubject))
        {
            log?.Info("handoff: this installer is not Authenticode-signed; the downloaded installer's signer is not checked");
            return;
        }
        var other = authenticode.Read(path);
        if (!other.IsValid || !string.Equals(other.SignerSubject, self.SignerSubject, StringComparison.Ordinal))
        {
            log?.Error($"handoff: signer '{other.SignerSubject ?? "(none)"}' (status 0x{other.Status:X8}) is not '{self.SignerSubject}'");
            throw new InstallerSignerMismatchException($"the downloaded installer is not signed by {self.SignerSubject}");
        }
        log?.Info($"handoff: the downloaded installer is signed by {self.SignerSubject}");
    }

    /// <summary>
    /// Starts the downloaded installer with <paramref name="args"/> plus <see cref="NoNewerCheckFlag"/>,
    /// waits for it and returns its exit code.
    /// </summary>
    public static async Task<int> RunAsync(string installerPath, IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(installerPath) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(installerPath)! };
        foreach (var a in args.Where(a => a != NoNewerCheckFlag)) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add(NoNewerCheckFlag);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {installerPath}");
        await process.WaitForExitAsync(ct);
        return process.ExitCode;
    }

    /// <summary>Removes handoff folders older than a day (a running installer cannot delete itself).</summary>
    public static void SweepOldDownloads()
    {
        try
        {
            var root = Path.Combine(Path.GetTempPath(), "Instella", "handoff");
            if (!Directory.Exists(root)) return;
            foreach (var dir in Directory.EnumerateDirectories(root))
                if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(dir) > TimeSpan.FromDays(1))
                    TryDeleteDirectory(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
    }

    private static void TryDeleteDirectory(string dir)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
    }
}
