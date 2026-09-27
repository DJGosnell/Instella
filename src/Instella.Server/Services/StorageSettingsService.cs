using Instella.Server.Data;
using Instella.Server.Data.Entities;
using Instella.Server.Storage;
using Microsoft.EntityFrameworkCore;

namespace Instella.Server.Services;

public class StorageSettingsService
{
    private readonly AppDbContext _db;
    private readonly SecretProtector _secrets;
    private readonly IStorageProviderAccessor? _storage;

    private readonly string? _configDirectory;

    /// <param name="db">Database.</param>
    /// <param name="secrets">Protects the S3 secret.</param>
    /// <param name="storage">Rebuilt after a save.</param>
    /// <param name="configDirectory">The server's config directory; a local storage folder may not overlap it.</param>
    public StorageSettingsService(AppDbContext db, SecretProtector secrets, IStorageProviderAccessor? storage = null,
        ServerConfigDirectory? configDirectory = null)
    {
        _db = db;
        _secrets = secrets;
        _storage = storage;
        _configDirectory = configDirectory?.Path;
    }

    /// <summary>
    /// Why <paramref name="basePath"/> cannot be the local storage folder, or null: it
    /// must be absolute, not a filesystem root, and neither be nor contain the config directory
    /// (the database and the Data Protection keys). The sweeper deletes stray files in Instella's
    /// layout there, so the folder must be Instella's alone.
    /// </summary>
    public static string? ValidateLocalBasePath(string? basePath, string? configDirectory)
    {
        if (string.IsNullOrWhiteSpace(basePath) || !Path.IsPathRooted(basePath))
            return "The storage folder must be an absolute path, for example /packages.";
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(basePath));
        var root = Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full) ?? "");
        if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase) || full.Length <= root.Length)
            return "The storage folder cannot be the root of a drive or filesystem.";
        if (!string.IsNullOrEmpty(configDirectory))
        {
            var config = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configDirectory));
            if (Overlaps(full, config))
                return "The storage folder cannot be or contain the server's config directory.";
        }
        return null;

        static bool Overlaps(string outer, string inner) =>
            string.Equals(outer, inner, StringComparison.OrdinalIgnoreCase)
            || inner.StartsWith(outer + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The saved settings, detached, with the S3 secret decrypted for display and use. Being
    /// detached, the decrypted value can never be written back by accident.
    /// </summary>
    public async Task<ServerSettings> GetSettingsAsync()
    {
        var settings = await _db.ServerSettings.AsNoTracking().FirstOrDefaultAsync();
        if (settings is null) return new ServerSettings();
        settings.S3SecretKey = _secrets.Unprotect(settings.S3SecretKey) ?? "";
        return settings;
    }

    public async Task<ServerSettings> SaveSettingsAsync(ServerSettings settings)
    {
        if (settings.StorageProvider == StorageProviderType.S3 && ValidateS3Endpoint(settings) is { } problem)
            throw new InvalidOperationException(problem);
        if (settings.StorageProvider == StorageProviderType.Local
            && ValidateLocalBasePath(settings.LocalBasePath, _configDirectory) is { } pathProblem)
            throw new InvalidOperationException(pathProblem);

        var existing = await _db.ServerSettings.FirstOrDefaultAsync();
        var protectedSecret = _secrets.Protect(settings.S3SecretKey) ?? "";

        if (existing == null)
        {
            settings.CreatedAt = DateTime.UtcNow;
            settings.UpdatedAt = DateTime.UtcNow;
            settings.S3SecretKey = protectedSecret;
            _db.ServerSettings.Add(settings);
        }
        else
        {
            existing.StorageProvider = settings.StorageProvider;
            existing.LocalBasePath = settings.LocalBasePath;
            existing.S3Preset = settings.S3Preset;
            existing.S3Endpoint = settings.S3Endpoint;
            existing.S3Bucket = settings.S3Bucket;
            existing.S3AccessKey = settings.S3AccessKey;
            existing.S3SecretKey = protectedSecret;
            existing.S3Region = settings.S3Region;
            existing.S3UrlExpiryMinutes = settings.S3UrlExpiryMinutes;
            existing.S3AllowInsecureEndpoint = settings.S3AllowInsecureEndpoint;
            existing.UpdatedAt = DateTime.UtcNow;
            settings = existing;
        }

        await _db.SaveChangesAsync();

        // The next storage access rebuilds the provider from these settings.
        _storage?.Invalidate();
        return settings;
    }

    /// <summary>Null when the S3 endpoint is acceptable; otherwise why it is not.</summary>
    public static string? ValidateS3Endpoint(ServerSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.S3Endpoint))
            return null; // AWS default endpoint (https)
        if (!Uri.TryCreate(settings.S3Endpoint, UriKind.Absolute, out var uri))
            return $"S3 endpoint '{settings.S3Endpoint}' is not an absolute URL.";
        if (uri.Scheme == Uri.UriSchemeHttps)
            return null;
        if (uri.Scheme == Uri.UriSchemeHttp && settings.S3AllowInsecureEndpoint)
            return null;
        return $"S3 endpoint '{settings.S3Endpoint}' must use https (tick 'Allow insecure endpoint' only for local development).";
    }

    public async Task<bool> IsStorageConfiguredAsync()
    {
        return await _db.ServerSettings.AnyAsync();
    }

    public async Task<long> GetPackageCountAsync()
    {
        return await _db.Packages.LongCountAsync();
    }

    public IStorageProvider CreateStorageProvider(ServerSettings settings)
    {
        return settings.StorageProvider switch
        {
            StorageProviderType.S3 => new S3StorageProvider(
                settings.S3Endpoint,
                settings.S3Bucket,
                settings.S3AccessKey,
                settings.S3SecretKey,
                settings.S3Region),
            _ => new LocalStorageProvider(settings.LocalBasePath)
        };
    }

    public static S3ProviderInfo GetS3ProviderInfo(S3ProviderPreset preset)
    {
        return preset switch
        {
            S3ProviderPreset.AwsS3 => new S3ProviderInfo
            {
                Name = "Amazon S3",
                Endpoint = "https://s3.{region}.amazonaws.com",
                RequiresRegion = true,
                DefaultRegion = "us-east-1",
                Description = "Amazon Web Services S3"
            },
            S3ProviderPreset.MinIO => new S3ProviderInfo
            {
                Name = "MinIO",
                Endpoint = "",
                RequiresRegion = false,
                DefaultRegion = "us-east-1",
                Description = "Self-hosted S3-compatible storage"
            },
            S3ProviderPreset.CloudflareR2 => new S3ProviderInfo
            {
                Name = "Cloudflare R2",
                Endpoint = "https://{account_id}.r2.cloudflarestorage.com",
                RequiresRegion = false,
                DefaultRegion = "auto",
                Description = "Cloudflare R2 Storage"
            },
            S3ProviderPreset.BackblazeB2 => new S3ProviderInfo
            {
                Name = "Backblaze B2",
                Endpoint = "https://s3.{region}.backblazeb2.com",
                RequiresRegion = true,
                DefaultRegion = "us-west-004",
                Description = "Backblaze B2 Cloud Storage"
            },
            S3ProviderPreset.DigitalOceanSpaces => new S3ProviderInfo
            {
                Name = "DigitalOcean Spaces",
                Endpoint = "https://{region}.digitaloceanspaces.com",
                RequiresRegion = true,
                DefaultRegion = "nyc3",
                Description = "DigitalOcean Spaces Object Storage"
            },
            _ => new S3ProviderInfo
            {
                Name = "Custom S3",
                Endpoint = "",
                RequiresRegion = true,
                DefaultRegion = "us-east-1",
                Description = "Custom S3-compatible endpoint"
            }
        };
    }
}

public class S3ProviderInfo
{
    public string Name { get; set; } = "";
    public string Endpoint { get; set; } = "";
    public bool RequiresRegion { get; set; }
    public string DefaultRegion { get; set; } = "";
    public string Description { get; set; } = "";
}

/// <summary>The server's config directory (database, keys, setup token), for checks that must not overlap it.</summary>
public sealed record ServerConfigDirectory(string Path);
