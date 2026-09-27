using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using Instella.Core.FileSystem;
using Instella.Core.Manifest;
using Instella.Core.Platform.Windows;
using Microsoft.Win32;

namespace Instella.Installer.Runtime.Core;

/// <summary>
/// Detects and installs prerequisites. A downloaded installer runs only after its SHA-256
/// matches the one the author declared, and it is verified through the very handle held
/// open (denying writers) while it executes, so it cannot be swapped in between. A
/// prerequisite with <see cref="Prerequisite.RequiresElevation"/> runs through a UAC prompt
/// when the installer lacks administrator rights, or is refused when no prompt may be shown.
/// </summary>
internal sealed class PrerequisiteInstaller
{
    /// <summary>Runs an installer (through a UAC prompt when <paramref name="elevate"/>) and returns its exit code.</summary>
    public delegate Task<int> ProcessRunner(string fileName, IReadOnlyList<string> arguments, bool elevate, CancellationToken ct);

    private const int ErrorCancelled = 1223; // the user answered No to the UAC prompt

    private readonly IReadOnlyList<Prerequisite> _prerequisites;
    private readonly IFileSystem _fs;
    private readonly HttpClient? _http;
    private readonly ProcessRunner _run;
    private readonly Func<Prerequisite, bool> _isInstalled;
    private readonly Func<bool> _canElevate;

    public PrerequisiteInstaller(IReadOnlyList<Prerequisite> prerequisites, IFileSystem fs)
        : this(prerequisites, fs, http: null, run: null, isInstalled: null)
    {
    }

    /// <summary>
    /// Whether a UAC prompt may be shown for a prerequisite that needs elevation. False for
    /// silent installs, which never prompt: such a prerequisite fails instead.
    /// </summary>
    public bool AllowElevationPrompt { get; init; }

    /// <param name="prerequisites">Declared prerequisites.</param>
    /// <param name="fs">File system (bundled extraction).</param>
    /// <param name="http">HTTP client for downloads; null creates a default one per download.</param>
    /// <param name="run">Process runner; null launches real processes.</param>
    /// <param name="isInstalled">Detection override; null uses registry detection.</param>
    /// <param name="canElevate">
    /// True when the process lacks administrator rights and could gain them through UAC; null
    /// means "on Windows and not elevated". Elsewhere there is no elevation, so a prerequisite
    /// runs with the installer's own rights.
    /// </param>
    internal PrerequisiteInstaller(
        IReadOnlyList<Prerequisite> prerequisites,
        IFileSystem fs,
        HttpClient? http,
        ProcessRunner? run,
        Func<Prerequisite, bool>? isInstalled,
        Func<bool>? canElevate = null)
    {
        _canElevate = canElevate ?? (() => OperatingSystem.IsWindows() && !Environment.IsPrivilegedProcess);
        _prerequisites = prerequisites;
        _fs = fs;
        _http = http;
        _run = run ?? RunProcessAsync;
        _isInstalled = isInstalled ?? IsInstalled;
    }

    /// <summary>
    /// Gets the list of prerequisites that are not currently installed.
    /// </summary>
    public Task<IReadOnlyList<Prerequisite>> GetMissingAsync(CancellationToken ct)
    {
        var missing = new List<Prerequisite>();
        foreach (var prereq in _prerequisites)
        {
            ct.ThrowIfCancellationRequested();
            if (!_isInstalled(prereq))
                missing.Add(prereq);
        }
        return Task.FromResult<IReadOnlyList<Prerequisite>>(missing);
    }

    /// <summary>
    /// Installs a prerequisite from a bundled installer or its download URL.
    /// </summary>
    /// <param name="prerequisite">The prerequisite to install.</param>
    /// <param name="bundledInstaller">Stream of the bundled installer (from the verified payload), or null to download.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Whether the installer asked for a reboot (exit code 3010).</returns>
    public async Task<PrerequisiteResult> InstallAsync(Prerequisite prerequisite, Stream? bundledInstaller, CancellationToken ct)
    {
        var elevate = prerequisite.RequiresElevation && _canElevate();
        if (elevate && !AllowElevationPrompt)
            throw new PrerequisiteElevationException(
                $"Prerequisite '{prerequisite.Name}' needs administrator rights, and a silent install never shows a UAC prompt; " +
                "run the installer from an elevated prompt.");

        var workDir = Path.Combine(Path.GetTempPath(), "Instella", $"prereq-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8))}");
        Directory.CreateDirectory(workDir);
        try
        {
            string installerPath;
            string? expectedSha256;
            if (bundledInstaller is not null)
            {
                // Bundled bytes are covered by the payload's integrity hash.
                installerPath = Path.Combine(workDir, SafeFileName(prerequisite.BundlePath) ?? "setup.exe");
                await using (var file = new FileStream(installerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    await bundledInstaller.CopyToAsync(file, ct);
                expectedSha256 = prerequisite.Sha256;
            }
            else if (!string.IsNullOrEmpty(prerequisite.DownloadUrl))
            {
                if (string.IsNullOrEmpty(prerequisite.Sha256))
                    throw new InvalidOperationException(
                        $"Prerequisite '{prerequisite.Name}' has a download URL but no SHA-256; refusing to run an unverified download.");
                installerPath = Path.Combine(workDir, FileNameFromUrl(prerequisite.DownloadUrl));
                await DownloadAsync(prerequisite.DownloadUrl, installerPath, ct);
                expectedSha256 = prerequisite.Sha256;
            }
            else
            {
                throw new InvalidOperationException($"No source available for prerequisite: {prerequisite.Name}");
            }

            // Hold a read handle that denies writers for the whole verify-then-run window.
            await using var locked = new FileStream(installerPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (expectedSha256 is not null)
            {
                var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(locked, ct));
                if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
                    throw new PrerequisiteIntegrityException(prerequisite.Name, expectedSha256, actual);
            }

            var exitCode = await _run(installerPath, prerequisite.InstallArguments, elevate, ct);
            if (!prerequisite.SuccessExitCodes.Contains(exitCode))
                throw new InvalidOperationException($"Prerequisite '{prerequisite.Name}' installer failed with exit code {exitCode}");

            if (!string.IsNullOrEmpty(prerequisite.DetectionRegistry) && !_isInstalled(prerequisite))
                throw new InvalidOperationException($"Prerequisite '{prerequisite.Name}' installation verification failed");

            return new PrerequisiteResult(RebootRequired: exitCode == 3010);
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); }
            catch (IOException) { /* best effort: an installer may still hold a file */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    private async Task DownloadAsync(string url, string destination, CancellationToken ct)
    {
        var http = _http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            await using var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await response.Content.CopyToAsync(file, ct);
        }
        finally
        {
            if (_http is null) http.Dispose();
        }
    }

    private static string FileNameFromUrl(string url)
    {
        var name = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? Path.GetFileName(uri.AbsolutePath) : null;
        return SafeFileName(name) ?? "setup.exe";
    }

    private static string? SafeFileName(string? candidate)
    {
        var name = Path.GetFileName(candidate?.Replace('\\', '/') ?? "");
        return SafePath.TryNormalizeRelative(name, out var normalized, out _) && !normalized.Contains('/') ? normalized : null;
    }

    private static bool IsInstalled(Prerequisite prerequisite)
    {
        if (string.IsNullOrEmpty(prerequisite.DetectionRegistry) || !OperatingSystem.IsWindows())
            return false;
        return IsInstalledWindows(prerequisite.DetectionRegistry);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool IsInstalledWindows(string registryPath)
    {
        // Format: HKLM\SOFTWARE\Microsoft\...
        var parts = registryPath.Split('\\', 2);
        if (parts.Length < 2)
            return false;

        var hive = parts[0].ToUpperInvariant() switch
        {
            "HKLM" or "HKEY_LOCAL_MACHINE" => Registry.LocalMachine,
            "HKCU" or "HKEY_CURRENT_USER" => Registry.CurrentUser,
            _ => null
        };
        if (hive == null)
            return false;

        try
        {
            using var key = hive.OpenSubKey(parts[1], writable: false);
            return key != null;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<int> RunProcessAsync(string fileName, IReadOnlyList<string> arguments, bool elevate, CancellationToken ct)
    {
        ProcessStartInfo psi;
        if (elevate)
        {
            // The runas verb needs ShellExecute, which takes one command-line string.
            psi = new ProcessStartInfo(fileName)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = WindowsCommandLine.Join(arguments),
            };
        }
        else
        {
            psi = new ProcessStartInfo(fileName) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in arguments) psi.ArgumentList.Add(arg);
        }

        try
        {
            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException($"Failed to start '{fileName}'");
            await process.WaitForExitAsync(ct);
            return process.ExitCode;
        }
        catch (Win32Exception ex) when (elevate && ex.NativeErrorCode == ErrorCancelled)
        {
            throw new PrerequisiteElevationException(
                $"The administrator prompt for '{Path.GetFileName(fileName)}' was declined.");
        }
    }
}

/// <summary>Outcome of a successful prerequisite install.</summary>
/// <param name="RebootRequired">The installer returned 3010.</param>
internal sealed record PrerequisiteResult(bool RebootRequired);

/// <summary>A prerequisite needed administrator rights that were not granted.</summary>
internal sealed class PrerequisiteElevationException(string message) : Exception(message);

/// <summary>A prerequisite installer did not match its declared SHA-256 and was not executed.</summary>
internal sealed class PrerequisiteIntegrityException(string name, string expected, string actual)
    : Exception($"Prerequisite '{name}' failed its integrity check (expected SHA-256 {expected}, got {actual}); it was not executed.");
