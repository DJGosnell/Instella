using System.Security.Cryptography;

namespace Instella.Core.Trust;

/// <summary>
/// Publisher key material: generation and PEM import/export. Private keys are PKCS#8
/// PEM, encrypted when a password is given; public keys travel as base64
/// SubjectPublicKeyInfo DER (what <c>WithPublisherKey</c> takes).
/// </summary>
public static class ReleaseKeys
{
    /// <summary>Creates a new ECDSA P-256 key.</summary>
    public static ECDsa Generate() => ECDsa.Create(ECCurve.NamedCurves.nistP256);

    /// <summary>PKCS#8 PEM of the private key; encrypted with AES-256-CBC / PBKDF2 when <paramref name="password"/> is set.</summary>
    public static string ExportPrivateKeyPem(ECDsa key, string? password)
    {
        if (string.IsNullOrEmpty(password))
            return key.ExportPkcs8PrivateKeyPem();
        var pbe = new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 600_000);
        return key.ExportEncryptedPkcs8PrivateKeyPem(password.AsSpan(), pbe);
    }

    /// <summary>Imports a private key from PEM text (encrypted or not).</summary>
    /// <exception cref="ArgumentException">The PEM is not an ECDSA P-256 private key, or the password is wrong.</exception>
    public static ECDsa ImportPrivateKeyPem(string pem, string? password)
    {
        var key = ECDsa.Create();
        try
        {
            if (pem.Contains("ENCRYPTED PRIVATE KEY", StringComparison.Ordinal))
            {
                if (string.IsNullOrEmpty(password))
                    throw new ArgumentException("The signing key is encrypted; a password is required.");
                key.ImportFromEncryptedPem(pem, password);
            }
            else
            {
                key.ImportFromPem(pem);
            }
        }
        catch (CryptographicException ex)
        {
            key.Dispose();
            throw new ArgumentException($"Could not read the signing key: {ex.Message}", ex);
        }
        catch
        {
            key.Dispose();
            throw;
        }

        if (key.KeySize != 256)
        {
            key.Dispose();
            throw new ArgumentException("Publisher keys must be ECDSA P-256.");
        }
        return key;
    }

    /// <summary>The <see cref="PublisherKey"/> (key id + base64 SPKI) for a key pair.</summary>
    public static PublisherKey PublicKeyOf(ECDsa key)
    {
        var spki = key.ExportSubjectPublicKeyInfo();
        return new PublisherKey(KeyIds.Compute(spki), Convert.ToBase64String(spki));
    }
}
