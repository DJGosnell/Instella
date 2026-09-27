namespace Instella.Server.Data.Entities;

/// <summary>
/// Types of security events that are logged.
/// </summary>
public enum SecurityEventType
{
    LoginFailed,
    LoginBlocked,
    LoginSuccess,
    UploadStarted,
    UploadFailed,
    UploadBlocked,
    UploadSuccess,
    ApiKeyInvalid,
    HashMismatch,
    IpBanned,
    IpUnbanned,
    TempBlocksCleared,
    DownloadDeniedNoKey,
    DownloadDeniedInvalidKey,
    DownloadDeniedInsufficientPermission,
    ChannelPinChanged,
    DownloadTokenCreated,
    DownloadTokenRevoked
}

/// <summary>
/// Security event audit log entry.
/// </summary>
public class SecurityEvent
{
    public long Id { get; set; }

    public SecurityEventType EventType { get; set; }

    public string IpAddress { get; set; } = string.Empty;

    public string? Username { get; set; }

    public string? ApiKeyName { get; set; }

    public string? PackageId { get; set; }

    public string? Details { get; set; }

    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
