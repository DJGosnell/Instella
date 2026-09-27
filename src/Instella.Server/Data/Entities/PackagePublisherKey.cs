namespace Instella.Server.Data.Entities;

/// <summary>
/// A publisher public key registered for a package. When a package has at least one,
/// <c>upload/complete</c> rejects releases that are unsigned or not signed by one of them.
/// This is defence in depth that catches CI misconfiguration early; clients never rely on
/// it — they verify against the keys compiled into their own installer.
/// </summary>
public class PackagePublisherKey
{
    public long Id { get; set; }

    public long PackageId { get; set; }

    public Package Package { get; set; } = null!;

    /// <summary>Key id (first 16 hex chars of SHA-256 over the SPKI).</summary>
    public required string KeyId { get; set; }

    /// <summary>Base64 SubjectPublicKeyInfo DER.</summary>
    public required string PublicKey { get; set; }

    /// <summary>Optional label shown in the admin UI.</summary>
    public string? Label { get; set; }

    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
}
