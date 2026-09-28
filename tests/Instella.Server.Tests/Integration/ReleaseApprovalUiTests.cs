using Bunit;
using Instella.Server.Components.Shared;
using Instella.Server.Data;
using Instella.Server.Data.Entities;
using Instella.Server.Services;
using Instella.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Instella.Server.Tests.Integration;

/// <summary>
/// The admin UI's release approval: a pending build shows Approve and Reject behind a confirmation,
/// the decision is recorded with the signed-in admin, and the package pane sets the release approval.
/// </summary>
[TestFixture]
public sealed class ReleaseApprovalUiTests
{
    private ReleaseTestBed _bed = null!;
    private BunitContext _ctx = null!;

    [SetUp]
    public void SetUp()
    {
        _ctx = new BunitContext();
        _ctx.AddAuthorization().SetAuthorized("alice");
    }

    [TearDown]
    public void TearDown()
    {
        _ctx.Dispose();
        _bed.Dispose();
    }

    private void AddServices()
    {
        var fixture = _bed.Fixture;
        _ctx.Services.AddSingleton(fixture.PackageService);
        _ctx.Services.AddSingleton(fixture.ReleaseApprovals);
        _ctx.Services.AddSingleton<AppDbContext>(fixture.Db);
        _ctx.Services.AddSingleton(new DownloadTokenService(fixture.Db, new MemoryCache(new MemoryCacheOptions())));
    }

    private async Task<IRenderedComponent<BuildProperties>> RenderBuildAsync(VersionBuild uploaded, Action? onChanged = null, Action? onDelete = null)
    {
        var build = (await _bed.Fixture.PackageService.GetBuildByIdAsync(uploaded.Id))!;
        return _ctx.Render<BuildProperties>(p => p
            .Add(x => x.Build, build)
            .Add(x => x.Version, build.Version)
            .Add(x => x.Package, build.Version.Package)
            .Add(x => x.OnChanged, () => onChanged?.Invoke())
            .Add(x => x.OnDelete, () => onDelete?.Invoke()));
    }

    [Test]
    public async Task PendingBuild_ApproveAfterConfirmation_PublishesIt_AsTheSignedInAdmin()
    {
        _bed = await ReleaseTestBed.CreateAsync(ReleaseApproval.Required);
        AddServices();
        var uploaded = await _bed.UploadSignedAsync("1.0.0");
        var changed = false;

        var cut = await RenderBuildAsync(uploaded, onChanged: () => changed = true);
        Assert.That(cut.Markup, Does.Contain("Pending approval").And.Contain(ReleaseTestBed.Hash(uploaded)));

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Approve…").Click();
        Assert.That(cut.Markup, Does.Contain("Installations can update to it"));
        Assert.That((await _bed.BuildAsync("1.0.0")).State, Is.EqualTo(BuildState.Pending), "nothing happens before the confirmation");
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Approve").ClickAsync(new());

        Assert.That(changed, Is.True);
        Assert.That((await _bed.BuildAsync("1.0.0")).State, Is.EqualTo(BuildState.Published));
        Assert.That((await _bed.EventsAsync(SecurityEventType.ReleaseApproved)).Single().Username, Is.EqualTo("alice"));
    }

    [Test]
    public async Task PendingBuild_RejectWithAReason_DeletesIt()
    {
        _bed = await ReleaseTestBed.CreateAsync(ReleaseApproval.Required);
        AddServices();
        var uploaded = await _bed.UploadSignedAsync("1.0.0");
        var deleted = false;

        var cut = await RenderBuildAsync(uploaded, onDelete: () => deleted = true);
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Reject…").Click();
        cut.Find("input.form-control").Change("wrong tag");
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Reject and delete").ClickAsync(new());

        Assert.That(deleted, Is.True);
        Assert.That(await _bed.Fixture.Db.VersionBuilds.CountAsync(), Is.Zero);
        var entry = (await _bed.EventsAsync(SecurityEventType.ReleaseRejected)).Single();
        Assert.That(entry.Username, Is.EqualTo("alice"));
        Assert.That(entry.Details, Does.Contain("reason: wrong tag"));
    }

    [Test]
    public async Task PublishedBuild_HasNoApprovalButtons()
    {
        _bed = await ReleaseTestBed.CreateAsync();
        AddServices();
        var uploaded = await _bed.UploadSignedAsync("1.0.0");

        var cut = await RenderBuildAsync(uploaded);

        Assert.That(cut.FindAll("button").Select(b => b.TextContent.Trim()), Has.None.StartsWith("Approve").And.None.StartsWith("Reject"));
    }

    [Test]
    public async Task PackagePane_SetsReleaseApproval_AndListsWhatAwaitsADecision()
    {
        _bed = await ReleaseTestBed.CreateAsync();
        AddServices();
        var package = (await _bed.Fixture.PackageService.GetPackageByIdAsync(_bed.Package.Id))!;

        var cut = _ctx.Render<PackageProperties>(p => p.Add(x => x.Package, package));
        cut.Find("#release-approval").Change(ReleaseApproval.Required.ToString());
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Save release approval").ClickAsync(new());

        _bed.Fixture.Db.ChangeTracker.Clear();
        Assert.That((await _bed.Fixture.Db.Packages.SingleAsync()).ReleaseApproval, Is.EqualTo(ReleaseApproval.Required));
        Assert.That((await _bed.EventsAsync(SecurityEventType.ReleaseApprovalChanged)).Single().Username, Is.EqualTo("alice"));

        await _bed.UploadSignedAsync("1.0.0");
        var reloaded = (await _bed.Fixture.PackageService.GetPackageByIdAsync(_bed.Package.Id))!;
        cut.Render(p => p.Add(x => x.Package, reloaded));
        Assert.That(cut.Markup, Does.Contain("Awaiting a decision").And.Contain("pending approval"));
    }
}
