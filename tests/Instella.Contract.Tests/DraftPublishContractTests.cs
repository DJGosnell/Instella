using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Instella.CLI.Services;
using Instella.Core.Platform;
using Instella.Core.Trust;
using Instella.Core.Wire;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace Instella.Contract.Tests;

/// <summary>
/// Draft releases: CI uploads with <c>upload --draft</c> (no key); the build is invisible to
/// clients until <c>instella publish</c> signs exactly the stored manifest bytes.
/// </summary>
[TestFixture]
public class DraftPublishContractTests
{
    private const string PackageId = "com.instella.drafts";
    private static readonly Version V1 = new(1, 0, 0);
    private static readonly Version V2 = new(2, 0, 0);

    private ContractServer _server = null!;
    private string _apiKey = null!;
    private string _work = null!;
    private ECDsa _signingKey = null!;
    private PublisherKey _publicKey = null!;

    [SetUp]
    public async Task SetUp()
    {
        _server = new ContractServer();
        _apiKey = await _server.SeedPackageAndKeyAsync(PackageId);
        _work = Directory.CreateTempSubdirectory("instella-draft-contract-").FullName;
        _signingKey = ReleaseKeys.Generate();
        _publicKey = ReleaseKeys.PublicKeyOf(_signingKey);
    }

    [TearDown]
    public void TearDown()
    {
        _signingKey.Dispose();
        _server.Dispose();
        Directory.Delete(_work, recursive: true);
    }

    [Test]
    public async Task Draft_IsInvisibleUntilPublished_ThenBehavesLikeASignedUpload()
    {
        Assert.That((await Upload(V1, draft: false)).Success, Is.True);
        var upload = await Upload(V2, draft: true);
        Assert.That(upload.Success, Is.True, upload.Error);

        using var http = _server.CreateClient();
        var server = new Uri(_server.BaseUrl);
        await AssertOnlyV1IsVisible(http, server);

        using var api = new ApiClient(_server.CreateClient(), _server.BaseUrl, _apiKey);
        var draft = await api.GetDraftAsync(PackageId, V2, TargetPlatform.Windows, Architecture.X64);
        Assert.That(draft.IsSuccess, Is.True, draft.Error);
        var bytes = Convert.FromBase64String(draft.Data!.Manifest);

        var release = await new LocalSigningKey(_signingKey).SignAsync(bytes, CancellationToken.None);
        var published = await api.PublishDraftAsync(PackageId, V2, TargetPlatform.Windows, Architecture.X64, release);
        Assert.That(published.IsSuccess, Is.True, published.Error);

        var signed = await http.GetFromJsonAsync(ApiRoutes.ForRelease(server, PackageId, V2, TargetPlatform.Windows, Architecture.X64),
            WireJsonContext.Default.SignedRelease);
        var manifest = ReleaseVerifier.Verify(signed!, new TrustPolicy([_publicKey], PackageId, "windows", "x64", null, V2));
        Assert.That(manifest.Installers!.Single().FileName, Is.EqualTo("App-WebSetup-2.0.0.exe"), "the draft carried the installer list");

        var check = await http.GetFromJsonAsync(ApiRoutes.ForCheckUpdate(server, PackageId, V1, TargetPlatform.Windows, Architecture.X64, "stable"),
            WireJsonContext.Default.CheckUpdateResponse);
        Assert.That(check!.Version, Is.EqualTo("2.0.0"), "published: offered as an update");
        Assert.That((await api.GetDraftAsync(PackageId, V2, TargetPlatform.Windows, Architecture.X64)).StatusCode,
            Is.EqualTo(HttpStatusCode.NotFound), "no longer a draft");
    }

    [Test]
    public async Task Publish_WithASignatureOverOtherBytes_IsRefused()
    {
        Assert.That((await Upload(V2, draft: true)).Success, Is.True);
        using var api = new ApiClient(_server.CreateClient(), _server.BaseUrl, _apiKey);

        var other = await new LocalSigningKey(_signingKey).SignAsync("{\"formatVersion\":1}"u8.ToArray(), CancellationToken.None);
        var result = await api.PublishDraftAsync(PackageId, V2, TargetPlatform.Windows, Architecture.X64, other);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Error, Does.Contain("not this draft's manifest"));
        Assert.That(await _server.QueryAsync(db => db.VersionBuilds.SingleAsync()).ContinueWith(t => t.Result.IsDraft), Is.True);
    }

    [Test]
    public async Task RegisteredPublisherKey_AllowsTheDraft_ButOnlyThatKeyCanPublishIt()
    {
        await _server.QueryAsync(async db =>
        {
            var pkg = await db.Packages.SingleAsync(p => p.PackageId == PackageId);
            db.PackagePublisherKeys.Add(new() { PackageId = pkg.Id, KeyId = _publicKey.KeyId, PublicKey = _publicKey.PublicKey });
            return await db.SaveChangesAsync();
        });
        Assert.That((await Upload(V2, draft: true)).Success, Is.True, "a draft needs no signature yet");

        using var api = new ApiClient(_server.CreateClient(), _server.BaseUrl, _apiKey);
        var bytes = Convert.FromBase64String((await api.GetDraftAsync(PackageId, V2, TargetPlatform.Windows, Architecture.X64)).Data!.Manifest);
        using var foreign = ReleaseKeys.Generate();

        var byForeign = await api.PublishDraftAsync(PackageId, V2, TargetPlatform.Windows, Architecture.X64,
            await new LocalSigningKey(foreign).SignAsync(bytes, CancellationToken.None));
        Assert.That(byForeign.IsSuccess, Is.False);
        Assert.That(byForeign.Error, Does.Contain("Release rejected"));

        var byOwner = await api.PublishDraftAsync(PackageId, V2, TargetPlatform.Windows, Architecture.X64,
            await new LocalSigningKey(_signingKey).SignAsync(bytes, CancellationToken.None));
        Assert.That(byOwner.IsSuccess, Is.True, byOwner.Error);
    }

    private async Task AssertOnlyV1IsVisible(HttpClient http, Uri server)
    {
        var versions = await http.GetFromJsonAsync(ApiRoutes.ForPackageVersions(server, PackageId), WireJsonContext.Default.VersionSummaryArray);
        Assert.That(versions!.Select(v => v.VersionString), Is.EqualTo(new[] { "1.0.0" }), "a draft-only version is not listed");

        var check = await http.GetFromJsonAsync(ApiRoutes.ForCheckUpdate(server, PackageId, V1, TargetPlatform.Windows, Architecture.X64, "stable"),
            WireJsonContext.Default.CheckUpdateResponse);
        Assert.That(check!.UpdateAvailable, Is.False, "a draft is never offered as an update");

        foreach (var uri in new[]
                 {
                     ApiRoutes.ForRelease(server, PackageId, V2, TargetPlatform.Windows, Architecture.X64),
                     ApiRoutes.ForDownloadBuild(server, PackageId, V2, TargetPlatform.Windows, Architecture.X64),
                     ApiRoutes.ForDownloadInstaller(server, PackageId, V2, TargetPlatform.Windows, Architecture.X64, InstallerKinds.Online),
                 })
        {
            using var response = await http.GetAsync(uri);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound), uri.AbsolutePath);
        }

        using var latest = await http.GetAsync(ApiRoutes.ForLatestInstaller(server, PackageId, TargetPlatform.Windows, Architecture.X64, InstallerKinds.Online));
        Assert.That(await latest.Content.ReadAsStringAsync(), Is.EqualTo("installer 1.0.0"), "latest skips the draft");
    }

    private async Task<UploadResult> Upload(Version version, bool draft)
    {
        var dir = Path.Combine(_work, "build-" + version);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "App.exe"), "app " + version);
        var installerDir = Path.Combine(_work, "installer-" + version);
        Directory.CreateDirectory(installerDir);
        var installer = Path.Combine(installerDir, $"App-WebSetup-{version}.exe");
        File.WriteAllText(installer, "installer " + version);

        using var client = new UploadClient(_server.CreateClient(), _server.BaseUrl, _apiKey);
        return await client.UploadVersionAsync(new UploadRequest
        {
            PackageId = PackageId, Version = version, SourceDirectory = dir, Channel = "stable",
            Platform = TargetPlatform.Windows, Architecture = Architecture.X64,
            SigningKey = draft ? null : _signingKey, Draft = draft,
            Installers = [new InstallerUpload(InstallerKinds.Online, installer)],
        });
    }
}
