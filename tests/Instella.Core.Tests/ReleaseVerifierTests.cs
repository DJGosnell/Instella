using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Instella.Core.Trust;
using Instella.Core.Wire;
using NUnit.Framework;

namespace Instella.Core.Tests;

/// <summary>Every rule in <see cref="ReleaseVerifier"/>.</summary>
[TestFixture]
public class ReleaseVerifierTests
{
    private ECDsa _keyA = null!;
    private ECDsa _keyB = null!;
    private PublisherKey _pubA = null!;
    private PublisherKey _pubB = null!;

    private static readonly string Hash = new('a', 64);

    [SetUp]
    public void SetUp()
    {
        _keyA = ReleaseKeys.Generate();
        _keyB = ReleaseKeys.Generate();
        _pubA = ReleaseKeys.PublicKeyOf(_keyA);
        _pubB = ReleaseKeys.PublicKeyOf(_keyB);
    }

    [TearDown]
    public void TearDown()
    {
        _keyA.Dispose();
        _keyB.Dispose();
    }

    private static ReleaseManifest Manifest(
        string version = "1.3.0", string appId = "com.example.app", string os = "windows", string arch = "x64",
        IReadOnlyList<ReleaseFile>? files = null, IReadOnlyList<PublisherKey>? trustedKeys = null, int format = 1) => new()
    {
        FormatVersion = format,
        AppId = appId,
        Version = Version.Parse(version),
        Os = os,
        Arch = arch,
        Channel = "stable",
        CreatedAt = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero),
        Files = files ?? [new ReleaseFile("App.exe", 10, Hash, Executable: true), new ReleaseFile("lib/a.dll", 5, Hash)],
        TrustedKeys = trustedKeys,
    };

    private TrustPolicy Policy(Version? newerThan = null, Version? equal = null, params PublisherKey[] keys) =>
        new(keys.Length > 0 ? keys : [_pubA], "com.example.app", "windows", "x64", newerThan, equal);

    [Test]
    public void ValidRelease_Verifies()
    {
        var signed = ReleaseSigner.Sign(Manifest(), _keyA);
        var m = ReleaseVerifier.Verify(signed, Policy(newerThan: new Version(1, 2, 0)));
        Assert.That(m.Version, Is.EqualTo(new Version(1, 3, 0)));
        Assert.That(signed.KeyId, Is.EqualTo(_pubA.KeyId));
    }

    [Test]
    public void ChannelPolicy_RefusesAReleaseOnAnotherChannel_CaseInsensitively()
    {
        var signed = ReleaseSigner.Sign(Manifest(), _keyA); // channel "stable"
        Assert.That(ReleaseVerifier.Verify(signed, Policy() with { Channel = "Stable" }).Channel, Is.EqualTo("stable"));
        Assert.That(ReleaseVerifier.Verify(signed, Policy()).Channel, Is.EqualTo("stable"), "no channel in the policy skips the check");
        var ex = Assert.Throws<UpdateTrustException>(() => ReleaseVerifier.Verify(signed, Policy() with { Channel = "beta" }));
        Assert.That(ex!.Message, Does.Contain("channel mismatch"));
    }

    [Test]
    public void FlippedManifestByte_IsRejected()
    {
        var signed = ReleaseSigner.Sign(Manifest(), _keyA);
        var bytes = Convert.FromBase64String(signed.Manifest);
        bytes[^5] ^= 0x01;
        Assert.Throws<UpdateTrustException>(() =>
            ReleaseVerifier.Verify(signed with { Manifest = Convert.ToBase64String(bytes) }, Policy()));
    }

    [Test]
    public void FlippedSignatureByte_IsRejected()
    {
        var signed = ReleaseSigner.Sign(Manifest(), _keyA);
        var sig = Convert.FromBase64String(signed.Signature);
        sig[10] ^= 0x01;
        Assert.Throws<UpdateTrustException>(() =>
            ReleaseVerifier.Verify(signed with { Signature = Convert.ToBase64String(sig) }, Policy()));
    }

    [Test]
    public void UnknownKeyId_IsRejected()
    {
        var signed = ReleaseSigner.Sign(Manifest(), _keyB);
        var ex = Assert.Throws<UpdateTrustException>(() => ReleaseVerifier.Verify(signed, Policy()));
        Assert.That(ex!.Message, Does.Contain("unknown key"));
    }

    [Test]
    public void KnownKeyIdWithWrongKey_IsRejected()
    {
        // Signed by B but claiming A's key id: the lookup succeeds, the signature must not.
        var signed = ReleaseSigner.Sign(Manifest(), _keyB) with { KeyId = _pubA.KeyId };
        Assert.Throws<UpdateTrustException>(() => ReleaseVerifier.Verify(signed, Policy()));
    }

    [Test]
    public void DerEncodedSignature_IsRejected()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(Manifest(), TrustJsonContext.Default.ReleaseManifest);
        var message = Encoding.ASCII.GetBytes("INSTELLA-RELEASE-V1\n").Concat(bytes).ToArray();
        var der = _keyA.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        var signed = new SignedRelease(Convert.ToBase64String(bytes), Convert.ToBase64String(der), _pubA.KeyId);
        Assert.Throws<UpdateTrustException>(() => ReleaseVerifier.Verify(signed, Policy()));
    }

    [Test]
    public void SignatureWithoutDomainPrefix_IsRejected()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(Manifest(), TrustJsonContext.Default.ReleaseManifest);
        var raw = _keyA.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var signed = new SignedRelease(Convert.ToBase64String(bytes), Convert.ToBase64String(raw), _pubA.KeyId);
        Assert.Throws<UpdateTrustException>(() => ReleaseVerifier.Verify(signed, Policy()));
    }

    [TestCase("com.other.app", "windows", "x64", "appId")]
    [TestCase("com.example.app", "linux", "x64", "platform")]
    [TestCase("com.example.app", "windows", "arm64", "platform")]
    public void IdentityMismatch_IsRejected(string appId, string os, string arch, string expectedWord)
    {
        var signed = ReleaseSigner.Sign(Manifest(appId: appId, os: os, arch: arch), _keyA);
        var ex = Assert.Throws<UpdateTrustException>(() => ReleaseVerifier.Verify(signed, Policy()));
        Assert.That(ex!.Message, Does.Contain(expectedWord));
    }

    [TestCase("1.3.0")]
    [TestCase("1.2.9")]
    public void EqualOrOlderVersion_IsRejectedAsReplay(string installed)
    {
        var signed = ReleaseSigner.Sign(Manifest(version: "1.3.0"), _keyA);
        Assert.Throws<UpdateTrustException>(() =>
            ReleaseVerifier.Verify(signed, Policy(newerThan: new Version(installed == "1.2.9" ? "1.3.1" : installed))));
    }

    [Test]
    public void PinnedVersion_MustMatch()
    {
        var signed = ReleaseSigner.Sign(Manifest(version: "1.3.0"), _keyA);
        Assert.That(ReleaseVerifier.Verify(signed, Policy(equal: new Version(1, 3, 0))).Version, Is.EqualTo(new Version(1, 3, 0)));
        Assert.Throws<UpdateTrustException>(() => ReleaseVerifier.Verify(signed, Policy(equal: new Version(1, 4, 0))));
    }

    [Test]
    public void SameReleaseSpelledShorter_IsNotNewer()
    {
        // System.Version says 1.3 < 1.3.0, so an installed "1.3" once let 1.3.0 through as newer.
        var signed = ReleaseSigner.Sign(Manifest(version: "1.3.0"), _keyA);
        var ex = Assert.Throws<UpdateTrustException>(() => ReleaseVerifier.Verify(signed, Policy(newerThan: new Version(1, 3))));
        Assert.That(ex!.Message, Does.Contain("not newer"));
    }

    [Test]
    public void PinnedVersion_ComparesCanonicalForms()
    {
        var signed = ReleaseSigner.Sign(Manifest(version: "1.3.0"), _keyA);
        Assert.That(ReleaseVerifier.Verify(signed, Policy(equal: new Version(1, 3))).Version, Is.EqualTo(new Version(1, 3, 0)));
        Assert.That(ReleaseVerifier.Verify(signed, Policy(equal: new Version(1, 3, 0, 0))).Version, Is.EqualTo(new Version(1, 3, 0)));
    }

    [Test]
    public void UnsupportedFormatVersion_IsRejected()
    {
        var signed = ReleaseSigner.Sign(Manifest(format: 2), _keyA);
        Assert.Throws<UpdateTrustException>(() => ReleaseVerifier.Verify(signed, Policy()));
    }

    [TestCase("../evil.dll")]
    [TestCase("C:\\evil.dll")]
    [TestCase("CON")]
    [TestCase("a/./b.dll")]
    [TestCase("a\\b.dll")]
    [TestCase("instella.exe")]
    [TestCase(".instella-manifest.json")]
    [TestCase(".instella/txn/journal.json")]
    public void UnsafeOrOwnedPath_IsRejected(string path)
    {
        var signed = ReleaseSigner.Sign(Manifest(files: [new ReleaseFile(path, 1, Hash)]), _keyA);
        Assert.Throws<UpdateTrustException>(() => ReleaseVerifier.Verify(signed, Policy()));
    }

    [Test]
    public void DuplicatePathsDifferingInCase_AreRejected()
    {
        var signed = ReleaseSigner.Sign(Manifest(files: [new ReleaseFile("A.dll", 1, Hash), new ReleaseFile("a.dll", 1, Hash)]), _keyA);
        var ex = Assert.Throws<UpdateTrustException>(() => ReleaseVerifier.Verify(signed, Policy()));
        Assert.That(ex!.Message, Does.Contain("duplicate"));
    }

    [TestCase("ABCDEF")]
    [TestCase("")]
    public void MalformedHash_IsRejected(string hash)
    {
        var signed = ReleaseSigner.Sign(Manifest(files: [new ReleaseFile("a.dll", 1, hash.Length == 0 ? "" : hash.PadRight(64, 'A'))]), _keyA);
        Assert.Throws<UpdateTrustException>(() => ReleaseVerifier.Verify(signed, Policy()));
    }

    [Test]
    public void RotationChain_AThenABThenB_IsAccepted()
    {
        // Installed with [A]. Release N (signed by A) rotates to [A, B].
        IReadOnlyList<PublisherKey> trusted = [_pubA];
        var n = ReleaseVerifier.Verify(
            ReleaseSigner.Sign(Manifest(version: "1.1.0", trustedKeys: [_pubA, _pubB]), _keyA),
            Policy(newerThan: new Version(1, 0, 0), keys: [.. trusted]));
        trusted = ReleaseVerifier.KeysAfter(n, trusted);
        Assert.That(trusted.Select(k => k.KeyId), Is.EquivalentTo(new[] { _pubA.KeyId, _pubB.KeyId }));

        // Release N+1 (signed by B) rotates to [B].
        var n1 = ReleaseVerifier.Verify(
            ReleaseSigner.Sign(Manifest(version: "1.2.0", trustedKeys: [_pubB]), _keyB),
            Policy(newerThan: new Version(1, 1, 0), keys: [.. trusted]));
        trusted = ReleaseVerifier.KeysAfter(n1, trusted);
        Assert.That(trusted.Select(k => k.KeyId), Is.EqualTo(new[] { _pubB.KeyId }));

        // A is no longer trusted.
        Assert.Throws<UpdateTrustException>(() => ReleaseVerifier.Verify(
            ReleaseSigner.Sign(Manifest(version: "1.3.0"), _keyA), Policy(newerThan: new Version(1, 2, 0), keys: [.. trusted])));
    }

    [Test]
    public void SignedByBBeforeBIsTrusted_IsRejected()
    {
        var signed = ReleaseSigner.Sign(Manifest(trustedKeys: [_pubB]), _keyB);
        Assert.Throws<UpdateTrustException>(() => ReleaseVerifier.Verify(signed, Policy(keys: [_pubA])));
    }

    [Test]
    public void RotationToEmptyOrForgedKeyList_IsRejected()
    {
        Assert.Throws<UpdateTrustException>(() =>
            ReleaseVerifier.Verify(ReleaseSigner.Sign(Manifest(trustedKeys: []), _keyA), Policy()));
        Assert.Throws<UpdateTrustException>(() =>
            ReleaseVerifier.Verify(ReleaseSigner.Sign(Manifest(trustedKeys: [_pubB with { KeyId = _pubA.KeyId }]), _keyA), Policy()));
    }

    [Test]
    public void KeyIds_AreStableAndValidated()
    {
        Assert.That(KeyIds.FromPublicKey(_pubA.PublicKey), Is.EqualTo(_pubA));
        Assert.Throws<ArgumentException>(() => KeyIds.FromPublicKey("not base64"));
        using var rsa = RSA.Create(2048);
        Assert.Throws<ArgumentException>(() => KeyIds.FromPublicKey(Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo())));
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        Assert.Throws<ArgumentException>(() => KeyIds.FromPublicKey(Convert.ToBase64String(p384.ExportSubjectPublicKeyInfo())));
    }

    [Test]
    public void PrivateKeyPem_RoundTripsWithAndWithoutPassword()
    {
        var plain = ReleaseKeys.ImportPrivateKeyPem(ReleaseKeys.ExportPrivateKeyPem(_keyA, null), null);
        Assert.That(ReleaseKeys.PublicKeyOf(plain), Is.EqualTo(_pubA));

        var encrypted = ReleaseKeys.ExportPrivateKeyPem(_keyA, "s3cret");
        Assert.That(encrypted, Does.Contain("ENCRYPTED PRIVATE KEY"));
        Assert.That(ReleaseKeys.PublicKeyOf(ReleaseKeys.ImportPrivateKeyPem(encrypted, "s3cret")), Is.EqualTo(_pubA));
        Assert.Throws<ArgumentException>(() => ReleaseKeys.ImportPrivateKeyPem(encrypted, "wrong"));
        Assert.Throws<ArgumentException>(() => ReleaseKeys.ImportPrivateKeyPem(encrypted, null));
    }
}

[TestFixture]
public class ServerUrlPolicyTests
{
    [TestCase("https://updates.example.com", false, true)]
    [TestCase("http://localhost:5000", false, true)]
    [TestCase("http://127.0.0.1:5000", false, true)]
    [TestCase("http://127.8.9.10", false, true)]
    [TestCase("http://[::1]:8080", false, true)]
    [TestCase("http://updates.example.com", false, false)]
    [TestCase("http://updates.example.com", true, true)]
    [TestCase("ftp://updates.example.com", true, false)]
    [TestCase("updates.example.com", false, false)]
    [TestCase("", false, false)]
    public void Check(string url, bool allowInsecure, bool accepted)
    {
        Assert.That(ServerUrlPolicy.Check(url, allowInsecure) is null, Is.EqualTo(accepted));
    }
}
