namespace Instella.Server.Data.Entities;

public class ServerSettings
{
    public long Id { get; set; }

    // Storage configuration
    public StorageProviderType StorageProvider { get; set; } = StorageProviderType.Local;

    // Local storage settings
    public string LocalBasePath { get; set; } = "./packages";

    // S3 storage settings
    public S3ProviderPreset S3Preset { get; set; } = S3ProviderPreset.Custom;
    public string S3Endpoint { get; set; } = "";
    public string S3Bucket { get; set; } = "";
    public string S3AccessKey { get; set; } = "";
    public string S3SecretKey { get; set; } = "";  // Encrypted
    public string S3Region { get; set; } = "us-east-1";
    public int S3UrlExpiryMinutes { get; set; } = 60;  // Pre-signed URL expiry time

    /// <summary>
    /// Explicit opt-in to a plain-http S3 endpoint. Clients follow presigned-URL redirects,
    /// and .NET refuses https-to-http redirects, so an http endpoint would also break
    /// downloads for every https client; it is only for a local MinIO in development.
    /// </summary>
    public bool S3AllowInsecureEndpoint { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public enum StorageProviderType
{
    Local,
    S3
}

public enum S3ProviderPreset
{
    Custom,
    AwsS3,
    MinIO,
    CloudflareR2,
    BackblazeB2,
    DigitalOceanSpaces
}
