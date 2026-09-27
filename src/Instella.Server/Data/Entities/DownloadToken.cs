namespace Instella.Server.Data.Entities;

/// <summary>
/// A download-only credential for one package: it satisfies
/// <see cref="DownloadAccessMode.PackageKeyRequired"/> for that package and nothing else. It is
/// compiled into installers, so anyone with the installer can read it: it limits casual access,
/// it is not a secret. Only its hash is stored.
/// </summary>
public class DownloadToken
{
    public long Id { get; set; }

    public long PackageId { get; set; }

    public Package Package { get; set; } = null!;

    /// <summary>What the admin called it ("QuickNotes 1.x installers").</summary>
    public required string Name { get; set; }

    /// <summary>Lowercase-hex SHA-256 of the whole token (<see cref="Instella.Core.Wire.DownloadTokens.Hash"/>).</summary>
    public required string TokenHash { get; set; }

    /// <summary>The first 8 characters after <c>idt_</c>, to recognise the token in the UI.</summary>
    public required string DisplayPrefix { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>After this time the token no longer works; null means never.</summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>Last successful use, written at most once an hour.</summary>
    public DateTime? LastUsedAt { get; set; }

    public bool IsRevoked { get; set; }
}
