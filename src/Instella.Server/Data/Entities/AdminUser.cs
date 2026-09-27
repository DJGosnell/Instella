namespace Instella.Server.Data.Entities;

/// <summary>
/// Admin user for Blazor UI authentication.
/// </summary>
public class AdminUser
{
    public long Id { get; set; }

    /// <summary>
    /// Unique username.
    /// </summary>
    public required string Username { get; set; }

    /// <summary>
    /// Password hash (using ASP.NET Core Identity PasswordHasher).
    /// </summary>
    public required string PasswordHash { get; set; }

    /// <summary>
    /// TOTP secret (encrypted, nullable).
    /// </summary>
    public string? TotpSecret { get; set; }

    /// <summary>
    /// Whether TOTP 2FA is enabled.
    /// </summary>
    public bool TotpEnabled { get; set; }

    /// <summary>
    /// TOTP time step of the last accepted code. A code is accepted only for a later step, so an
    /// observed code cannot be replayed within its validity window.
    /// </summary>
    public long LastTotpTimeStep { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
