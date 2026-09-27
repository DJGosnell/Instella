using Instella.Server.Data.Entities;
using Instella.Server.Services;
using Instella.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Instella.Server.Tests.Services;

/// <summary>Security-event throttling, log retention and the per-address login backoff.</summary>
[TestFixture]
public sealed class AbuseLimitsTests
{
    private DatabaseFixture _db = null!;

    [SetUp]
    public void SetUp() => _db = new DatabaseFixture();

    [TearDown]
    public void TearDown() => _db.Dispose();

    [Test]
    public async Task RepeatedEvents_AreWrittenOnce_ThenSummarised()
    {
        var now = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        var throttle = new SecurityEventThrottle { UtcNow = () => now };
        var log = new SecurityLogService(_db.Context, throttle);

        for (var i = 0; i < 50; i++)
            await log.LogEventAsync(SecurityEventType.DownloadDeniedNoKey, "198.51.100.7", packageId: "com.test");
        await log.LogEventAsync(SecurityEventType.DownloadDeniedNoKey, "203.0.113.9", packageId: "com.test");   // another address
        now = now.AddSeconds(61);
        await log.LogEventAsync(SecurityEventType.DownloadDeniedNoKey, "198.51.100.7", packageId: "com.test", details: "later");

        var rows = await _db.Context.SecurityEvents.OrderBy(e => e.Id).ToListAsync();
        Assert.That(rows.Select(r => r.IpAddress), Is.EqualTo(new[] { "198.51.100.7", "203.0.113.9", "198.51.100.7" }));
        Assert.That(rows[2].Details, Is.EqualTo("later (+49 similar)"));
    }

    [Test]
    public async Task Retention_DeletesOnlyOldRows()
    {
        var now = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        _db.Context.SecurityEvents.AddRange(
            new SecurityEvent { EventType = SecurityEventType.LoginFailed, IpAddress = "a", Timestamp = now.AddDays(-91) },
            new SecurityEvent { EventType = SecurityEventType.LoginFailed, IpAddress = "b", Timestamp = now.AddDays(-89) });
        var package = await _db.SeedPackageAsync();
        var version = await _db.SeedVersionAsync(package);
        var build = await _db.SeedBuildAsync(version);
        _db.Context.DownloadLogs.AddRange(
            new DownloadLog { BuildId = build.Id, Timestamp = now.AddDays(-366) },
            new DownloadLog { BuildId = build.Id, Timestamp = now.AddDays(-10) });
        await _db.Context.SaveChangesAsync();

        var removed = await OrphanSweeper.ApplyRetentionAsync(_db.Context, now, securityEventDays: 90, downloadLogDays: 365,
            NullLogger.Instance, CancellationToken.None);

        Assert.That(removed, Is.EqualTo(2));
        Assert.That(await _db.Context.SecurityEvents.Select(e => e.IpAddress).SingleAsync(), Is.EqualTo("b"));
        Assert.That(await _db.Context.DownloadLogs.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public void TheAddressKey_BlocksOnlyAfterTwentyFailures()
    {
        var now = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        var limits = new RateLimitService(null!) { UtcNow = () => now };
        const string key = "login-ip:198.51.100.7";

        for (var i = 1; i < RateLimitConfig.LoginFailuresPerIp; i++)
        {
            limits.RecordFailure(key, freeFailures: RateLimitConfig.LoginFailuresPerIp - 1);
            Assert.That(limits.CheckRequest(key).Allowed, Is.True, $"failure {i}");
        }
        limits.RecordFailure(key, freeFailures: RateLimitConfig.LoginFailuresPerIp - 1);

        Assert.That(limits.CheckRequest(key), Is.EqualTo((false, RateLimitConfig.InitialDelaySeconds)));
    }

    [Test]
    public void TheKeyCap_EvictsTheOldestEntry()
    {
        var now = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        var limits = new RateLimitService(null!) { UtcNow = () => now };
        limits.RecordFailure("oldest");
        now = now.AddSeconds(1);
        for (var i = 1; i < RateLimitConfig.MaxKeys; i++) limits.RecordFailure("k" + i);

        limits.RecordFailure("newest");

        var keys = limits.GetTemporaryBlocks().Select(b => b.Key).ToHashSet();
        Assert.That(keys, Has.Count.EqualTo(RateLimitConfig.MaxKeys));
        Assert.That(keys, Does.Not.Contain("oldest").And.Contain("newest"));
    }
}
