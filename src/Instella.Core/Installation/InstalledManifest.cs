using System.Text.Json;
using System.Text.Json.Serialization;
using Instella.Core.FileSystem;
using Instella.Core.Platform;
using Instella.Core.Logging;
using Instella.Core.Trust;

namespace Instella.Core.Installation;

/// <summary>
/// Manifest written to installation directory tracking what was installed.
/// Used by uninstaller, updater, and update detection.
/// </summary>
public sealed record InstalledManifest
{
    /// <summary>
    /// Manifest schema version: <c>4</c>. Readers accept 4 through their own current version and
    /// upgrade older ones in memory; anything lower than 4 is refused.
    /// </summary>
    public int ManifestVersion { get; init; } = InstallManifestWriter.CurrentVersion;

    /// <summary>Application display name.</summary>
    public required string AppName { get; init; }

    /// <summary>Application unique identifier.</summary>
    public required string AppId { get; init; }

    /// <summary>Installed version.</summary>
    public required Version Version { get; init; }

    /// <summary>Installation directory path.</summary>
    public required string InstallDirectory { get; init; }

    /// <summary>Main executable name.</summary>
    public required string ExecutableName { get; init; }

    /// <summary>When the application was installed.</summary>
    public required DateTime InstalledAt { get; init; }

    /// <summary>Whether a desktop shortcut was created.</summary>
    public bool HasDesktopShortcut { get; init; }

    /// <summary>Whether a start menu shortcut was created.</summary>
    public bool HasStartMenuShortcut { get; init; }

    /// <summary>Whether the app was added to PATH.</summary>
    public bool AddedToPath { get; init; }

    /// <summary>Whether auto-start was configured.</summary>
    public bool HasAutoStart { get; init; }

    /// <summary>Whether an OS "Installed Apps" / Add-Remove Programs entry was registered.</summary>
    public bool HasUninstallEntry { get; init; }

    /// <summary>Whether the install was scoped to the current user (true) or machine-wide (false). Controls HKCU vs HKLM for registry-based cleanup.</summary>
    public bool InstalledPerUser { get; init; }

    /// <summary>List of registered file extensions.</summary>
    public IReadOnlyList<string>? FileAssociations { get; init; }

    /// <summary>Target platform.</summary>
    public TargetPlatform Platform { get; init; }

    /// <summary>
    /// Architecture the build was installed as. Update requests use this, not the OS
    /// architecture, so an x64 build running under emulation on ARM64 stays x64.
    /// </summary>
    public Architecture? Architecture { get; init; }

    /// <summary>Server URL for updates.</summary>
    public string? ServerUrl { get; init; }

    /// <summary>Release channel the installation follows.</summary>
    public string? Channel { get; init; }

    /// <summary>
    /// Publisher keys this installation trusts. Updates are verified against these, never
    /// against anything passed on a command line; a verified release may rotate them.
    /// </summary>
    public IReadOnlyList<PublisherKey>? TrustedKeys { get; init; }

    /// <summary>The installer was built with <c>AllowUnsignedUpdates()</c>.</summary>
    public bool AllowUnsignedUpdates { get; init; }

    /// <summary>
    /// The installer was built with <c>AllowInsecureServer()</c>: <see cref="ServerUrl"/> may be
    /// plain http to a non-loopback host. The SDK and updater re-check the URL against
    /// <see cref="Wire.ServerUrlPolicy"/> with this flag before every request.
    /// </summary>
    public bool AllowInsecureServer { get; init; }

    /// <summary>
    /// The download token the installer was built with (<c>downloadToken</c>; optional). The
    /// updater and the SDK send it to <see cref="ServerUrl"/>; updates never change it.
    /// </summary>
    public string? DownloadToken { get; init; }

    /// <summary>All installed files with their hashes.</summary>
    public required IReadOnlyList<InstalledFile> Files { get; init; }

    /// <summary>
    /// Windows registry entries written by the install. The uninstaller
    /// reverses each via <see cref="IPlatformServices.DeleteRegistryValueAsync"/>.
    /// Empty on non-Windows or when the installer declared no registry
    /// writes.
    /// </summary>
    public IReadOnlyList<ManifestRegistryEntry>? Registry { get; init; }

    /// <summary>
    /// Mutations custom steps recorded with <c>ctx.Track*</c> (files, directories, registry
    /// values and keys, PATH entries). Uninstall reverses them in reverse order.
    /// </summary>
    public IReadOnlyList<ManifestTrackedItem>? TrackedItems { get; init; }

    /// <summary>User-declared CLI flags. Informational only; the installer binary itself is authoritative.</summary>
    public IReadOnlyList<ManifestCliFlag>? DeclaredCliFlags { get; init; }

    /// <summary>Logging configuration captured at install time.</summary>
    public ManifestLoggingConfig? Logging { get; init; }

    /// <summary>
    /// Properties this version does not know, kept so a read–modify–write (the updater's
    /// manifest refresh) never drops a field a newer installer wrote: a stub is never replaced,
    /// so it meets manifests from later 1.x installers (docs/compatibility.md).
    /// </summary>
    /// <remarks>A setter, not <c>init</c>: the source generator binds init-only members through
    /// its constructor delegate, which extension data cannot use. <c>with</c> still copies it.</remarks>
    [JsonExtensionData, JsonInclude]
    internal Dictionary<string, JsonElement>? UnknownFields { get; set; }
}

/// <summary>
/// Registry write descriptor persisted inside <c>.instella-manifest.json</c>. With
/// <paramref name="IsKey"/> it records a key the install created (uninstall deletes it,
/// after its values, only when it is empty); otherwise a value the install wrote.
/// </summary>
public sealed record ManifestRegistryEntry(
    RegistryHive Hive,
    string KeyPath,
    string ValueName,
    InstellaRegistryValueKind Kind,
    bool PerUser,
    bool IsKey = false);

/// <summary>A mutation a custom step tracked, replayed in reverse by uninstall.</summary>
/// <param name="Kind">file, directory, registry-value, registry-key or path-entry.</param>
/// <param name="Path">The file, directory or PATH entry, or the registry key path.</param>
/// <param name="Recursive">For a directory: delete its contents too.</param>
/// <param name="Hive">For a registry item: the hive.</param>
/// <param name="ValueName">For a registry value: its name.</param>
public sealed record ManifestTrackedItem(
    string Kind,
    string Path,
    bool Recursive = false,
    RegistryHive Hive = RegistryHive.AutoFromScope,
    string? ValueName = null);

/// <summary>Declared-CLI-flag descriptor persisted inside <c>.instella-manifest.json</c>.</summary>
public sealed record ManifestCliFlag(string Name, string TypeName, string? MapsTo);

/// <summary>Logging snapshot persisted inside <c>.instella-manifest.json</c>.</summary>
public sealed record ManifestLoggingConfig(
    InstellaLogLevel DefaultLevel,
    string? FileSinkPath,
    int RetainCount);

/// <summary>
/// Information about an installed file.
/// </summary>
public sealed record InstalledFile(string RelativePath, string Sha256, long Size);

/// <summary>
/// Reads and writes installation manifests.
/// </summary>
internal sealed class InstallManifestWriter
{
    private const string ManifestFileName = ".instella-manifest.json";
    private readonly IFileSystem _fs;

    public InstallManifestWriter(IFileSystem fs)
    {
        _fs = fs;
    }

    /// <summary>
    /// Writes the installed manifest to the installation directory.
    /// </summary>
    public async Task WriteAsync(string installPath, InstalledManifest manifest, CancellationToken ct)
    {
        var path = Path.Combine(installPath, ManifestFileName);
        var json = JsonSerializer.Serialize(manifest, InstalledManifestJsonContext.Default.InstalledManifest);
        await _fs.WriteAllBytesAsync(path, System.Text.Encoding.UTF8.GetBytes(json), ct);
    }

    /// <summary>Current installed-manifest schema version.</summary>
    public const int CurrentVersion = 4;

    /// <summary>
    /// Reads the installed manifest from the installation directory.
    /// Returns null if the manifest is missing, unreadable, or its
    /// <see cref="InstalledManifest.ManifestVersion"/> is not
    /// <see cref="CurrentVersion"/>. The uninstall runner treats a null
    /// return as <c>UninstallManifestMissing</c>; a manifest below version 4 is not adopted.
    /// </summary>
    public async Task<InstalledManifest?> ReadAsync(string installPath, CancellationToken ct)
    {
        var path = Path.Combine(installPath, ManifestFileName);
        if (!_fs.Exists(path))
            return null;

        var result = await _fs.ReadAllBytesAsync(path, ct);
        if (!result.Success || result.Value == null)
            return null;

        var json = System.Text.Encoding.UTF8.GetString(result.Value);
        try
        {
            var manifest = JsonSerializer.Deserialize(json, InstalledManifestJsonContext.Default.InstalledManifest);
            if (manifest is null) return null;
            if (manifest.ManifestVersion != CurrentVersion) return null;
            return manifest;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Checks if an installation manifest exists at the given path.
    /// </summary>
    public bool Exists(string installPath)
    {
        var path = Path.Combine(installPath, ManifestFileName);
        return _fs.Exists(path);
    }
}

/// <summary>
/// JSON serialization context for installed manifest.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(InstalledManifest))]
[JsonSerializable(typeof(InstalledFile))]
[JsonSerializable(typeof(ManifestRegistryEntry))]
[JsonSerializable(typeof(ManifestTrackedItem))]
[JsonSerializable(typeof(ManifestCliFlag))]
[JsonSerializable(typeof(ManifestLoggingConfig))]
[JsonSerializable(typeof(PublisherKey))]
internal partial class InstalledManifestJsonContext : JsonSerializerContext;
