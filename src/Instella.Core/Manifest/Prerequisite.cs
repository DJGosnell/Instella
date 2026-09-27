namespace Instella.Core.Manifest;

/// <summary>
/// A dependency installed before the application (for example a runtime redistributable).
/// A class with init-only properties rather than a positional record, so fields can be
/// added later without a breaking change.
/// </summary>
public sealed class Prerequisite
{
    /// <summary>Human-readable name (e.g., "VC++ 2022 Runtime").</summary>
    public required string Name { get; init; }

    /// <summary>URL to download the installer from when it is not bundled.</summary>
    public string? DownloadUrl { get; init; }

    /// <summary>
    /// Lower-case hex SHA-256 of the downloaded installer. Required whenever
    /// <see cref="DownloadUrl"/> is set; a download that does not match is never executed.
    /// </summary>
    public string? Sha256 { get; init; }

    /// <summary>
    /// Payload-relative path of a bundled installer (under <c>.instella/prereqs/</c>).
    /// Its integrity is covered by the installer's payload hash.
    /// </summary>
    public string? BundlePath { get; init; }

    /// <summary>Registry key whose presence means the prerequisite is installed (Windows only).</summary>
    public string? DetectionRegistry { get; init; }

    /// <summary>Arguments passed to the installer; each element is one argument.</summary>
    public IReadOnlyList<string> InstallArguments { get; init; } = ["/quiet", "/norestart"];

    /// <summary>Exit codes that mean success. 3010 means success, reboot required.</summary>
    public IReadOnlyList<int> SuccessExitCodes { get; init; } = [0, 3010];

    /// <summary>Whether the installer must run elevated.</summary>
    public bool RequiresElevation { get; init; } = true;
}
