using Instella.Server.Data;
using Instella.Server.Data.Entities;
using Instella.Server.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Instella.E2E.Tests;

/// <summary>
/// The real server on Kestrel (127.0.0.1, a free port), with its own SQLite database, blob
/// storage and config directory, so separate processes (installer, app, updater, CLI) can
/// reach it over the network.
/// </summary>
internal sealed class E2EServer : WebApplicationFactory<Instella.Server.Api.PackagesController>
{
    private readonly string _root;

    public E2EServer(string root, int port)
    {
        _root = root;
        Port = port;
        UseKestrel(port);
    }

    public int Port { get; }

    public string BaseUrl => $"http://127.0.0.1:{Port}";

    public string StorageRoot => Path.Combine(_root, "packages");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(_root);
        builder.UseEnvironment("Development");
        builder.UseSetting("Instella:ConfigDir", Path.Combine(_root, "config"));
        builder.UseSetting("Database:ConnectionString", $"Data Source={Path.Combine(_root, "instella.db")};Pooling=False");
        builder.UseSetting("Storage:Local:BasePath", StorageRoot);
        builder.UseSetting("Server:TlsTerminatedByProxy", "true");
    }

    /// <summary>Creates the package and an admin-scope API key that can upload.</summary>
    public async Task<string> SeedPackageAndKeyAsync(string packageId)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Packages.Add(new Package
        {
            PackageId = packageId,
            DisplayName = packageId,
            DownloadAccessMode = DownloadAccessMode.Open,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var (_, key) = await scope.ServiceProvider.GetRequiredService<AuthService>()
            .CreateApiKeyAsync("e2e", ApiKeyScope.Admin, canUpload: true, canDownload: true, canManageVersions: true);
        return key;
    }

    /// <summary>Waits until the patch job producing <paramref name="version"/> has finished.</summary>
    public async Task WaitForPatchJobAsync(string packageId, string version, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            using (var scope = Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var jobs = await db.PendingPatchJobs
                    .Where(j => j.ToBuild!.Version.VersionString == version && j.ToBuild.Version.Package.PackageId == packageId)
                    .Select(j => j.Status)
                    .ToListAsync();
                if (jobs.Count > 0 && jobs.All(s => s is PatchJobStatus.Completed or PatchJobStatus.Dead))
                    return;
            }
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"patch job for {packageId} {version} did not finish");
            await Task.Delay(500);
        }
    }

    /// <summary>The on-disk blob of the stored content with <paramref name="sha256"/>.</summary>
    public async Task<string> BlobPathAsync(string sha256)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.StoredFiles.SingleAsync(f => f.ContentHash == sha256);
        return Path.Combine(StorageRoot, stored.StoragePath.Replace('/', Path.DirectorySeparatorChar));
    }
}
