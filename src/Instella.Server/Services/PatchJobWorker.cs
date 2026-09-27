using Instella.Server.Data;
using Instella.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Instella.Server.Services;

/// <summary>
/// Background service that polls PendingPatchJobs and runs patch generation.
/// Replaces the fire-and-forget Task.Run pattern with durable, retryable jobs.
/// </summary>
public sealed class PatchJobWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<PatchJobWorker> logger) : BackgroundService
{
    private const int MaxAttempts = 3;
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan LeaseRenewal = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly int[] BackoffSeconds = [30, 120, 600];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("PatchJobWorker started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPendingJobsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in PatchJobWorker poll loop");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }

        logger.LogInformation("PatchJobWorker stopped");
    }

    /// <summary>Claims and runs at most one due job. Internal for tests.</summary>
    internal async Task ProcessPendingJobsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var diffService = scope.ServiceProvider.GetRequiredService<DiffService>();

        var now = DateTime.UtcNow;

        // A worker that died during the last allowed attempt leaves its job InProgress with an
        // expired lease and no attempts left. The claim below never selects it, so bury it here;
        // otherwise it would stay InProgress forever. One conditional UPDATE, so a racing worker
        // cannot both bury and claim the same job.
        var buried = await db.PendingPatchJobs
            .Where(j => j.Status == PatchJobStatus.InProgress && j.LeaseExpiresAt < now && j.Attempts >= MaxAttempts)
            .ExecuteUpdateAsync(u => u
                .SetProperty(j => j.Status, PatchJobStatus.Dead)
                .SetProperty(j => j.LeaseExpiresAt, (DateTime?)null)
                .SetProperty(j => j.LastError, $"worker stopped during the final attempt ({MaxAttempts}/{MaxAttempts}); lease expired")
                .SetProperty(j => j.RowVersion, j => j.RowVersion + 1), ct);
        if (buried > 0)
            logger.LogWarning("Marked {Count} patch job(s) dead: their worker stopped during the final attempt", buried);

        var job = await db.PendingPatchJobs
            .Where(j => ((j.Status == PatchJobStatus.Pending || j.Status == PatchJobStatus.Failed) && j.NextAttemptAt <= now
                         || j.Status == PatchJobStatus.InProgress && j.LeaseExpiresAt < now)
                        && j.Attempts < MaxAttempts)
            .OrderBy(j => j.NextAttemptAt)
            .AsNoTracking()
            .FirstOrDefaultAsync(ct);

        if (job is null)
            return;

        // Compare-and-set on RowVersion: exactly one worker wins the claim.
        var claimed = await db.PendingPatchJobs
            .Where(j => j.Id == job.Id && j.RowVersion == job.RowVersion)
            .ExecuteUpdateAsync(u => u
                .SetProperty(j => j.Status, PatchJobStatus.InProgress)
                .SetProperty(j => j.Attempts, j => j.Attempts + 1)
                .SetProperty(j => j.LeaseExpiresAt, now + LeaseDuration)
                .SetProperty(j => j.RowVersion, j => j.RowVersion + 1), ct);
        if (claimed == 0)
        {
            logger.LogDebug("Patch job {JobId} already claimed by another worker", job.Id);
            return;
        }
        job = await db.PendingPatchJobs.FirstAsync(j => j.Id == job.Id, ct);

        // Renew the lease while the job runs, so a long diff is not mistaken for a dead worker.
        using var renewCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var renewal = RenewLeaseAsync(job.Id, renewCts.Token);

        try
        {
            logger.LogInformation("Processing patch job {JobId} for build {BuildId} (attempt {Attempt})",
                job.Id, job.ToBuildId, job.Attempts);

            // Every outcome completes the job; a real failure throws and is retried below.
            var result = job.FromBuildId.HasValue
                ? await diffService.GeneratePatchAsync(job.FromBuildId.Value, job.ToBuildId, ct)
                : await diffService.GeneratePatchAsync(job.ToBuildId, ct);
            logger.LogInformation("Patch job {JobId}: {Outcome}", job.Id, result.Outcome);

            await StopRenewalAsync(renewCts, renewal);
            job.Status = PatchJobStatus.Completed;
            job.LastError = null;
            job.LeaseExpiresAt = null;
            await db.SaveChangesAsync(ct);

            logger.LogInformation("Patch job {JobId} completed successfully", job.Id);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Patch job {JobId} failed (attempt {Attempt}/{Max})",
                job.Id, job.Attempts, MaxAttempts);
            await StopRenewalAsync(renewCts, renewal);
            job.LeaseExpiresAt = null;

            if (job.Attempts >= MaxAttempts)
            {
                job.Status = PatchJobStatus.Dead;
                job.LastError = ex.Message;
            }
            else
            {
                job.Status = PatchJobStatus.Failed;
                job.LastError = ex.Message;
                var backoffIndex = Math.Min(job.Attempts - 1, BackoffSeconds.Length - 1);
                job.NextAttemptAt = DateTime.UtcNow.AddSeconds(BackoffSeconds[backoffIndex]);
            }

            await db.SaveChangesAsync(ct);
        }
    }

    private async Task RenewLeaseAsync(long jobId, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await Task.Delay(LeaseRenewal, ct);
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var expires = DateTime.UtcNow + LeaseDuration;   // a parameter, not SQL 'now' (precision differs)
                await db.PendingPatchJobs.Where(j => j.Id == jobId && j.Status == PatchJobStatus.InProgress)
                    .ExecuteUpdateAsync(u => u.SetProperty(j => j.LeaseExpiresAt, expires), ct);
            }
        }
        catch (OperationCanceledException)
        {
            // The job finished.
        }
    }

    private static async Task StopRenewalAsync(CancellationTokenSource cts, Task renewal)
    {
        await cts.CancelAsync();
        await renewal;
    }
}
