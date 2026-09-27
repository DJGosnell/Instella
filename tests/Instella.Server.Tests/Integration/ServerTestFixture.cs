using Instella.Server.Models;
using Instella.Server.Data;
using Instella.Server.Data.Entities;
using Instella.Server.Services;
using Instella.Server.Storage;
using Instella.Server.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Instella.Server.Tests.Integration;

/// <summary>
/// Test fixture for server integration tests.
/// Provides configured services with in-memory database and test storage.
/// </summary>
public class ServerTestFixture : IDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly IServiceScope _scope;
    private readonly SqliteConnection _connection;

    public AppDbContext Db { get; }
    public TestStorageProvider Storage { get; }
    public UploadService UploadService { get; }
    public ContentStorageService ContentStorage { get; }
    public DiffService DiffService { get; }
    public AuthService AuthService { get; }
    public PackageService PackageService { get; }

    /// <summary>Scopes over the same database, for services that create their own (background workers).</summary>
    public IServiceScopeFactory ScopeFactory => _serviceProvider.GetRequiredService<IServiceScopeFactory>();

    public ServerTestFixture()
    {
        Storage = new TestStorageProvider();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Diff:Enabled"] = "true",
                ["Diff:MaxPatchRatio"] = "0.9"
            })
            .Build();

        var services = new ServiceCollection();

        // Real SQLite, kept alive by one open in-memory connection.
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection));

        // Add storage
        services.AddSingleton<IStorageProvider>(Storage);

        // Add configuration
        services.AddSingleton<IConfiguration>(configuration);

        // Add logging
        services.AddLogging();
        services.AddScoped<DiffService>();

        _serviceProvider = services.BuildServiceProvider();
        _scope = _serviceProvider.CreateScope();

        Db = _scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Db.Database.Migrate();

        // Create services manually with null loggers for testing
        ContentStorage = new ContentStorageService(
            Db,
            Storage,
            NullLogger<ContentStorageService>.Instance);

        UploadService = new UploadService(
            Db,
            ContentStorage,
            NullLogger<UploadService>.Instance);

        DiffService = new DiffService(
            Db,
            Storage,
            configuration,
            NullLogger<DiffService>.Instance);

        AuthService = new AuthService(Db);
        PackageService = new PackageService(Db, ContentStorage);
    }

    public async Task<Package> SeedPackageAsync(
        string packageId = "com.test.app",
        string displayName = "Test App")
    {
        var package = new Package
        {
            PackageId = packageId,
            DisplayName = displayName,
            DownloadAccessMode = DownloadAccessMode.Open,
            CreatedAt = DateTime.UtcNow
        };

        Db.Packages.Add(package);
        await Db.SaveChangesAsync();
        return package;
    }

    public async Task<(PackageVersion version, VersionBuild build)> SeedVersionWithBuildAsync(
        Package package,
        string versionString = "1.0.0",
        TargetOS os = TargetOS.Windows,
        Architecture arch = Architecture.X64)
    {
        var version = new PackageVersion
        {
            PackageId = package.Id,
            VersionString = versionString,
            Channel = "stable",
            Changelog = "",
            ReleasedAt = DateTime.UtcNow
        };

        Db.PackageVersions.Add(version);
        await Db.SaveChangesAsync();

        var build = new VersionBuild
        {
            VersionId = version.Id,
            OS = os,
            Architecture = arch,
            TotalSize = 1000,
            ManifestHash = Guid.NewGuid().ToString("N"),
            UploadedAt = DateTime.UtcNow
        };

        Db.VersionBuilds.Add(build);
        await Db.SaveChangesAsync();

        return (version, build);
    }

    public void Dispose()
    {
        _scope.Dispose();
        _serviceProvider.Dispose();
        _connection.Dispose();
    }
}
