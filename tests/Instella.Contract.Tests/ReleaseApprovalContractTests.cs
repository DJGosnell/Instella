using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Instella.CLI.Services;
using Instella.Core.Platform;
using Instella.Core.Trust;
using Instella.Core.Wire;
using Instella.Server.Data.Entities;
using Instella.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Instella.Contract.Tests;

/// <summary>
/// Release approval over HTTP: a signed upload to a package with release approval Required is held
/// (<c>state: pending</c>) and invisible to clients until a separate approve key approves the exact
/// manifest it reviewed; the upload key can neither see nor decide it.
/// </summary>
[TestFixture]
public class ReleaseApprovalContractTests
{
    private const string PackageId = "com.instella.approval";
    private static readonly Version V1 = new(1, 0, 0);

    private ContractServer _server = null!;
    private string _uploadKey = null!;
    private string _approveKey = null!;
    private string _work = null!;
    private ECDsa _signingKey = null!;
    private Uri _base = null!;

    [SetUp]
    public async Task SetUp()
    {
        _server = new ContractServer();
        _base = new Uri(_server.BaseUrl);
        _work = Directory.CreateTempSubdirectory("instella-approval-contract-").FullName;
        _signingKey = ReleaseKeys.Generate();
        _server.CreateClient().Dispose();   // starts the host (and migrates)

        using var scope = _server.Services.CreateScope();
        var packages = scope.ServiceProvider.GetRequiredService<PackageService>();
        var package = await packages.CreatePackageAsync(PackageId, "Approval");
        await packages.AddPublisherKeyAsync(package.Id, ReleaseKeys.PublicKeyOf(_signingKey).PublicKey, "ci");
        await scope.ServiceProvider.GetRequiredService<ReleaseApprovalService>()
            .SetReleaseApprovalAsync(package.Id, ReleaseApproval.Required, 1440, "admin");
        var auth = scope.ServiceProvider.GetRequiredService<AuthService>();
        (_, _uploadKey) = await auth.CreateApiKeyAsync("ci", ApiKeyScope.Package, canUpload: true, packageId: package.Id);
        (_, _approveKey) = await auth.CreateApiKeyAsync("laptop", ApiKeyScope.Package, canUpload: false, packageId: package.Id,
            canApproveReleases: true);
    }

    [TearDown]
    public void TearDown()
    {
        _signingKey.Dispose();
        _server.Dispose();
        Directory.Delete(_work, recursive: true);
    }

    [Test]
    public async Task PendingUpload_IsHidden_UntilTheApproveKeyApprovesTheReviewedManifest()
    {
        var response = await UploadAsync();
        Assert.That((response.State, response.PublishAfter), Is.EqualTo((ReleaseStates.Pending, (DateTime?)null)));

        using var anonymous = _server.CreateClient();
        var check = await anonymous.GetFromJsonAsync(ApiRoutes.ForCheckUpdate(_base, PackageId, new Version(0, 9), TargetPlatform.Windows,
            Architecture.X64, "stable"), WireJsonContext.Default.CheckUpdateResponse);
        Assert.That(check!.UpdateAvailable, Is.False, "a pending release is never offered");

        using var approver = Client(_approveKey);
        var list = await approver.GetFromJsonAsync(ApiRoutes.ForApprovals(_base, PackageId), WireJsonContext.Default.UnpublishedReleaseSummaryArray);
        Assert.That(list!.Select(r => (r.Version, r.State, r.KeyLabel, r.UploadedBy)),
            Is.EqualTo(new[] { ("1.0.0", ReleaseStates.Pending, (string?)"ci", (string?)"ci") }));

        var one = await approver.GetFromJsonAsync(Approval(), WireJsonContext.Default.UnpublishedReleaseResponse);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Convert.FromBase64String(one!.Manifest)));
        Assert.That(hash, Is.EqualTo(one.Summary.ManifestSha256));

        using var wrong = await Post(approver, ApiRoutes.ForApproveRelease(_base, PackageId, V1, TargetPlatform.Windows, Architecture.X64),
            new ApproveReleaseRequest { ManifestSha256 = new string('0', 64) });
        Assert.That(wrong.StatusCode, Is.EqualTo(HttpStatusCode.Conflict), "not the manifest that was reviewed");

        using var approved = await Post(approver, ApiRoutes.ForApproveRelease(_base, PackageId, V1, TargetPlatform.Windows, Architecture.X64),
            new ApproveReleaseRequest { ManifestSha256 = hash });
        Assert.That(approved.StatusCode, Is.EqualTo(HttpStatusCode.OK), await approved.Content.ReadAsStringAsync());

        check = await anonymous.GetFromJsonAsync(ApiRoutes.ForCheckUpdate(_base, PackageId, new Version(0, 9), TargetPlatform.Windows,
            Architecture.X64, "stable"), WireJsonContext.Default.CheckUpdateResponse);
        Assert.That(check!.Version, Is.EqualTo("1.0.0"));
        var audit = await _server.QueryAsync(db => db.SecurityEvents.Where(e => e.EventType == SecurityEventType.ReleaseApproved).SingleAsync());
        Assert.That((audit.ApiKeyName, audit.IpAddress), Is.EqualTo(("laptop", ContractServer.ClientAddress.ToString())));
        Assert.That(await _server.QueryAsync(db => db.SecurityEvents.CountAsync(e => e.EventType == SecurityEventType.ReleasePending)),
            Is.EqualTo(1));
    }

    [Test]
    public async Task TheUploadKey_CannotSeeOrDecide()
    {
        await UploadAsync();
        using var uploader = Client(_uploadKey);

        using var list = await uploader.GetAsync(ApiRoutes.ForApprovals(_base, PackageId));
        using var approve = await Post(uploader, ApiRoutes.ForApproveRelease(_base, PackageId, V1, TargetPlatform.Windows, Architecture.X64),
            new ApproveReleaseRequest { ManifestSha256 = new string('0', 64) });
        using var reject = await Post(uploader, ApiRoutes.ForRejectRelease(_base, PackageId, V1, TargetPlatform.Windows, Architecture.X64),
            new RejectReleaseRequest());

        Assert.That(new[] { list.StatusCode, approve.StatusCode, reject.StatusCode }, Is.All.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task Reject_DeletesTheRelease()
    {
        await UploadAsync();
        using var approver = Client(_approveKey);

        using var rejected = await Post(approver, ApiRoutes.ForRejectRelease(_base, PackageId, V1, TargetPlatform.Windows, Architecture.X64),
            new RejectReleaseRequest { Reason = "unexpected tag" });

        Assert.That(rejected.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await _server.QueryAsync(db => db.VersionBuilds.CountAsync()), Is.Zero);
        using var again = await approver.GetAsync(Approval());
        Assert.That(again.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task UnknownPackage_Is404_AndNoKey_Is401()
    {
        using var approver = Client(_approveKey);
        using var unknown = await approver.GetAsync(ApiRoutes.ForApprovals(_base, "com.nobody"));
        using var anonymous = _server.CreateClient();
        using var noKey = await anonymous.GetAsync(ApiRoutes.ForApprovals(_base, PackageId));

        Assert.That((unknown.StatusCode, noKey.StatusCode), Is.EqualTo((HttpStatusCode.NotFound, HttpStatusCode.Unauthorized)));
    }

    [Test]
    public async Task SignedDraft_UnderRequired_IsPending()
    {
        await UploadAsync(draft: true);
        using var api = new ApiClient(_server.CreateClient(), _server.BaseUrl, _uploadKey);
        var bytes = Convert.FromBase64String((await api.GetDraftAsync(PackageId, V1, TargetPlatform.Windows, Architecture.X64)).Data!.Manifest);
        using var uploader = Client(_uploadKey);

        using var published = await uploader.PostAsJsonAsync(ApiRoutes.ForPublishDraft(_base, PackageId, V1, TargetPlatform.Windows, Architecture.X64),
            ReleaseSigner.Sign(bytes, _signingKey), WireJsonContext.Default.SignedRelease);
        var body = await published.Content.ReadFromJsonAsync(WireJsonContext.Default.PublishDraftResponse);

        Assert.That(body!.State, Is.EqualTo(ReleaseStates.Pending));
        Assert.That(body.Message, Does.Contain("pending approval"));
        Assert.That(await _server.QueryAsync(db => db.SecurityEvents.CountAsync(e => e.EventType == SecurityEventType.DraftSigned)), Is.EqualTo(1));
    }

    [Test]
    public void TheApprovalRoutes_NeedAnApiKey_NotAnAdminCookie()
    {
        var authorize = (Microsoft.AspNetCore.Authorization.AuthorizeAttribute)Attribute.GetCustomAttribute(
            typeof(Instella.Server.Api.ApprovalsController), typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute))!;
        Assert.That(authorize.Policy, Is.EqualTo("ApiKey"));
    }

    [Test]
    public async Task TheCli_ListsAndApproves_AgainstTheRealServer()
    {
        var upload = await UploadAsync();
        Assert.That(upload.State, Is.EqualTo(ReleaseStates.Pending));
        using var client = new ApiClient(_server.CreateClient(), _server.BaseUrl, _approveKey);
        var console = new Instella.CLI.Commands.CommandConsole(new StringWriter(), new StringWriter(), new StringReader(""), true);

        Assert.That(await Instella.CLI.Commands.PendingCommand.RunAsync(client, PackageId, console, CancellationToken.None), Is.Zero);
        var exit = await Instella.CLI.Commands.ApproveCommand.RunAsync(client,
            new Instella.CLI.Commands.ReleaseTarget(PackageId, V1, TargetPlatform.Windows, Architecture.X64),
            new DirectoryInfo(Path.Combine(_work, "app")), null, null, yes: true, console, CancellationToken.None);

        Assert.That(exit, Is.Zero, console.Error.ToString());
        Assert.That(console.Out.ToString(), Does.Contain("pending approval").And.Contain("Approved 1.0.0 windows/x64"));
        Assert.That(await _server.QueryAsync(db => db.VersionBuilds.SingleAsync()).ContinueWith(t => t.Result.State), Is.EqualTo(BuildState.Published));
    }

    private Uri Approval() => ApiRoutes.ForApproval(_base, PackageId, V1, TargetPlatform.Windows, Architecture.X64);

    private HttpClient Client(string key)
    {
        var client = _server.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return client;
    }

    private static Task<HttpResponseMessage> Post<T>(HttpClient client, Uri uri, T body) =>
        client.PostAsJsonAsync(uri, body, (System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>)WireJsonContext.Default.GetTypeInfo(typeof(T))!);

    /// <summary>Uploads 1.0.0 with the upload key; the result carries the state the server reported.</summary>
    private async Task<UploadResult> UploadAsync(bool draft = false)
    {
        var dir = Path.Combine(_work, "app");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "App.exe"), "app 1.0.0");

        using var client = new UploadClient(_server.CreateClient(), _server.BaseUrl, _uploadKey);
        var result = await client.UploadVersionAsync(new UploadRequest
        {
            PackageId = PackageId, Version = V1, SourceDirectory = dir, Channel = "stable",
            Platform = TargetPlatform.Windows, Architecture = Architecture.X64,
            SigningKey = draft ? null : _signingKey, Draft = draft,
        });
        Assert.That(result.Success, Is.True, result.Error);
        return result;
    }
}
