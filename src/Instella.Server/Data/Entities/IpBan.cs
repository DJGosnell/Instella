namespace Instella.Server.Data.Entities;

/// <summary>
/// Permanent IP ban entry.
/// </summary>
public class IpBan
{
    public long Id { get; set; }

    public string IpAddress { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public long CreatedByAdminId { get; set; }

    public AdminUser CreatedByAdmin { get; set; } = null!;
}
