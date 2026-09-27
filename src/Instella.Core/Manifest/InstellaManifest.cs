using System.Text.Json.Serialization;
using Instella.Core.Trust;
using Instella.Core.Wire;

namespace Instella.Core.Manifest;

/// <summary>
/// The central configuration manifest for an Instella installation.
/// Defines application metadata, installation options, and platform-specific overrides.
/// </summary>
public sealed record InstellaManifest
{
    /// <summary>The build-manifest schema this version of Instella writes and reads.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Schema version of this manifest. Readers accept any version up to
    /// <see cref="CurrentSchemaVersion"/> (an absent field reads as 0) and reject newer ones as
    /// "built by a newer Instella".
    /// </summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Human-readable application name.</summary>
    public required string AppName { get; init; }

    /// <summary>Unique application identifier (e.g., "com.example.myapp").</summary>
    public required string AppId { get; init; }

    /// <summary>Application version.</summary>
    public required Version Version { get; init; }

    /// <summary>URL of the Instella server for updates.</summary>
    public required string ServerUrl { get; init; }

    /// <summary>Name of the main executable file.</summary>
    public string? ExecutableName { get; init; }

    /// <summary>Shortcut creation configuration.</summary>
    public ShortcutConfig? Shortcuts { get; init; }

    /// <summary>Whether to add the install directory to PATH.</summary>
    public bool PathRegistration { get; init; }

    /// <summary>Whether to configure the application to start on login.</summary>
    public bool AutoStart { get; init; }

    /// <summary>File type associations to register.</summary>
    public IReadOnlyList<FileAssociation>? FileAssociations { get; init; }

    /// <summary>Prerequisites required before installation.</summary>
    public IReadOnlyList<Prerequisite>? Prerequisites { get; init; }


    /// <summary>How to handle elevation (admin privileges).</summary>
    public ElevationMode Elevation { get; init; }

    /// <summary>Path to the application icon.</summary>
    public string? IconPath { get; init; }

    /// <summary>Release channel (e.g., "stable", "beta", "nightly").</summary>
    public string Channel { get; init; } = "stable";

    /// <summary>Optional application description.</summary>
    public string? Description { get; init; }

    /// <summary>Optional publisher/company name.</summary>
    public string? Publisher { get; init; }

    /// <summary>Optional URL to the application homepage.</summary>
    public string? HomepageUrl { get; init; }

    /// <summary>Optional URL to the license information.</summary>
    public string? LicenseUrl { get; init; }

    /// <summary>
    /// Optional payload glob filter. When present, the
    /// <c>AppendPayloadToSelf</c> MSBuild task applies the include/exclude
    /// lists to the payload files (by zip-entry path) before zipping, so
    /// unwanted artifacts (debug PDBs, dev runtime-config files, source maps,
    /// etc.) never reach the shipping installer. Null means "include every
    /// collected payload file" (the pre-20.5 behavior).
    /// </summary>
    public PayloadFilter? PayloadFilter { get; init; }

    /// <summary>
    /// Publisher public keys (from <c>WithPublisherKey</c>). Copied into the installed
    /// manifest; every update must be signed by one of them.
    /// </summary>
    public IReadOnlyList<PublisherKey>? PublisherKeys { get; init; }

    /// <summary>Development escape hatch (<c>AllowUnsignedUpdates()</c>): the server is not verified.</summary>
    public bool AllowUnsignedUpdates { get; init; }

    /// <summary>Opt-in (<c>AllowInsecureServer()</c>) to a plain-http, non-loopback server URL.</summary>
    public bool AllowInsecureServer { get; init; }

    /// <summary>
    /// The download token from <c>WithDownloadToken</c> (<c>idt_…</c>), sent to the update server for a
    /// package that requires a key; null when none. Readable by anyone with the installer: not a secret.
    /// </summary>
    public string? DownloadToken { get; init; }

    /// <summary>
    /// Validates that all required fields have meaningful values.
    /// Call after deserialization to catch empty/whitespace strings.
    /// </summary>
    /// <exception cref="InvalidManifestException">Thrown when a required field is missing or empty.</exception>
    public void Validate()
    {
        if (SchemaVersion > CurrentSchemaVersion)
            throw new InvalidManifestException(nameof(SchemaVersion),
                $"schema version {SchemaVersion} was written by a newer Instella (this version reads up to {CurrentSchemaVersion})");
        if (string.IsNullOrWhiteSpace(AppName))
            throw new InvalidManifestException(nameof(AppName));
        if (string.IsNullOrWhiteSpace(AppId))
            throw new InvalidManifestException(nameof(AppId));
        if (string.IsNullOrWhiteSpace(ServerUrl))
            throw new InvalidManifestException(nameof(ServerUrl));
        if (Version is null)
            throw new InvalidManifestException(nameof(Version));
        if (ServerUrlPolicy.Check(ServerUrl, AllowInsecureServer) is { } urlProblem)
            throw new InvalidManifestException(nameof(ServerUrl), urlProblem);
    }
}

/// <summary>
/// JSON serialization context for manifest types.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(InstellaManifest))]
[JsonSerializable(typeof(ShortcutConfig))]
[JsonSerializable(typeof(FileAssociation))]
[JsonSerializable(typeof(Prerequisite))]
[JsonSerializable(typeof(PayloadFilter))]
[JsonSerializable(typeof(PublisherKey))]
internal partial class ManifestJsonContext : JsonSerializerContext;
