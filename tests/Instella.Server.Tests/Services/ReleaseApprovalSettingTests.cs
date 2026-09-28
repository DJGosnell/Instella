using Instella.Core.Trust;
using Instella.Server.Data.Entities;
using Instella.Server.Services;
using Instella.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace Instella.Server.Tests.Services;

/// <summary>
/// Changing a package's release approval and its publisher keys: logged, never publishes anything,
/// tightening applies at once, and held releases always keep a key to verify against.
/// </summary>
[TestFixture]
public sealed class ReleaseApprovalSettingTests
{
    private ReleaseTestBed _bed = null!;

    private ReleaseApprovalService Approvals => _bed.Fixture.ReleaseApprovals;

    [TearDown]
    public void TearDown() => _bed.Dispose();

    [TestCase(ReleaseApproval.Delayed)]
    [TestCase(ReleaseApproval.Required)]
    public async Task HoldingReleases_NeedsARegisteredPublisherKey(ReleaseApproval approval)
    {
        _bed = await ReleaseTestBed.CreateAsync(registerKey: false);

        var ex = Assert.ThrowsAsync<InvalidOperationException>(() => Approvals.SetReleaseApprovalAsync(_bed.Package.Id, approval, 1440, "admin"));

        Assert.That(ex!.Message, Does.Contain("publisher key"));
    }

    [TestCase(9)]
    [TestCase(43201)]
    public async Task TheDelay_MustBeInRange(int minutes)
    {
        _bed = await ReleaseTestBed.CreateAsync();

        Assert.ThrowsAsync<InvalidOperationException>(() =>
            Approvals.SetReleaseApprovalAsync(_bed.Package.Id, ReleaseApproval.Delayed, minutes, "admin"));
    }

    [Test]
    public async Task EveryChange_IsLogged_WithTheAdmin()
    {
        _bed = await ReleaseTestBed.CreateAsync();

        await Approvals.SetReleaseApprovalAsync(_bed.Package.Id, ReleaseApproval.Delayed, 60, "alice");
        await Approvals.SetReleaseApprovalAsync(_bed.Package.Id, ReleaseApproval.Delayed, 60, "alice");   // no change, no entry

        var entries = await _bed.EventsAsync(SecurityEventType.ReleaseApprovalChanged);
        Assert.That(entries.Select(e => (e.Username, e.Details)), Is.EqualTo(new[] { ((string?)"alice", (string?)"Automatic → Delayed (1 h)") }));
    }

    [Test]
    public async Task SwitchingToRequired_CancelsRunningDelayTimers()
    {
        _bed = await ReleaseTestBed.CreateAsync(ReleaseApproval.Delayed);
        await _bed.UploadSignedAsync("1.0.0");
        await _bed.UploadSignedAsync("1.0.0", Models.TargetOS.Linux);

        await Approvals.SetReleaseApprovalAsync(_bed.Package.Id, ReleaseApproval.Required, 1440, "alice");

        Assert.That(await _bed.Fixture.Db.VersionBuilds.AllAsync(b => b.State == BuildState.Pending && b.PublishAfter == null), Is.True);
        Assert.That((await _bed.EventsAsync(SecurityEventType.ReleaseApprovalChanged)).Last().Details,
            Is.EqualTo("Delayed (24 h) → Required; 2 pending timer(s) cancelled"));
    }

    [Test]
    public async Task Loosening_PublishesNothing()
    {
        _bed = await ReleaseTestBed.CreateAsync(ReleaseApproval.Required);
        await _bed.UploadSignedAsync("1.0.0");

        await Approvals.SetReleaseApprovalAsync(_bed.Package.Id, ReleaseApproval.Automatic, 1440, "alice");

        Assert.That((await _bed.BuildAsync("1.0.0")).State, Is.EqualTo(BuildState.Pending), "only a decision publishes a held release");
        Assert.That(await _bed.LatestAsync(), Is.Null);
        Assert.That((await _bed.UploadSignedAsync("2.0.0")).State, Is.EqualTo(BuildState.Published), "later uploads follow the new setting");
    }

    [Test]
    public async Task ShorteningTheDelay_KeepsExistingTimers()
    {
        _bed = await ReleaseTestBed.CreateAsync(ReleaseApproval.Delayed);
        var before = (await _bed.UploadSignedAsync("1.0.0")).PublishAfter;

        await Approvals.SetReleaseApprovalAsync(_bed.Package.Id, ReleaseApproval.Delayed, 10, "alice");

        Assert.That((await _bed.BuildAsync("1.0.0")).PublishAfter, Is.EqualTo(before));
    }

    [Test]
    public async Task TheLastKey_CannotBeRemoved_WhileReleasesAreHeld()
    {
        _bed = await ReleaseTestBed.CreateAsync(ReleaseApproval.Required);
        var key = await _bed.Fixture.Db.PackagePublisherKeys.SingleAsync();

        var ex = Assert.ThrowsAsync<InvalidOperationException>(() => _bed.Fixture.PackageService.RemovePublisherKeyAsync(key.Id, "alice"));
        Assert.That(ex!.Message, Does.Contain("last publisher key"));

        await _bed.UploadSignedAsync("1.0.0");
        await Approvals.SetReleaseApprovalAsync(_bed.Package.Id, ReleaseApproval.Automatic, 1440, "alice");
        ex = Assert.ThrowsAsync<InvalidOperationException>(() => _bed.Fixture.PackageService.RemovePublisherKeyAsync(key.Id, "alice"));
        Assert.That(ex!.Message, Does.Contain("pending approval"));

        var build = await _bed.BuildAsync("1.0.0");
        await Approvals.RejectAsync(build.Id, null, null, ReleaseActor.Admin("alice"));
        Assert.That(await _bed.Fixture.PackageService.RemovePublisherKeyAsync(key.Id, "alice"), Is.True);
    }

    [Test]
    public async Task KeyChanges_AreLogged()
    {
        _bed = await ReleaseTestBed.CreateAsync();
        using var backup = ReleaseKeys.Generate();
        var added = await _bed.Fixture.PackageService.AddPublisherKeyAsync(_bed.Package.Id, ReleaseKeys.PublicKeyOf(backup).PublicKey,
            "backup", "alice");
        await _bed.Fixture.PackageService.RemovePublisherKeyAsync(added.Id, "bob");

        var addedEntry = (await _bed.EventsAsync(SecurityEventType.PublisherKeyAdded)).Last();
        var removedEntry = (await _bed.EventsAsync(SecurityEventType.PublisherKeyRemoved)).Single();
        Assert.That((addedEntry.Username, addedEntry.Details), Is.EqualTo(("alice", $"{added.KeyId} (backup)")));
        Assert.That((removedEntry.Username, removedEntry.PackageId), Is.EqualTo(("bob", ReleaseTestBed.PackageId)));
    }

    [Test]
    public async Task AKey_CannotBothUploadAndApprove()
    {
        _bed = await ReleaseTestBed.CreateAsync();

        Assert.ThrowsAsync<ArgumentException>(() => _bed.Fixture.AuthService.CreateApiKeyAsync("both", ApiKeyScope.Admin,
            canUpload: true, canApproveReleases: true));
    }
}
