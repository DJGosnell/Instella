using System.Security.Cryptography;
using System.Text.Json;
using Instella.Core.FileSystem;
using Instella.Core.Installation;
using Instella.Core.Utilities;

namespace Instella.Core.Trust;

/// <summary>
/// Signs and verifies release manifests with ECDSA P-256 / SHA-256, IEEE P1363 encoding.
/// The signed message is <c>"INSTELLA-RELEASE-V1\n"</c> followed by the manifest bytes;
/// the prefix keeps a release signature from ever validating some other Instella document.
/// </summary>
public static class ReleaseSigner
{
    private static readonly byte[] Domain = "INSTELLA-RELEASE-V1\n"u8.ToArray();

    /// <summary>Serialises <paramref name="manifest"/> once and signs those exact bytes.</summary>
    public static SignedRelease Sign(ReleaseManifest manifest, ECDsa privateKey) =>
        Sign(JsonSerializer.SerializeToUtf8Bytes(manifest, TrustJsonContext.Default.ReleaseManifest), privateKey);

    /// <summary>Signs <paramref name="manifestUtf8"/> as-is.</summary>
    public static SignedRelease Sign(ReadOnlySpan<byte> manifestUtf8, ECDsa privateKey)
    {
        if (privateKey.KeySize != 256)
            throw new ArgumentException("Publisher keys must be ECDSA P-256.", nameof(privateKey));

        var signature = privateKey.SignData(Concat(Domain, manifestUtf8), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return new SignedRelease(
            Convert.ToBase64String(manifestUtf8),
            Convert.ToBase64String(signature),
            KeyIds.Compute(privateKey.ExportSubjectPublicKeyInfo()));
    }

    /// <summary>The exact bytes <paramref name="manifest"/> is signed as (the manifest's JSON serialisation).</summary>
    internal static byte[] Serialize(ReleaseManifest manifest) =>
        JsonSerializer.SerializeToUtf8Bytes(manifest, TrustJsonContext.Default.ReleaseManifest);

    /// <summary>
    /// The message a signature covers: the domain prefix followed by <paramref name="manifestUtf8"/>.
    /// External signers (a KMS that signs a SHA-256 digest) sign <c>SHA-256(message)</c>.
    /// </summary>
    internal static byte[] MessageFor(ReadOnlySpan<byte> manifestUtf8) => Concat(Domain, manifestUtf8);

    /// <summary>True when <paramref name="signature"/> is a valid signature of the bytes by <paramref name="key"/>.</summary>
    internal static bool Verify(ReadOnlySpan<byte> manifestUtf8, ReadOnlySpan<byte> signature, PublisherKey key)
    {
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(key.PublicKey), out _);
            if (ecdsa.KeySize != 256) return false;
            if (KeyIds.Compute(ecdsa.ExportSubjectPublicKeyInfo()) != key.KeyId) return false;
            return ecdsa.VerifyData(Concat(Domain, manifestUtf8), signature, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return false;
        }
    }

    private static byte[] Concat(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        var result = new byte[a.Length + b.Length];
        a.CopyTo(result);
        b.CopyTo(result.AsSpan(a.Length));
        return result;
    }
}

/// <summary>Key identifiers.</summary>
public static class KeyIds
{
    /// <summary>First 16 hex characters of SHA-256 over SubjectPublicKeyInfo DER bytes.</summary>
    public static string Compute(ReadOnlySpan<byte> subjectPublicKeyInfo) =>
        Convert.ToHexStringLower(SHA256.HashData(subjectPublicKeyInfo))[..16];

    /// <summary>Builds a <see cref="PublisherKey"/> from a base64 SubjectPublicKeyInfo string, validating it is P-256.</summary>
    /// <exception cref="ArgumentException">The value is not a base64 ECDSA P-256 public key.</exception>
    public static PublisherKey FromPublicKey(string publicKeyBase64)
    {
        try
        {
            var spki = Convert.FromBase64String(publicKeyBase64.Trim());
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(spki, out _);
            if (ecdsa.KeySize != 256)
                throw new ArgumentException("Publisher keys must be ECDSA P-256.", nameof(publicKeyBase64));
            var canonical = ecdsa.ExportSubjectPublicKeyInfo();
            return new PublisherKey(Compute(canonical), Convert.ToBase64String(canonical));
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            throw new ArgumentException($"Not a base64 ECDSA P-256 public key: {ex.Message}", nameof(publicKeyBase64), ex);
        }
    }
}

/// <summary>
/// Everything a verifier must check beyond the signature.
/// </summary>
/// <param name="TrustedKeys">Keys the installation trusts (from the installed or build manifest).</param>
/// <param name="AppId">The installation's app id.</param>
/// <param name="Os">The installation's canonical OS name.</param>
/// <param name="Arch">The architecture the installation was installed as.</param>
/// <param name="MustBeNewerThan">For updates: the installed version (blocks replay/downgrade).</param>
/// <param name="MustEqual">For the lite installer and repair: the exact expected version.</param>
public sealed record TrustPolicy(
    IReadOnlyList<PublisherKey> TrustedKeys,
    string AppId,
    string Os,
    string Arch,
    Version? MustBeNewerThan,
    Version? MustEqual)
{
    /// <summary>
    /// For updates: the channel the update was requested on. A release on another channel is
    /// refused (compared case-insensitively). Null skips the check (repair, lite installer).
    /// </summary>
    public string? Channel { get; init; }
}

/// <summary>Verifies a <see cref="SignedRelease"/> against a <see cref="TrustPolicy"/>.</summary>
public static class ReleaseVerifier
{
    /// <summary>
    /// Returns the parsed manifest only when the signature verifies against a trusted key
    /// and every policy check holds; otherwise throws <see cref="UpdateTrustException"/>.
    /// </summary>
    public static ReleaseManifest Verify(SignedRelease signed, TrustPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(signed);
        ArgumentNullException.ThrowIfNull(policy);

        var key = policy.TrustedKeys.FirstOrDefault(k => k.KeyId == signed.KeyId)
            ?? throw new UpdateTrustException($"release is signed by unknown key {signed.KeyId}");

        byte[] bytes, signature;
        try
        {
            bytes = Convert.FromBase64String(signed.Manifest);
            signature = Convert.FromBase64String(signed.Signature);
        }
        catch (FormatException)
        {
            throw new UpdateTrustException("release envelope is not valid base64");
        }

        if (!ReleaseSigner.Verify(bytes, signature, key))
            throw new UpdateTrustException("release signature is invalid");

        ReleaseManifest? m;
        try
        {
            m = JsonSerializer.Deserialize(bytes, TrustJsonContext.Default.ReleaseManifest);
        }
        catch (JsonException ex)
        {
            throw new UpdateTrustException($"release manifest is malformed: {ex.Message}");
        }
        if (m is null) throw new UpdateTrustException("release manifest is empty");

        if (m.FormatVersion != ReleaseManifest.CurrentFormatVersion)
            throw new UpdateTrustException($"unsupported release format {m.FormatVersion}");
        if (!string.Equals(m.AppId, policy.AppId, StringComparison.Ordinal))
            throw new UpdateTrustException($"appId mismatch: release is for '{m.AppId}'");
        if (!string.Equals(m.Os, policy.Os, StringComparison.Ordinal) || !string.Equals(m.Arch, policy.Arch, StringComparison.Ordinal))
            throw new UpdateTrustException($"platform mismatch: release is for {m.Os}/{m.Arch}");
        if (policy.MustBeNewerThan is { } floor && AppVersions.Compare(m.Version, floor) <= 0)
            throw new UpdateTrustException($"release {m.Version} is not newer than {floor} (replay/downgrade)");
        if (policy.MustEqual is { } pin && !AppVersions.Equal(m.Version, pin))
            throw new UpdateTrustException($"unexpected release version {m.Version}; expected {pin}");
        if (policy.Channel is { } channel && !string.Equals(m.Channel, channel, StringComparison.OrdinalIgnoreCase))
            throw new UpdateTrustException($"channel mismatch: release is on '{m.Channel}', expected '{channel}'");

        // Windows is case-insensitive, so A.dll and a.dll would collapse into one file.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in m.Files)
        {
            if (!SafePath.TryNormalizeRelative(f.Path, out var normalized, out var why) || normalized != f.Path)
                throw new UpdateTrustException($"release lists unsafe path '{f.Path}': {(why.Length > 0 ? why : "not canonical")}");
            if (InstellaOwnedPaths.IsOwned(f.Path))
                throw new UpdateTrustException($"release may not contain Instella-owned path '{f.Path}'");
            if (!seen.Add(f.Path))
                throw new UpdateTrustException($"duplicate path '{f.Path}'");
            if (f.Size < 0 || f.Sha256.Length != 64 || !f.Sha256.All(char.IsAsciiHexDigitLower))
                throw new UpdateTrustException($"bad entry for '{f.Path}'");
        }

        if (m.TrustedKeys is { Count: 0 })
            throw new UpdateTrustException("release rotates to an empty trusted-key list");
        foreach (var k in m.TrustedKeys ?? [])
        {
            PublisherKey parsed;
            try { parsed = KeyIds.FromPublicKey(k.PublicKey); }
            catch (ArgumentException) { throw new UpdateTrustException($"release lists an invalid trusted key {k.KeyId}"); }
            if (parsed.KeyId != k.KeyId)
                throw new UpdateTrustException($"trusted key {k.KeyId} does not match its public key");
        }

        return m;
    }

    /// <summary>
    /// The trusted-key list to record after installing <paramref name="release"/>: its
    /// rotation list when present, otherwise the keys trusted before.
    /// </summary>
    public static IReadOnlyList<PublisherKey> KeysAfter(ReleaseManifest release, IReadOnlyList<PublisherKey> current) =>
        release.TrustedKeys ?? current;
}
