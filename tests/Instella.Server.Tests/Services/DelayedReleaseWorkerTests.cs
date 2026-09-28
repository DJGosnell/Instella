using Instella.Core.Trust;
using Instella.Server.Data.Entities;
using Instella.Server.Services;
using Instella.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Instella.Server.Tests.Services;

/// <summary>
/// Release approval Delayed: the worker publishes a pending release once its stored time has passed
/// (also right after a restart), never before, never one that was decided, and never one whose key
/// was removed.
/// </summary>
[TestFixture]
public sealed class DelayedReleaseWorkerTests
{
    private ReleaseTestBed _bed = null!;

    [SetUp]
    public async Task SetUp() => _bed = await ReleaseTestBed.CreateAsync(ReleaseApproval.Delayed);

    [TearDown]
    public void TearDown() => _bed.Dispose();

    private DelayedReleaseWorker NewWorker() => new(_bed.Fixture.ScopeFactory, NullLogger<DelayedReleaseWorker>.Instance);

    [Test]
    public async Task BeforeTheDelayEnds_NothingIsPublished()
    {
        var build = await _bed.UploadSignedAsync("1.0.0");

        var published = await NewWorker().PublishDueAsync(build.PublishAfter!.Value.AddSeconds(-1), CancellationToken.None);

        Assert.That(published, Is.Zero);
        Assert.That((await _bed.BuildAsync("1.0.0")).State, Is.EqualTo(BuildState.Pending));
    }

    [Test]
    public async Task WhenTheDelayEnds_TheReleaseIsPublished_AndLogged()
    {
        var build = await _bed.UploadSignedAsync("1.0.0");

        var published = await NewWorker().PublishDueAsync(build.PublishAfter!.Value, CancellationToken.None);

        Assert.That(published, Is.EqualTo(1));
        var after = await _bed.BuildAsync("1.0.0");
        Assert.That((after.State, after.PublishAfter), Is.EqualTo((BuildState.Published, (DateTime?)null)));
        Assert.That(await _bed.LatestAsync(), Is.EqualTo("1.0.0"));
        var entry = (await _bed.EventsAsync(SecurityEventType.ReleaseAutoPublished)).Single();
        Assert.That((entry.IpAddress, entry.Username, entry.ApiKeyName), Is.EqualTo(("server", (string?)null, (string?)null)));
    }

    [Test]
    public async Task AfterARestart_OverdueReleasesArePublishedOnTheFirstPass()
    {
        var build = await _bed.UploadSignedAsync("1.0.0");
        // The server was down past the release's time: a new worker instance starts much later.
        var restartedAt = build.PublishAfter!.Value.AddDays(3);

        Assert.That(await NewWorker().PublishDueAsync(restartedAt, CancellationToken.None), Is.EqualTo(1));
        Assert.That(await NewWorker().PublishDueAsync(restartedAt, CancellationToken.None), Is.Zero, "published once");
        Assert.That(await _bed.EventsAsync(SecurityEventType.ReleaseAutoPublished), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task ARejectedOrApprovedRelease_IsLeftAlone()
    {
        var rejected = await _bed.UploadSignedAsync("1.0.0");
        var approved = await _bed.UploadSignedAsync("1.1.0");
        await _bed.Fixture.ReleaseApprovals.RejectAsync(rejected.Id, null, null, ReleaseActor.Admin("alice"));
        await _bed.Fixture.ReleaseApprovals.ApproveAsync(approved.Id, ReleaseTestBed.Hash(approved), ReleaseActor.Admin("alice"));

        Assert.That(await NewWorker().PublishDueAsync(DateTime.UtcNow.AddDays(30), CancellationToken.None), Is.Zero);
        Assert.That(await _bed.EventsAsync(SecurityEventType.ReleaseAutoPublished), Is.Empty);
    }

    [Test]
    public async Task SwitchingToRequired_StopsTheTimer()
    {
        await _bed.UploadSignedAsync("1.0.0");
        await _bed.Fixture.ReleaseApprovals.SetReleaseApprovalAsync(_bed.Package.Id, ReleaseApproval.Required, 1440, "alice");

        Assert.That(await NewWorker().PublishDueAsync(DateTime.UtcNow.AddDays(30), CancellationToken.None), Is.Zero);
        Assert.That((await _bed.BuildAsync("1.0.0")).State, Is.EqualTo(BuildState.Pending));
    }

    [Test]
    public async Task AReleaseSignedByARemovedKey_IsHeldForAPerson()
    {
        var build = await _bed.UploadSignedAsync("1.0.0");
        using var backup = ReleaseKeys.Generate();
        await _bed.Fixture.PackageService.AddPublisherKeyAsync(_bed.Package.Id, ReleaseKeys.PublicKeyOf(backup).PublicKey, "backup");
        var ci = await _bed.Fixture.Db.PackagePublisherKeys.SingleAsync(k => k.Label == "ci");
        await _bed.Fixture.PackageService.RemovePublisherKeyAsync(ci.Id, "alice");

        var published = await NewWorker().PublishDueAsync(build.PublishAfter!.Value.AddMinutes(1), CancellationToken.None);

        Assert.That(published, Is.Zero);
        var after = await _bed.BuildAsync("1.0.0");
        Assert.That((after.State, after.PublishAfter), Is.EqualTo((BuildState.Pending, (DateTime?)null)), "timer cancelled");
        Assert.That((await _bed.EventsAsync(SecurityEventType.ReleaseAutoPublishBlocked)).Single().Details, Does.Contain("no longer registered"));
        Assert.That(await NewWorker().PublishDueAsync(DateTime.UtcNow.AddDays(30), CancellationToken.None), Is.Zero);
        Assert.That(await _bed.EventsAsync(SecurityEventType.ReleaseAutoPublishBlocked), Has.Count.EqualTo(1), "logged once");
    }

    [Test]
    public async Task TwoWorkers_PublishARelease_Once()
    {
        var build = await _bed.UploadSignedAsync("1.0.0");
        var due = build.PublishAfter!.Value;

        // Both passes read the build as due; the second one's conditional UPDATE finds it published.
        var first = await _bed.Fixture.ReleaseApprovals.AutoPublishAsync(build.Id, due);
        using var scope = _bed.Fixture.ScopeFactory.CreateScope();
        var second = await ((ReleaseApprovalService)scope.ServiceProvider.GetService(typeof(ReleaseApprovalService))!).AutoPublishAsync(build.Id, due);

        Assert.That((first, second), Is.EqualTo((true, false)));
        Assert.That(await _bed.EventsAsync(SecurityEventType.ReleaseAutoPublished), Has.Count.EqualTo(1));
    }
}
