using System.Text.Json.Serialization;

namespace Instella.Core.Trust;

/// <summary>
/// Describes exactly one build: one app, one version, one OS/arch, one channel, and every
/// file's path, size and SHA-256. Produced and signed by the publisher's CLI; the server
/// stores and relays the signed bytes unmodified. Clients trust file contents only when
/// they match a verified release manifest.
/// </summary>
public sealed record ReleaseManifest
{
    /// <summary>The only format version this release reads or writes.</summary>
    public const int CurrentFormatVersion = 1;

    /// <summary>Format version; must equal <see cref="CurrentFormatVersion"/>.</summary>
    public required int FormatVersion { get; init; }

    /// <summary>Application id.</summary>
    public required string AppId { get; init; }

    /// <summary>Version this release describes.</summary>
    public required Version Version { get; init; }

    /// <summary>Canonical OS name (<see cref="Wire.PlatformStrings.Os"/>).</summary>
    public required string Os { get; init; }

    /// <summary>Canonical architecture name (<see cref="Wire.PlatformStrings.Arch"/>).</summary>
    public required string Arch { get; init; }

    /// <summary>Release channel.</summary>
    public required string Channel { get; init; }

    /// <summary>When the publisher created the manifest.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Every file of the build.</summary>
    public required IReadOnlyList<ReleaseFile> Files { get; init; }

    /// <summary>
    /// Optional key rotation: the complete list of keys to trust after this release is
    /// installed. Accepted only because this release verified against an already-trusted key.
    /// </summary>
    public IReadOnlyList<PublisherKey>? TrustedKeys { get; init; }

    /// <summary>
    /// Optional installers published with this build (<see cref="Wire.InstallerKinds"/>). An
    /// older installer hands off to one of these only when its size and SHA-256 match this
    /// signed entry, so the server cannot substitute an installer.
    /// </summary>
    public IReadOnlyList<ReleaseInstaller>? Installers { get; init; }
}

/// <summary>One file of a release.</summary>
/// <param name="Path">Canonical relative path (forward slashes).</param>
/// <param name="Size">Size in bytes.</param>
/// <param name="Sha256">Lower-case hex SHA-256.</param>
/// <param name="Executable">Whether the file should be marked executable on POSIX systems.</param>
public sealed record ReleaseFile(string Path, long Size, string Sha256, bool Executable = false);

/// <summary>An installer published with a release.</summary>
/// <param name="Kind"><see cref="Wire.InstallerKinds.Online"/> or <see cref="Wire.InstallerKinds.Offline"/>.</param>
/// <param name="FileName">File name users download (no directory).</param>
/// <param name="Size">Size in bytes.</param>
/// <param name="Sha256">Lower-case hex SHA-256.</param>
public sealed record ReleaseInstaller(string Kind, string FileName, long Size, string Sha256);

/// <summary>A publisher public key.</summary>
/// <param name="KeyId">First 16 hex characters of SHA-256 over the SubjectPublicKeyInfo DER bytes.</param>
/// <param name="PublicKey">Base64 SubjectPublicKeyInfo DER of an ECDSA P-256 key.</param>
public sealed record PublisherKey(string KeyId, string PublicKey);

/// <summary>
/// Wire envelope for a signed release manifest. <see cref="Manifest"/> carries the exact
/// signed bytes; verifiers check the signature over those bytes and only then parse them,
/// so nothing ever needs to re-serialise a signed document.
/// </summary>
/// <param name="Manifest">Base64 of the exact signed manifest bytes (UTF-8 JSON).</param>
/// <param name="Signature">Base64 IEEE P1363 ECDSA P-256 signature (64 bytes).</param>
/// <param name="KeyId">Id of the signing key.</param>
public sealed record SignedRelease(string Manifest, string Signature, string KeyId);

/// <summary>A release failed verification. Nothing it describes may be installed.</summary>
public sealed class UpdateTrustException(string message) : Exception(message);

/// <summary>Source-generated JSON for trust documents.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ReleaseManifest))]
[JsonSerializable(typeof(SignedRelease))]
[JsonSerializable(typeof(PublisherKey))]
[JsonSerializable(typeof(IReadOnlyList<PublisherKey>))]
internal sealed partial class TrustJsonContext : JsonSerializerContext;
