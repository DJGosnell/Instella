using Instella.Server.Data;
using Instella.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Instella.Server.Services;

/// <summary>
/// Publishes releases held by <see cref="ReleaseApproval.Delayed"/> once their
/// <see cref="VersionBuild.PublishAfter"/> has passed and nobody rejected them. The time is stored, so
/// a restart can make a release late but never early or skipped: the first pass runs at startup.
/// Each build is published by <see cref="ReleaseApprovalService.AutoPublishAsync"/>, a conditional
/// UPDATE, so a concurrent approve, reject or second server instance cannot publish it twice.
/// </summary>
public sealed class DelayedReleaseWorker(IServiceScopeFactory scopeFactory, ILogger<DelayedReleaseWorker> logger) : BackgroundService
{
    /// <summary>How often due releases are looked for.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private const int BatchSize = 50;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PublishDueAsync(DateTime.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Delayed release pass failed");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    /// <summary>One pass: publishes the builds due by <paramref name="now"/>. Returns how many it published. Internal for tests.</summary>
    internal async Task<int> PublishDueAsync(DateTime now, CancellationToken ct)
    {
        List<long> due;
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            due = await db.VersionBuilds
                .Where(b => b.State == BuildState.Pending && b.PublishAfter != null && b.PublishAfter <= now)
                .OrderBy(b => b.PublishAfter)
                .Select(b => b.Id)
                .Take(BatchSize)
                .ToListAsync(ct);
        }

        var published = 0;
        foreach (var id in due)
        {
            try
            {
                // A fresh scope per build: one failure leaves nothing half-tracked for the next.
                using var scope = scopeFactory.CreateScope();
                if (await scope.ServiceProvider.GetRequiredService<ReleaseApprovalService>().AutoPublishAsync(id, now, ct))
                    published++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Could not publish delayed build {BuildId}; retrying on the next pass", id);
            }
        }
        return published;
    }
}
