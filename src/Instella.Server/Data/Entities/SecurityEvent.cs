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
    DownloadTokenRevoked,

    // Release approval. Stored as numbers: append only.

    /// <summary>A build is held back by the package's release approval.</summary>
    ReleasePending,
    ReleaseApproved,

    /// <summary>An unpublished build was rejected (deleted).</summary>
    ReleaseRejected,

    /// <summary>A delayed release's hold ended and the server published it.</summary>
    ReleaseAutoPublished,

    /// <summary>The server did not publish a delayed release: its key is no longer registered.</summary>
    ReleaseAutoPublishBlocked,
    ReleaseApprovalChanged,
    PublisherKeyAdded,
    PublisherKeyRemoved,

    /// <summary><c>instella publish</c> signed a draft.</summary>
    DraftSigned
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
