using Instella.Server.Data;
using Instella.Server.Data.Entities;
using Instella.Server.Services;
using Microsoft.EntityFrameworkCore;

namespace Instella.Server.Storage;

/// <summary>The storage provider built from the current settings.</summary>
public interface IStorageProviderAccessor
{
    /// <summary>The provider for the saved <see cref="ServerSettings"/>, built on first use.</summary>
    IStorageProvider Current { get; }

    /// <summary>Drops the cached provider; the next use rebuilds it from the saved settings.</summary>
    void Invalidate();
}

/// <summary>
/// Builds the storage provider from <see cref="ServerSettings"/> on first use and rebuilds it
/// after <see cref="StorageSettingsService.SaveSettingsAsync"/> invalidates it, so a settings
/// change needs no restart. Before setup has saved settings, the configured local path is used.
/// </summary>
public sealed class StorageProviderAccessor(IServiceScopeFactory scopeFactory, IConfiguration configuration) : IStorageProviderAccessor
{
    private readonly object _gate = new();
    private IStorageProvider? _current;

    public IStorageProvider Current
    {
        get
        {
            lock (_gate)
                return _current ??= Build();
        }
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            (_current as IDisposable)?.Dispose();
            _current = null;
        }
    }

    private IStorageProvider Build()
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var secrets = scope.ServiceProvider.GetRequiredService<SecretProtector>();
        var settings = db.ServerSettings.AsNoTracking().FirstOrDefault();
        if (settings is null)
            return new LocalStorageProvider(configuration.GetValue<string>("Storage:Local:BasePath") ?? "./packages");

        return settings.StorageProvider switch
        {
            StorageProviderType.S3 => new S3StorageProvider(
                settings.S3Endpoint, settings.S3Bucket, settings.S3AccessKey,
                secrets.Unprotect(settings.S3SecretKey), settings.S3Region),
            _ => new LocalStorageProvider(settings.LocalBasePath),
        };
    }
}
