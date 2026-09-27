using Instella.Core.Manifest;
using Instella.Core.Platform;

namespace Instella.Installer.Runtime.Core;

/// <summary>
/// Type of installer packaging.
/// </summary>
internal enum InstallerType
{
    /// <summary>Self-contained with all files appended as archive.</summary>
    Offline,

    /// <summary>Small stub that downloads files during installation.</summary>
    Lite
}

/// <summary>
/// Configuration for lite installers (embedded resource).
/// </summary>
internal sealed record InstallerConfig
{
    /// <summary>Server URL for downloading files.</summary>
    public required string ServerUrl { get; init; }

    /// <summary>Package identifier.</summary>
    public required string PackageId { get; init; }

    /// <summary>Version to install.</summary>
    public required Version Version { get; init; }

    /// <summary>Target platform.</summary>
    public required TargetPlatform Platform { get; init; }

    /// <summary>Target architecture.</summary>
    public required Architecture Architecture { get; init; }
}

/// <summary>
/// Installation options selected by the user.
/// </summary>
public sealed record InstallOptions
{
    /// <summary>Directory to install to.</summary>
    public required string InstallPath { get; init; }

    /// <summary>Whether to create a desktop shortcut.</summary>
    public bool CreateDesktopShortcut { get; init; }

    /// <summary>Whether to create a start menu shortcut (Windows) or desktop entry (Linux).</summary>
    public bool CreateStartMenuShortcut { get; init; }

    /// <summary>Whether to add the install directory to PATH.</summary>
    public bool AddToPath { get; init; }

    /// <summary>Whether to configure auto-start on login.</summary>
    public bool ConfigureAutoStart { get; init; }

    /// <summary>Whether to register file associations.</summary>
    public bool RegisterFileAssociations { get; init; }

    /// <summary>Elevation mode for installation.</summary>
    public ElevationMode Elevation { get; init; }
}

/// <summary>
/// State of the installation process.
/// </summary>
internal enum InstallationState
{
    /// <summary>Installation not started.</summary>
    NotStarted,

    /// <summary>Initializing installation.</summary>
    Initializing,

    /// <summary>Downloading files (lite installer only).</summary>
    Downloading,

    /// <summary>Extracting files from archive.</summary>
    Extracting,

    /// <summary>Copying files to installation directory.</summary>
    CopyingFiles,

    /// <summary>Creating shortcuts.</summary>
    CreatingShortcuts,

    /// <summary>Registering file associations.</summary>
    RegisteringFileAssociations,

    /// <summary>Adding to PATH.</summary>
    ConfiguringPath,

    /// <summary>Configuring auto-start.</summary>
    ConfiguringAutoStart,

    /// <summary>Installing prerequisites.</summary>
    InstallingPrerequisites,

    /// <summary>Writing installation manifest.</summary>
    WritingManifest,

    /// <summary>Installation completed successfully.</summary>
    Completed,

    /// <summary>Installation failed.</summary>
    Failed,

    /// <summary>Installation cancelled by user.</summary>
    Cancelled,

    /// <summary>Uninstalling application.</summary>
    Uninstalling
}

/// <summary>
/// Progress information during installation.
/// </summary>
internal sealed record InstallationProgress
{
    /// <summary>Current installation state.</summary>
    public required InstallationState State { get; init; }

    /// <summary>Overall progress percentage (0-100).</summary>
    public double Percentage { get; init; }

    /// <summary>Description of current step.</summary>
    public string? CurrentStep { get; init; }

    /// <summary>Bytes downloaded (for downloading state).</summary>
    public long? BytesDownloaded { get; init; }

    /// <summary>Total bytes to download.</summary>
    public long? TotalBytes { get; init; }
}

/// <summary>
/// Result of an installation attempt.
/// </summary>
internal sealed record InstallationResult
{
    /// <summary>Whether installation succeeded.</summary>
    public required bool Success { get; init; }

    /// <summary>Error message if failed.</summary>
    public string? Error { get; init; }

    /// <summary>
    /// Non-fatal note for successful results. Example: "3 file(s) were locked
    /// and skipped: QuickNotes.exe, QuickNotes.dll, ..." — set on uninstall
    /// when some tracked files couldn't be deleted but enough succeeded to
    /// consider the operation done.
    /// </summary>
    public string? Warning { get; init; }

    /// <summary>Path where application was installed.</summary>
    public string? InstallPath { get; init; }

    /// <summary>Creates a successful result.</summary>
    public static InstallationResult Succeeded(string installPath)
        => new() { Success = true, InstallPath = installPath };

    /// <summary>Creates a successful-with-warning result.</summary>
    public static InstallationResult SucceededWithWarning(string installPath, string warning)
        => new() { Success = true, InstallPath = installPath, Warning = warning };

    /// <summary>Creates a failed result.</summary>
    public static InstallationResult Failed(string error)
        => new() { Success = false, Error = error };

    /// <summary>Creates a cancelled result.</summary>
    public static InstallationResult Cancelled()
        => new() { Success = false, Error = "Installation was cancelled" };
}
