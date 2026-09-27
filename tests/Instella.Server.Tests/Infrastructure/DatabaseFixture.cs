using Instella.Server.Models;
using Instella.Server.Data;
using Instella.Server.Data.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace Instella.Server.Tests.Infrastructure;

/// <summary>
/// Test database: one open in-memory SQLite connection with the real migrations applied, so
/// foreign keys, transactions and bulk SQL behave exactly as in production.
/// </summary>
public class DatabaseFixture : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly AppDbContext _context;

    public DatabaseFixture()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new AppDbContext(_options);
        _context.Database.Migrate();
    }

    public AppDbContext Context => _context;

    public AppDbContext CreateNewContext()
    {
        return new AppDbContext(_options);
    }

    public async Task<Package> SeedPackageAsync(
        string packageId = "com.test.app",
        string displayName = "Test App",
        string? description = null)
    {
        var package = new Package
        {
            PackageId = packageId,
            DisplayName = displayName,
            Description = description ?? string.Empty,
            DownloadAccessMode = DownloadAccessMode.Open,
            CreatedAt = DateTime.UtcNow
        };

        _context.Packages.Add(package);
        await _context.SaveChangesAsync();
        return package;
    }

    public async Task<PackageVersion> SeedVersionAsync(
        Package package,
        string versionString = "1.0.0",
        string channel = "stable",
        string? changelog = null)
    {
        var version = new PackageVersion
        {
            PackageId = package.Id,
            VersionString = versionString,
            Channel = channel,
            Changelog = changelog ?? "",
            ReleasedAt = DateTime.UtcNow
        };

        _context.PackageVersions.Add(version);
        await _context.SaveChangesAsync();
        return version;
    }

    public async Task<VersionBuild> SeedBuildAsync(
        PackageVersion version,
        TargetOS os = TargetOS.Windows,
        Architecture arch = Architecture.X64,
        long totalSize = 1000)
    {
        var build = new VersionBuild
        {
            VersionId = version.Id,
            OS = os,
            Architecture = arch,
            TotalSize = totalSize,
            ManifestHash = $"manifest_{Guid.NewGuid():N}",
            UploadedAt = DateTime.UtcNow
        };

        _context.VersionBuilds.Add(build);
        await _context.SaveChangesAsync();
        return build;
    }

    public async Task<StoredFile> SeedStoredFileAsync(
        string contentHash,
        long size = 1000,
        int referenceCount = 1)
    {
        var storedFile = new StoredFile
        {
            ContentHash = contentHash,
            Size = size,
            StoragePath = $"{contentHash[..2]}/{contentHash[2..4]}/{contentHash}",
            ReferenceCount = referenceCount,
            FirstUploadedAt = DateTime.UtcNow
        };

        _context.StoredFiles.Add(storedFile);
        await _context.SaveChangesAsync();
        return storedFile;
    }

    public async Task<BuildFile> SeedBuildFileAsync(
        VersionBuild build,
        StoredFile storedFile,
        string relativePath = "test.dll")
    {
        var buildFile = new BuildFile
        {
            BuildId = build.Id,
            RelativePath = relativePath,
            ContentHash = storedFile.ContentHash,
            Size = storedFile.Size
        };

        _context.BuildFiles.Add(buildFile);
        await _context.SaveChangesAsync();
        return buildFile;
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }
}
