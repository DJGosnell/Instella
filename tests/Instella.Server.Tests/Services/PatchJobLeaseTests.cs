using Microsoft.Extensions.Configuration;
using Instella.Server.Data.Entities;
using Instella.Server.Services;
using Instella.Server.Tests.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Instella.Server.Tests.Services;

/// <summary>
/// A job whose worker died (expired lease) is claimed again; a live lease is not;
/// a job whose worker died during the final attempt is marked Dead instead of staying InProgress forever.
/// </summary>
[TestFixture]
public sealed class PatchJobLeaseTests
{
    private ServerTestFixture _fixture = null!;

    [SetUp]
    public void SetUp() => _fixture = new ServerTestFixture();

    [TearDown]
    public void TearDown() => _fixture.Dispose();

    [Test]
    public async Task InProgressJob_WithAnExpiredLease_IsClaimedAgain()
    {
        var jobId = await SeedJobAsync(leaseExpiresAt: DateTime.UtcNow.AddMinutes(-1));

        await new PatchJobWorker(_fixture.ScopeFactory, NullLogger<PatchJobWorker>.Instance).ProcessPendingJobsAsync(CancellationToken.None);

        _fixture.Db.ChangeTracker.Clear();
        var job = await _fixture.Db.PendingPatchJobs.SingleAsync(j => j.Id == jobId);
        Assert.That(job.Attempts, Is.EqualTo(2), "the job was claimed a second time");
        Assert.That(job.Status, Is.Not.EqualTo(PatchJobStatus.InProgress));
        Assert.That(job.LeaseExpiresAt, Is.Null, "the new worker finished and released its lease");
    }

    [Test]
    public async Task InProgressJob_WithALiveLease_IsLeftToItsWorker()
    {
        var jobId = await SeedJobAsync(leaseExpiresAt: DateTime.UtcNow.AddMinutes(5));

        await new PatchJobWorker(_fixture.ScopeFactory, NullLogger<PatchJobWorker>.Instance).ProcessPendingJobsAsync(CancellationToken.None);

        _fixture.Db.ChangeTracker.Clear();
        var job = await _fixture.Db.PendingPatchJobs.SingleAsync(j => j.Id == jobId);
        Assert.That(job.Attempts, Is.EqualTo(1));
        Assert.That(job.Status, Is.EqualTo(PatchJobStatus.InProgress));
    }

    [Test]
    public async Task InProgressJob_WithAnExpiredLease_OnTheFinalAttempt_IsMarkedDead()
    {
        var jobId = await SeedJobAsync(leaseExpiresAt: DateTime.UtcNow.AddMinutes(-1), attempts: 3);

        await new PatchJobWorker(_fixture.ScopeFactory, NullLogger<PatchJobWorker>.Instance).ProcessPendingJobsAsync(CancellationToken.None);

        _fixture.Db.ChangeTracker.Clear();
        var job = await _fixture.Db.PendingPatchJobs.SingleAsync(j => j.Id == jobId);
        Assert.That(job.Status, Is.EqualTo(PatchJobStatus.Dead), "no attempts are left, so the job must not stay InProgress");
        Assert.That(job.Attempts, Is.EqualTo(3), "it was not claimed a fourth time");
        Assert.That(job.LeaseExpiresAt, Is.Null);
        Assert.That(job.LastError, Does.Contain("final attempt"));
    }

    [Test]
    public async Task InProgressJob_WithALiveLease_OnTheFinalAttempt_IsLeftToItsWorker()
    {
        var jobId = await SeedJobAsync(leaseExpiresAt: DateTime.UtcNow.AddMinutes(5), attempts: 3);

        await new PatchJobWorker(_fixture.ScopeFactory, NullLogger<PatchJobWorker>.Instance).ProcessPendingJobsAsync(CancellationToken.None);

        _fixture.Db.ChangeTracker.Clear();
        var job = await _fixture.Db.PendingPatchJobs.SingleAsync(j => j.Id == jobId);
        Assert.That(job.Status, Is.EqualTo(PatchJobStatus.InProgress));
        Assert.That(job.LastError, Is.Null);
    }

    // ---- real failures retry, outcomes complete ----------------------------------

    [Test]
    public async Task AStorageFailure_IsRetriedWithBackoff_ThenDead()
    {
        await _fixture.SeedPackageAsync();
        await PublishAsync("1.0.0", new() { ["app.bin"] = Content(64 * 1024, 1) });
        await PublishAsync("1.1.0", new() { ["app.bin"] = Content(64 * 1024, 2) });
        _fixture.Storage.Clear();   // the content a patch is made from is gone
        var worker = new PatchJobWorker(_fixture.ScopeFactory, NullLogger<PatchJobWorker>.Instance);

        await worker.ProcessPendingJobsAsync(CancellationToken.None);   // 1.0.0's job: no previous build
        await worker.ProcessPendingJobsAsync(CancellationToken.None);   // 1.1.0's job
        Assert.That((await JobFor("1.0.0")).Status, Is.EqualTo(PatchJobStatus.Completed), "an outcome, not a failure");
        var job = await JobFor("1.1.0");
        Assert.That(job.Status, Is.EqualTo(PatchJobStatus.Failed));
        Assert.That(job.NextAttemptAt, Is.GreaterThan(DateTime.UtcNow.AddSeconds(20)), "30 s back-off");
        Assert.That(job.LastError, Does.Contain("missing"));

        for (var attempt = 2; attempt <= 3; attempt++)
        {
            await _fixture.Db.PendingPatchJobs.Where(j => j.Id == job.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(j => j.NextAttemptAt, DateTime.UtcNow.AddMinutes(-1)));
            await worker.ProcessPendingJobsAsync(CancellationToken.None);
        }

        job = await JobFor("1.1.0");
        Assert.That(job.Status, Is.EqualTo(PatchJobStatus.Dead));
        Assert.That(job.Attempts, Is.EqualTo(3));
    }

    [Test]
    public async Task APatchNotWorthIt_CompletesTheJob_WithoutAPatch()
    {
        await _fixture.SeedPackageAsync();
        await PublishAsync("1.0.0", new() { ["app.bin"] = Content(4096, 1, seed: 1) });
        await PublishAsync("1.1.0", new() { ["app.bin"] = Content(4096, 1, seed: 2) });   // entirely different

        var worker = new PatchJobWorker(_fixture.ScopeFactory, NullLogger<PatchJobWorker>.Instance);
        await worker.ProcessPendingJobsAsync(CancellationToken.None);
        await worker.ProcessPendingJobsAsync(CancellationToken.None);

        Assert.That((await JobFor("1.1.0")).Status, Is.EqualTo(PatchJobStatus.Completed));
        Assert.That(await _fixture.Db.BuildPatches.CountAsync(), Is.Zero);
    }

    [Test]
    public async Task AFileOverTheCap_IsShippedWhole_NotDiffed()
    {
        await _fixture.SeedPackageAsync();
        await PublishAsync("1.0.0", new() { ["small.bin"] = Content(10 * 1024, 1), ["big.bin"] = Content(100 * 1024, 1) });
        var target = await PublishAsync("1.1.0", new() { ["small.bin"] = Content(10 * 1024, 2), ["big.bin"] = Content(100 * 1024, 2) });
        var diff = new DiffService(_fixture.Db, _fixture.Storage, Config(("Diff:MaxFileBytes", "51200"), ("Diff:MaxPatchRatio", "1.5")),
            NullLogger<DiffService>.Instance);

        var result = await diff.GeneratePatchAsync(target);

        Assert.That(result.Outcome, Is.EqualTo(PatchOutcome.Created));
        var manifest = await diff.GetPatchManifestAsync(result.Patch!.Id);
        Assert.That(manifest!.NewFiles.Select(f => f.RelativePath), Is.EqualTo(new[] { "big.bin" }));
        Assert.That(manifest.PatchedFiles.Select(f => f.RelativePath), Is.EqualTo(new[] { "small.bin" }));
    }

    [Test]
    public async Task CaseVariantPaths_ProduceAPatch()
    {
        // App.dll and app.dll are two files of a valid Linux build; a case-insensitive map threw.
        await _fixture.SeedPackageAsync();
        await PublishAsync("1.0.0", new() { ["App.dll"] = Content(64 * 1024, 1), ["app.dll"] = Content(64 * 1024, 3) });
        var target = await PublishAsync("1.1.0", new() { ["App.dll"] = Content(64 * 1024, 2), ["app.dll"] = Content(64 * 1024, 4) });

        var result = await _fixture.DiffService.GeneratePatchAsync(target);

        Assert.That(result.Outcome, Is.EqualTo(PatchOutcome.Created));
        var manifest = await _fixture.DiffService.GetPatchManifestAsync(result.Patch!.Id);
        Assert.That(manifest!.PatchedFiles.Select(f => f.RelativePath), Is.EquivalentTo(new[] { "App.dll", "app.dll" }));
    }

    [Test]
    public async Task CompletingAnUpload_EnqueuesItsPatchJob_InTheSameTransaction()
    {
        await _fixture.SeedPackageAsync();
        var build = await PublishAsync("1.0.0", new() { ["app.bin"] = Content(1024, 1) });

        Assert.That(await _fixture.Db.PendingPatchJobs.Select(j => j.ToBuildId).SingleAsync(), Is.EqualTo(build));
    }

    private async Task<long> PublishAsync(string version, Dictionary<string, byte[]> files)
    {
        var upload = _fixture.UploadService;
        var session = await upload.StartSessionAsync("com.test.app", version, "stable",
            Instella.Server.Models.TargetOS.Windows, Instella.Server.Models.Architecture.X64);
        foreach (var (path, bytes) in files)
            await upload.UploadFileAsync(session.Id, path, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)),
                new MemoryStream(bytes));
        var result = await upload.CompleteSessionAsync(session.Id, null);
        _fixture.Db.ChangeTracker.Clear();
        return result.BuildId;
    }

    private async Task<PendingPatchJob> JobFor(string version)
    {
        _fixture.Db.ChangeTracker.Clear();
        return await _fixture.Db.PendingPatchJobs.SingleAsync(j => j.ToBuild!.Version.VersionString == version);
    }

    /// <summary><paramref name="size"/> pseudo-random bytes with one byte flipped per <paramref name="variant"/>.</summary>
    private static byte[] Content(int size, int variant, int seed = 42)
    {
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        bytes[(variant * 997) % size] ^= 0xFF;
        return bytes;
    }

    private static Microsoft.Extensions.Configuration.IConfiguration Config(params (string Key, string Value)[] values) =>
        new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    private async Task<long> SeedJobAsync(DateTime leaseExpiresAt, int attempts = 1)
    {
        var package = await _fixture.SeedPackageAsync();
        var version = new PackageVersion { PackageId = package.Id, VersionString = "1.0.0", ReleasedAt = DateTime.UtcNow };
        _fixture.Db.PackageVersions.Add(version);
        await _fixture.Db.SaveChangesAsync();
        var build = new VersionBuild
        {
            VersionId = version.Id, OS = Instella.Server.Models.TargetOS.Windows, Architecture = Instella.Server.Models.Architecture.X64,
            ManifestHash = "m", UploadedAt = DateTime.UtcNow,
        };
        _fixture.Db.VersionBuilds.Add(build);
        var job = new PendingPatchJob
        {
            ToBuild = build, Status = PatchJobStatus.InProgress, Attempts = attempts,
            LeaseExpiresAt = leaseExpiresAt, NextAttemptAt = DateTime.UtcNow.AddMinutes(-10),
        };
        _fixture.Db.PendingPatchJobs.Add(job);
        await _fixture.Db.SaveChangesAsync();
        _fixture.Db.ChangeTracker.Clear();
        return job.Id;
    }
}
