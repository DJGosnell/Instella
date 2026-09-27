using Instella.Server.Data;
using Instella.Server.Data.Entities;
using Instella.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace Instella.Server.Tests.Services;

[TestFixture]
public sealed class PatchJobWorkerTests
{
    private DatabaseFixture _dbFixture = null!;

    [SetUp]
    public void SetUp()
    {
        _dbFixture = new DatabaseFixture();
    }

    [TearDown]
    public void TearDown()
    {
        _dbFixture.Dispose();
    }

    [Test]
    public async Task PendingJob_IsPickedUp()
    {
        var db = _dbFixture.Context;
        var pkg = await _dbFixture.SeedPackageAsync("com.test", "Test");
        var ver = await _dbFixture.SeedVersionAsync(pkg, "1.0.0");
        var build = await _dbFixture.SeedBuildAsync(ver);

        db.PendingPatchJobs.Add(new PendingPatchJob { ToBuildId = build.Id });
        await db.SaveChangesAsync();

        // A parameter: SQLite's translated 'now' has millisecond precision and can sort before
        // a just-stored 100ns timestamp.
        var now = DateTime.UtcNow;
        var pendingJobs = await db.PendingPatchJobs
            .Where(j => j.Status == PatchJobStatus.Pending && j.NextAttemptAt <= now)
            .ToListAsync();

        Assert.That(pendingJobs, Has.Count.EqualTo(1));
        Assert.That(pendingJobs[0].ToBuildId, Is.EqualTo(build.Id));
    }

    [Test]
    public async Task CompletedJob_IsNotPickedUp()
    {
        var db = _dbFixture.Context;
        var pkg = await _dbFixture.SeedPackageAsync("com.test2", "Test2");
        var ver = await _dbFixture.SeedVersionAsync(pkg, "1.0.0");
        var build = await _dbFixture.SeedBuildAsync(ver);

        db.PendingPatchJobs.Add(new PendingPatchJob
        {
            ToBuildId = build.Id,
            Status = PatchJobStatus.Completed
        });
        await db.SaveChangesAsync();

        var pendingJobs = await db.PendingPatchJobs
            .Where(j => j.Status == PatchJobStatus.Pending)
            .ToListAsync();

        Assert.That(pendingJobs, Is.Empty);
    }

    [Test]
    public async Task DeadJob_IsNotPickedUp()
    {
        var db = _dbFixture.Context;
        var pkg = await _dbFixture.SeedPackageAsync("com.test3", "Test3");
        var ver = await _dbFixture.SeedVersionAsync(pkg, "1.0.0");
        var build = await _dbFixture.SeedBuildAsync(ver);

        db.PendingPatchJobs.Add(new PendingPatchJob
        {
            ToBuildId = build.Id,
            Status = PatchJobStatus.Dead,
            Attempts = 3
        });
        await db.SaveChangesAsync();

        var pendingJobs = await db.PendingPatchJobs
            .Where(j => j.Status == PatchJobStatus.Pending || j.Status == PatchJobStatus.Failed)
            .Where(j => j.Attempts < 3)
            .ToListAsync();

        Assert.That(pendingJobs, Is.Empty);
    }

    [Test]
    public async Task FailedJob_WithRetriesLeft_IsPickedUp()
    {
        var db = _dbFixture.Context;
        var pkg = await _dbFixture.SeedPackageAsync("com.test4", "Test4");
        var ver = await _dbFixture.SeedVersionAsync(pkg, "1.0.0");
        var build = await _dbFixture.SeedBuildAsync(ver);

        db.PendingPatchJobs.Add(new PendingPatchJob
        {
            ToBuildId = build.Id,
            Status = PatchJobStatus.Failed,
            Attempts = 1,
            LastError = "Temporary failure",
            NextAttemptAt = DateTime.UtcNow.AddSeconds(-1) // Past due
        });
        await db.SaveChangesAsync();

        var pendingJobs = await db.PendingPatchJobs
            .Where(j => (j.Status == PatchJobStatus.Pending || j.Status == PatchJobStatus.Failed)
                        && j.NextAttemptAt <= DateTime.UtcNow
                        && j.Attempts < 3)
            .ToListAsync();

        Assert.That(pendingJobs, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task FailedJob_NotYetDue_IsNotPickedUp()
    {
        var db = _dbFixture.Context;
        var pkg = await _dbFixture.SeedPackageAsync("com.test5", "Test5");
        var ver = await _dbFixture.SeedVersionAsync(pkg, "1.0.0");
        var build = await _dbFixture.SeedBuildAsync(ver);

        db.PendingPatchJobs.Add(new PendingPatchJob
        {
            ToBuildId = build.Id,
            Status = PatchJobStatus.Failed,
            Attempts = 1,
            NextAttemptAt = DateTime.UtcNow.AddMinutes(10) // Future
        });
        await db.SaveChangesAsync();

        var pendingJobs = await db.PendingPatchJobs
            .Where(j => (j.Status == PatchJobStatus.Pending || j.Status == PatchJobStatus.Failed)
                        && j.NextAttemptAt <= DateTime.UtcNow
                        && j.Attempts < 3)
            .ToListAsync();

        Assert.That(pendingJobs, Is.Empty);
    }

    [Test]
    public async Task Job_WithFromBuildId_IsStored()
    {
        var db = _dbFixture.Context;
        var pkg = await _dbFixture.SeedPackageAsync("com.test6", "Test6");
        var ver1 = await _dbFixture.SeedVersionAsync(pkg, "1.0.0");
        var ver2 = await _dbFixture.SeedVersionAsync(pkg, "2.0.0");
        var build1 = await _dbFixture.SeedBuildAsync(ver1);
        var build2 = await _dbFixture.SeedBuildAsync(ver2);

        db.PendingPatchJobs.Add(new PendingPatchJob
        {
            FromBuildId = build1.Id,
            ToBuildId = build2.Id
        });
        await db.SaveChangesAsync();

        var job = await db.PendingPatchJobs.FirstAsync();
        Assert.That(job.FromBuildId, Is.EqualTo(build1.Id));
        Assert.That(job.ToBuildId, Is.EqualTo(build2.Id));
    }
}
