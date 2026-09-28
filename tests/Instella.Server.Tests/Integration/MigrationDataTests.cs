using Instella.Server.Data;
using Instella.Server.Data.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NUnit.Framework;

namespace Instella.Server.Tests.Integration;

/// <summary>Migrations keep the data of an existing server: rows written by an older schema mean the same afterwards.</summary>
[TestFixture]
public class MigrationDataTests
{
    [Test]
    public async Task ReleaseApproval_KeepsDraftsAsDrafts_AndPublishedBuildsPublished()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20260927011420_InitialCreate");

        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO Packages (Id, PackageId, DisplayName, Description, DownloadAccessMode, CreatedAt)
                VALUES (1, 'com.test.app', 'Test', '', 0, '2026-09-27 00:00:00');
            INSERT INTO PackageVersions (Id, PackageId, VersionString, VersionKey, Channel, Changelog, ReleasedAt, IsDeprecated)
                VALUES (1, 1, '1.0.0', 'k1', 'stable', '', '2026-09-27 00:00:00', 0);
            INSERT INTO VersionBuilds (Id, VersionId, OS, Architecture, TotalSize, ManifestHash, IsDraft, UploadedAt, DownloadCount, PatchDownloadCount)
                VALUES (1, 1, 0, 0, 1, 'h1', 0, '2026-09-27 00:00:00', 0, 0),
                       (2, 1, 1, 0, 1, 'h2', 1, '2026-09-27 00:00:00', 0, 0);
            """);

        await migrator.MigrateAsync();

        var states = await db.VersionBuilds.OrderBy(b => b.Id).Select(b => b.State).ToListAsync();
        Assert.That(states, Is.EqualTo(new[] { BuildState.Published, BuildState.Draft }));
        var package = await db.Packages.SingleAsync();
        Assert.That(package.ReleaseApproval, Is.EqualTo(ReleaseApproval.Automatic), "existing packages keep publishing at once");
        Assert.That(package.ReleaseDelayMinutes, Is.EqualTo(1440));
    }
}
