using Instella.Core.Trust;
using Instella.Server.Data.Entities;
using Instella.Server.Models;
using Instella.Server.Services;
using Instella.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace Instella.Server.Tests.Services;

/// <summary>
/// Release approval: which state an upload gets, and the approve and reject rules. Pending builds are
/// invisible to clients until a decision publishes them; nothing but a decision or the delay publishes.
/// </summary>
[TestFixture]
public sealed class ReleaseApprovalServiceTests
{
    private ReleaseTestBed _bed = null!;

    private ReleaseApprovalService Approvals => _bed.Fixture.ReleaseApprovals;

    [TearDown]
    public void TearDown() => _bed.Dispose();

    [TestCase(ReleaseApproval.Automatic, false, BuildState.Published, false)]
    [TestCase(ReleaseApproval.Delayed, false, BuildState.Pending, true)]
    [TestCase(ReleaseApproval.Required, false, BuildState.Pending, false)]
    [TestCase(ReleaseApproval.Required, true, BuildState.Draft, false)]
    [TestCase(ReleaseApproval.Delayed, true, BuildState.Draft, false)]
    public void InitialState_FollowsTheReleaseApproval(ReleaseApproval approval, bool draft, BuildState expected, bool timer)
    {
        _bed = new ReleaseTestBed();
        var now = new DateTime(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc);
        var package = new Package { PackageId = "p", DisplayName = "p", ReleaseApproval = approval, ReleaseDelayMinutes = 60 };

        var (state, publishAfter) = ReleaseApprovalService.InitialState(package, draft, now);

        Assert.That(state, Is.EqualTo(expected));
        Assert.That(publishAfter, Is.EqualTo(timer ? now.AddHours(1) : (DateTime?)null));
    }

    [Test]
    public async Task Automatic_PublishesAtOnce()
    {
        _bed = await ReleaseTestBed.CreateAsync();
        var build = await _bed.UploadSignedAsync("1.0.0");

        Assert.That(build.State, Is.EqualTo(BuildState.Published));
        Assert.That(await _bed.LatestAsync(), Is.EqualTo("1.0.0"));
    }

    [Test]
    public async Task Required_HoldsTheUpload_InvisibleToClients_UntilApproved()
    {
        _bed = await ReleaseTestBed.CreateAsync(ReleaseApproval.Required);
        await _bed.UploadSignedAsync("1.0.0");
        _bed.Fixture.Db.ChangeTracker.Clear();
        await _bed.Fixture.ReleaseApprovals.ApproveAsync((await _bed.BuildAsync("1.0.0")).Id,
            ReleaseTestBed.Hash(await _bed.BuildAsync("1.0.0")), ReleaseActor.Admin("admin"));

        var build = await _bed.UploadSignedAsync("2.0.0");
        Assert.That(build.State, Is.EqualTo(BuildState.Pending));
        Assert.That(build.PublishAfter, Is.Null, "Required has no timer");
        Assert.That(build.UploadedByApiKeyId, Is.EqualTo(_bed.Uploader.Id));
        Assert.That(build.UploadedByKeyName, Is.EqualTo("ci-upload"));

        Assert.That(await _bed.LatestAsync(), Is.EqualTo("1.0.0"), "latest ignores the pending release");
        Assert.That(await _bed.Fixture.PackageService.GetBuildAsync(ReleaseTestBed.PackageId, "2.0.0", TargetOS.Windows, Architecture.X64), Is.Null);
        Assert.That(await _bed.Fixture.PackageService.GetBuildAsync(ReleaseTestBed.PackageId, "2.0.0", TargetOS.Windows, Architecture.X64,
            includeUnpublished: true), Is.Not.Null);
        Assert.That(await Approvals.CountPendingAsync(_bed.Package.Id), Is.EqualTo(1));

        var decision = await Approvals.ApproveAsync(build.Id, ReleaseTestBed.Hash(build), ReleaseActor.Admin("alice"));

        Assert.That(decision.Status, Is.EqualTo(ReleaseDecisionStatus.Done), decision.Message);
        Assert.That((await _bed.BuildAsync("2.0.0")).State, Is.EqualTo(BuildState.Published));
        Assert.That(await _bed.LatestAsync(), Is.EqualTo("2.0.0"));
        var approved = (await _bed.EventsAsync(SecurityEventType.ReleaseApproved)).Last();
        Assert.That(approved.Username, Is.EqualTo("alice"));
        Assert.That(approved.Details, Does.Contain("2.0.0 windows/x64"));
        Assert.That(await _bed.EventsAsync(SecurityEventType.ReleasePending), Is.Empty,
            "the upload's pending entry is written by the API controller, not the service");
    }

    [Test]
    public async Task Approve_SetsTheReleaseDate_WhenThePendingBuildGoesLive()
    {
        _bed = await ReleaseTestBed.CreateAsync(ReleaseApproval.Required);
        var build = await _bed.UploadSignedAsync("1.0.0");
        await _bed.Fixture.Db.PackageVersions.ExecuteUpdateAsync(u => u.SetProperty(v => v.ReleasedAt, new DateTime(2020, 1, 1)));

        await Approvals.ApproveAsync(build.Id, ReleaseTestBed.Hash(build), ReleaseActor.Admin("alice"));

        Assert.That((await _bed.BuildAsync("1.0.0")).Version.ReleasedAt, Is.GreaterThan(DateTime.UtcNow.AddMinutes(-1)));
    }

    [Test]
    public async Task Approve_RefusesAManifestThatWasNotReviewed()
    {
        _bed = await ReleaseTestBed.CreateAsync(ReleaseApproval.Required);
        var build = await _bed.UploadSignedAsync("1.0.0");

        var decision = await Approvals.ApproveAsync(build.Id, new string('0', 64), ReleaseActor.Admin("alice"));

        Assert.That(decision.Status, Is.EqualTo(ReleaseDecisionStatus.ManifestChanged));
        Assert.That((await _bed.BuildAsync("1.0.0")).State, Is.EqualTo(BuildState.Pending));
    }

    [Test]
    public async Task Approve_ByTheUploadingKey_IsRefused()
    {
        _bed = await ReleaseTestBed.CreateAsync(ReleaseApproval.Required);
        var build = await _bed.UploadSignedAsync("1.0.0");

        var decision = await Approvals.ApproveAsync(build.Id, ReleaseTestBed.Hash(build), ReleaseActor.Key(_bed.Uploader, "10.0.0.1"));

        Assert.That(decision.Status, Is.EqualTo(ReleaseDecisionStatus.SelfApproval));
        Assert.That((await _bed.BuildAsync("1.0.0")).State, Is.EqualTo(BuildState.Pending));
    }

    [Test]
    public async Task Approve_ByAnotherKey_IsRecordedWithTheKeyName()
    {
        _bed = await ReleaseTestBed.CreateAsync(ReleaseApproval.Required);
        var build = await _bed.UploadSignedAsync("1.0.0");
        var (approver, _) = await _bed.Fixture.AuthService.CreateApiKeyAsync("laptop", ApiKeyScope.Package, canUpload: false,
            packageId: _bed.Package.Id, canApproveReleases: true);

        var decision = await Approvals.ApproveAsync(build.Id, ReleaseTestBed.Hash(build), ReleaseActor.Key(approver, "10.0.0.2"));

        Assert.That(decision.Status, Is.EqualTo(ReleaseDecisionStatus.Done), decision.Message);
        var entry = (await _bed.EventsAsync(SecurityEventType.ReleaseApproved)).Single();
        Assert.That((entry.ApiKeyName, entry.IpAddress, entry.Username), Is.EqualTo(("laptop", "10.0.0.2", (string?)null)));
    }

    [Test]
    public async Task Approve_ABuildSignedByARemovedKey_IsRefused()
    {
        _bed = await ReleaseTestBed.CreateAsync(ReleaseApproval.Required);
        var build = await _bed.UploadSignedAsync("1.0.0");
        using var other = ReleaseKeys.Generate();
        await _bed.Fixture.PackageService.AddPublisherKeyAsync(_bed.Package.Id, ReleaseKeys.PublicKeyOf(other).PublicKey, "backup");
        var ci = await _bed.Fixture.Db.PackagePublisherKeys.SingleAsync(k => k.Label == "ci");
        await _bed.Fixture.PackageService.RemovePublisherKeyAsync(ci.Id, "admin");

        var decision = await Approvals.ApproveAsync(build.Id, ReleaseTestBed.Hash(build), ReleaseActor.Admin("alice"));

        Assert.That(decision.Status, Is.EqualTo(ReleaseDecisionStatus.SignatureRejected));
        Assert.That(decision.Message, Does.Contain("no longer registered"));
    }

    [Test]
    public async Task Approve_ADraft_IsRefused()
    {
        _bed = await ReleaseTestBed.CreateAsync(ReleaseApproval.Required);
        var draft = await _bed.UploadDraftAsync("1.0.0");

        var decision = await Approvals.ApproveAsync(draft.Id, ReleaseTestBed.Hash(draft), ReleaseActor.Admin("alice"));

        Assert.That(decision.Status, Is.EqualTo(ReleaseDecisionStatus.NotAwaiting));
    }

    [Test]
    public async Task Reject_DeletesThePendingBuild_AndItsEmptyVersion_AndTheNumberCanBeUploadedAgain()
    {
        _bed = await ReleaseTestBed.CreateAsync(ReleaseApproval.Required);
        var build = await _bed.UploadSignedAsync("1.0.0");

        var decision = await Approvals.RejectAsync(build.Id, ReleaseTestBed.Hash(build), "not ours", ReleaseActor.Admin("alice"));

        Assert.That(decision.Status, Is.EqualTo(ReleaseDecisionStatus.Done), decision.Message);
        _bed.Fixture.Db.ChangeTracker.Clear();
        Assert.That(await _bed.Fixture.Db.VersionBuilds.CountAsync(), Is.Zero);
        Assert.That(await _bed.Fixture.Db.PackageVersions.CountAsync(), Is.Zero, "the version had no other build");
        Assert.That(await _bed.Fixture.Db.StoredFiles.SumAsync(f => f.ReferenceCount), Is.Zero, "references released");
        var entry = (await _bed.EventsAsync(SecurityEventType.ReleaseRejected)).Single();
        Assert.That(entry.Details, Does.Contain("reason: not ours").And.Contain("(pending)"));

        var again = await _bed.UploadSignedAsync("1.0.0");
        Assert.That(again.State, Is.EqualTo(BuildState.Pending), "a re-upload needs approval again");
    }

    [Test]
    public async Task Reject_KeepsTheVersion_WhenAnotherPlatformIsPublished()
    {
        _bed = await ReleaseTestBed.CreateAsync();
        await _bed.UploadSignedAsync("1.0.0", TargetOS.Linux);
        await _bed.Fixture.ReleaseApprovals.SetReleaseApprovalAsync(_bed.Package.Id, ReleaseApproval.Required, 1440, "admin");
        var build = await _bed.UploadSignedAsync("1.0.0");

        await Approvals.RejectAsync(build.Id, null, null, ReleaseActor.Admin("alice"));

        Assert.That(await _bed.Fixture.Db.PackageVersions.CountAsync(), Is.EqualTo(1));
        Assert.That(await _bed.LatestAsync(TargetOS.Linux), Is.EqualTo("1.0.0"));
    }

    [Test]
    public async Task Reject_ADraft_DeletesIt()
    {
        _bed = await ReleaseTestBed.CreateAsync();
        var draft = await _bed.UploadDraftAsync("1.0.0");

        var decision = await Approvals.RejectAsync(draft.Id, null, null, ReleaseActor.Admin("alice"));

        Assert.That(decision.Status, Is.EqualTo(ReleaseDecisionStatus.Done), decision.Message);
        Assert.That((await _bed.EventsAsync(SecurityEventType.ReleaseRejected)).Single().Details, Does.Contain("(a draft)"));
    }

    [Test]
    public async Task Reject_APublishedBuild_IsRefused()
    {
        _bed = await ReleaseTestBed.CreateAsync();
        var build = await _bed.UploadSignedAsync("1.0.0");

        var decision = await Approvals.RejectAsync(build.Id, null, null, ReleaseActor.Admin("alice"));

        Assert.That(decision.Status, Is.EqualTo(ReleaseDecisionStatus.NotAwaiting));
        Assert.That(decision.Message, Does.Contain("deprecate or delete"));
        Assert.That(await _bed.LatestAsync(), Is.EqualTo("1.0.0"));
    }

    [Test]
    public async Task ApproveThenReject_OnlyTheFirstDecisionWins()
    {
        _bed = await ReleaseTestBed.CreateAsync(ReleaseApproval.Required);
        var build = await _bed.UploadSignedAsync("1.0.0");
        using var other = _bed.Fixture.ScopeFactory.CreateScope();
        var second = (ReleaseApprovalService)other.ServiceProvider.GetService(typeof(ReleaseApprovalService))!;

        var approve = await Approvals.ApproveAsync(build.Id, ReleaseTestBed.Hash(build), ReleaseActor.Admin("alice"));
        var reject = await second.RejectAsync(build.Id, ReleaseTestBed.Hash(build), null, ReleaseActor.Admin("bob"));
        var approveAgain = await second.ApproveAsync(build.Id, ReleaseTestBed.Hash(build), ReleaseActor.Admin("bob"));

        Assert.That((approve.Status, reject.Status, approveAgain.Status),
            Is.EqualTo((ReleaseDecisionStatus.Done, ReleaseDecisionStatus.NotAwaiting, ReleaseDecisionStatus.NotAwaiting)));
        Assert.That(await _bed.EventsAsync(SecurityEventType.ReleaseApproved), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task ThreeApprovalsWithinAMinute_LeaveThreeEntries()
    {
        _bed = await ReleaseTestBed.CreateAsync(ReleaseApproval.Required);
        var builds = new[]
        {
            await _bed.UploadSignedAsync("1.0.0", TargetOS.Windows),
            await _bed.UploadSignedAsync("1.0.0", TargetOS.Linux),
            await _bed.UploadSignedAsync("1.0.0", TargetOS.MacOS),
        };

        foreach (var build in builds)
            await Approvals.ApproveAsync(build.Id, ReleaseTestBed.Hash(build), ReleaseActor.Admin("alice"));

        Assert.That(await _bed.EventsAsync(SecurityEventType.ReleaseApproved), Has.Count.EqualTo(3));
        var throttle = new SecurityEventThrottle();
        Assert.That(throttle.ShouldWrite(SecurityEventType.ReleaseApproved, "1.2.3.4", "p", out _)
                    && throttle.ShouldWrite(SecurityEventType.ReleaseApproved, "1.2.3.4", "p", out _), Is.True,
            "the security log never collapses release decisions");
    }

    [Test]
    public async Task SignedDraft_TakesThePackagesReleaseApproval()
    {
        _bed = await ReleaseTestBed.CreateAsync(ReleaseApproval.Required);
        var draft = await _bed.UploadDraftAsync("1.0.0");

        var result = await _bed.SignDraftAsync(draft);

        Assert.That(result, Is.EqualTo(new DraftPublishResult(BuildState.Pending, null)));
        Assert.That(await _bed.LatestAsync(), Is.Null, "signed, but still waiting for approval");
        var signed = await _bed.BuildAsync("1.0.0");
        Assert.That(signed.ReleaseKeyId, Is.EqualTo(ReleaseKeys.PublicKeyOf(_bed.Key).KeyId));
        Assert.That((await Approvals.ApproveAsync(signed.Id, ReleaseTestBed.Hash(signed), ReleaseActor.Admin("alice"))).Status,
            Is.EqualTo(ReleaseDecisionStatus.Done));
    }

    [Test]
    public async Task SignedDraft_UnderDelayed_StartsTheTimerAtSigning()
    {
        _bed = await ReleaseTestBed.CreateAsync(ReleaseApproval.Delayed);
        var draft = await _bed.UploadDraftAsync("1.0.0");

        var result = await _bed.SignDraftAsync(draft);

        Assert.That(result!.State, Is.EqualTo(BuildState.Pending));
        Assert.That(result.PublishAfter, Is.EqualTo(DateTime.UtcNow.AddMinutes(1440)).Within(TimeSpan.FromMinutes(1)));
    }

    [Test]
    public async Task ListUnpublished_ShowsDraftsAndPending_WithKeyLabels()
    {
        _bed = await ReleaseTestBed.CreateAsync(ReleaseApproval.Required);
        await _bed.UploadDraftAsync("1.0.0");
        var pending = await _bed.UploadSignedAsync("1.1.0");

        var list = await Approvals.ListUnpublishedAsync(_bed.Package.Id);

        Assert.That(list.Select(u => (u.Version, u.State)),
            Is.EqualTo(new[] { ("1.0.0", BuildState.Draft), ("1.1.0", BuildState.Pending) }));
        var item = list[1];
        Assert.That((item.KeyLabel, item.UploadedByKeyName, item.ManifestSha256), Is.EqualTo(("ci", "ci-upload", ReleaseTestBed.Hash(pending))));
        Assert.That(await Approvals.GetUnpublishedAsync(ReleaseTestBed.PackageId, "1.1", TargetOS.Windows, Architecture.X64), Is.Not.Null);
    }
}
