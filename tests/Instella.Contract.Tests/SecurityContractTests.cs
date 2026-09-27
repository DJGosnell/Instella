using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Instella.Core.Wire;
using Instella.Server.Data.Entities;
using Instella.Server.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Instella.Contract.Tests;

/// <summary>
/// Known attacks on the server, each replayed against the real server; every one must fail.
/// </summary>
[TestFixture]
public partial class SecurityContractTests
{
    private const string PackageId = "com.instella.security";

    private ContractServer _server = null!;
    private string _fullKey = null!;
    private long _adminId;

    [OneTimeSetUp]
    public async Task SetUp()
    {
        _server = new ContractServer();
        _fullKey = await _server.SeedPackageAndKeyAsync(PackageId);
        using var scope = _server.Services.CreateScope();
        _adminId = (await scope.ServiceProvider.GetRequiredService<AuthService>().CreateAdminUserAsync("admin", "correct horse battery")).Id;
    }

    [SetUp]
    public void ClearBackoff() =>
        _server.Services.GetRequiredService<IRateLimitService>().ClearAllTemporaryBlocks();   // failed uploads back off per key

    [OneTimeTearDown]
    public void TearDown() => _server.Dispose();

    private HttpClient Browser() =>
        _server.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    private HttpClient Api(string key)
    {
        var client = _server.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return client;
    }

    [Test]
    public async Task Totp_WithoutAPendingCookie_RedirectsToExpired_AndSignsNobodyIn()
    {
        using var browser = Browser();
        var token = await AntiforgeryTokenAsync(browser);

        // Before 10.1 the form carried the user id, so the password step could be skipped.
        using var response = await browser.PostAsync("/api/auth/totp", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["userId"] = _adminId.ToString(),
            ["code"] = "123456",
        }));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Redirect), await response.Content.ReadAsStringAsync());
        Assert.That(response.Headers.Location?.OriginalString, Is.EqualTo("/login?error=expired"));
        Assert.That(response.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies : [],
            Has.None.StartsWith(".AspNetCore.Cookies").And.None.StartsWith("Instella.Admin"));
    }

    [Test]
    public async Task Login_WithoutAnAntiforgeryToken_IsRejected()
    {
        using var browser = Browser();
        using var response = await browser.PostAsync("/api/auth/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = "admin",
            ["password"] = "correct horse battery",
        }));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), await response.Content.ReadAsStringAsync());
    }

    [Test]
    public async Task SpoofedForwardedFor_DoesNotEscapeABan()
    {
        await BanTestClientAsync();
        try
        {
            using var client = Api(_fullKey);
            client.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.50");

            using var response = await client.PostAsJsonAsync($"{ApiRoutes.Prefix}/{ApiRoutes.UploadStart}",
                new StartUploadRequest { PackageId = PackageId, Version = "9.9.9", Os = "windows", Arch = "x64" });

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden),
                " X-Forwarded-For from an untrusted peer must not replace the connection address (10.3)");
        }
        finally
        {
            await UnbanAllAsync();
        }
    }

    [Test]
    public async Task ABan_CoversEveryEndpoint_ButNotTheHealthCheck()
    {
        using (var before = await Browser().GetAsync("/login"))
            Assert.That(before.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        await BanTestClientAsync();
        try
        {
            using var anonymous = _server.CreateClient();
            foreach (var path in new[] { $"/{ApiRoutes.Prefix}/{ApiRoutes.Packages}", $"/{ApiRoutes.Prefix}/packages/{PackageId}/versions", "/login" })
            {
                using var response = await anonymous.GetAsync(path);
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden), path);
            }
            using var health = await anonymous.GetAsync("/healthz");
            Assert.That(health.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }
        finally
        {
            await UnbanAllAsync();
        }
    }

    [Test]
    public void ConfigDirectory_AndKeys_AreOwnerOnly()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Ignore("POSIX permissions; Windows keeps the inherited ACL and DPAPI-protects the keys");
            return;
        }
        const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        Assert.That(File.GetUnixFileMode(_server.ConfigDir), Is.EqualTo(ownerOnly));
        Assert.That(File.GetUnixFileMode(Path.Combine(_server.ConfigDir, "keys")), Is.EqualTo(ownerOnly));
    }

    /// <summary>Bans the address the test host's clients connect from, as the admin UI would.</summary>
    private async Task BanTestClientAsync()
    {
        using var scope = _server.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IIpBanService>()
            .AddBanAsync(ContractServer.ClientAddress.ToString(), "test", _adminId);
    }

    private async Task UnbanAllAsync()
    {
        await _server.QueryAsync(db => db.IpBans.ExecuteDeleteAsync());
        _server.Services.GetRequiredService<IpBanCache>().Invalidate();
    }

    [Test]
    public async Task PrivatePackage_ReadsFollowTheDownloadAccessRule()
    {
        const string privateId = "com.instella.private";
        await _server.QueryAsync(async db =>
        {
            db.Packages.Add(new Package
            {
                PackageId = privateId, DisplayName = privateId,
                DownloadAccessMode = DownloadAccessMode.PackageKeyRequired, CreatedAt = DateTime.UtcNow,
            });
            return await db.SaveChangesAsync();
        });
        var uploadOnly = await CreateKeyAsync(canUpload: true, canDownload: false);
        var server = new Uri("http://localhost/");

        using var anonymous = _server.CreateClient();
        using var unknown = await anonymous.GetAsync(ApiRoutes.ForPackage(server, "com.instella.unknown").PathAndQuery);
        var unknownBody = await unknown.Content.ReadAsStringAsync();
        foreach (var uri in new[] { ApiRoutes.ForPackage(server, privateId), ApiRoutes.ForPackageVersions(server, privateId) })
        {
            // Without valid credentials a private package is indistinguishable from an unknown one.
            using var noKey = await anonymous.GetAsync(uri.PathAndQuery);
            Assert.That(noKey.StatusCode, Is.EqualTo(HttpStatusCode.NotFound), uri.ToString());
            Assert.That(await noKey.Content.ReadAsStringAsync(), Is.EqualTo(unknownBody), uri.ToString());
            using var bogusToken = await Api("idt_" + new string('A', 43)).GetAsync(uri.PathAndQuery);
            Assert.That(bogusToken.StatusCode, Is.EqualTo(HttpStatusCode.NotFound), "a bogus token too: " + uri);
            using var bogusKey = await Api("not-a-key").GetAsync(uri.PathAndQuery);
            Assert.That(bogusKey.StatusCode, Is.EqualTo(HttpStatusCode.NotFound), "an invalid API key too: " + uri);
            using var noPermission = await Api(uploadOnly).GetAsync(uri.PathAndQuery);
            Assert.That(noPermission.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden), uri.ToString());
            using var allowed = await Api(_fullKey).GetAsync(uri.PathAndQuery);
            Assert.That(allowed.StatusCode, Is.EqualTo(HttpStatusCode.OK), uri.ToString());
        }

        var summary = await Api(_fullKey).GetFromJsonAsync<PackageSummary>(ApiRoutes.ForPackage(server, privateId).PathAndQuery);
        Assert.That(summary!.PackageId, Is.EqualTo(privateId));
        var listed = await anonymous.GetFromJsonAsync<PackageSummary[]>(ApiRoutes.ForPackages(server).PathAndQuery);
        Assert.That(listed!.Select(p => p.PackageId), Does.Not.Contain(privateId), "private package ids are never listed");

        using var missing = await anonymous.GetAsync(ApiRoutes.ForPackage(server, "com.instella.none").PathAndQuery);
        Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task DownloadOnlyKey_CannotDeleteAVersion()
    {
        var version = await UploadVersionAsync("2.0.0");
        var downloadOnly = await CreateKeyAsync(canUpload: false, canDownload: true);

        using var response = await Api(downloadOnly).DeleteAsync($"{ApiRoutes.Prefix}/packages/{PackageId}/versions/{version}");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That(await _server.QueryAsync(db => db.PackageVersions.AnyAsync(v => v.VersionString == version)), Is.True);
    }

    [Test]
    public async Task VersionDeletion_WithFiles_WorksOnSqlite()
    {
        var version = await UploadVersionAsync("3.0.0");

        using var response = await Api(_fullKey).DeleteAsync($"{ApiRoutes.Prefix}/packages/{PackageId}/versions/{version}");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), await response.Content.ReadAsStringAsync());
        Assert.That(await _server.QueryAsync(db => db.PackageVersions.AnyAsync(v => v.VersionString == version)), Is.False);
    }

    [Test]
    public async Task AnUploadOnlyKey_CannotDeleteOrEditVersions()
    {
        // Managing versions is its own permission, off by default.
        var version = await UploadVersionAsync("3.1.0");
        var uploadOnly = await CreateKeyAsync(canUpload: true, canDownload: false);

        using var delete = await Api(uploadOnly).DeleteAsync($"{ApiRoutes.Prefix}/packages/{PackageId}/versions/{version}");
        using var edit = await Api(uploadOnly).PutAsJsonAsync($"{ApiRoutes.Prefix}/packages/{PackageId}/versions/{version}",
            new UpdateVersionRequest { IsDeprecated = true });

        Assert.That(delete.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That(edit.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That(await _server.QueryAsync(db => db.PackageVersions.AnyAsync(v => v.VersionString == version && !v.IsDeprecated)), Is.True);
    }

    [TestCase("../evil.dll")]
    [TestCase("lib/../../evil.dll")]
    [TestCase("C:/Windows/evil.dll")]
    [TestCase("/etc/evil")]
    public async Task Upload_OfATraversingPath_IsRejected(string path)
    {
        using var client = Api(_fullKey);
        var session = await StartSessionAsync(client, "4.0.0");
        var content = "payload"u8.ToArray();

        using var response = await client.PostAsync(
            $"{ApiRoutes.Prefix}/upload/{session}/file?path={Uri.EscapeDataString(path)}&sha256={Sha(content)}",
            new ByteArrayContent(content));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        await client.DeleteAsync($"{ApiRoutes.Prefix}/upload/{session}");
    }

    [TestCase("/api-keys")]
    [TestCase("/packages")]
    [TestCase("/settings")]
    public async Task AdminPage_Anonymous_RedirectsToLogin(string page)
    {
        using var browser = Browser();

        using var response = await browser.GetAsync(page);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
        Assert.That(response.Headers.Location?.ToString(), Does.Contain("/login"));
    }

    [Test]
    public async Task HealthEndpoint_IsAnonymous()
    {
        using var response = await Browser().GetAsync("/healthz");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    private async Task<string> UploadVersionAsync(string version)
    {
        using var client = Api(_fullKey);
        var session = await StartSessionAsync(client, version);
        var content = RandomNumberGenerator.GetBytes(256);
        using (var file = await client.PostAsync(
                   $"{ApiRoutes.Prefix}/upload/{session}/file?path=App.exe&sha256={Sha(content)}", new ByteArrayContent(content)))
            Assert.That(file.StatusCode, Is.EqualTo(HttpStatusCode.OK), await file.Content.ReadAsStringAsync());
        using (var complete = await client.PostAsJsonAsync($"{ApiRoutes.Prefix}/upload/{session}/complete", new { }))
            Assert.That(complete.StatusCode, Is.EqualTo(HttpStatusCode.OK), await complete.Content.ReadAsStringAsync());
        return version;
    }

    private static async Task<Guid> StartSessionAsync(HttpClient client, string version)
    {
        using var response = await client.PostAsJsonAsync($"{ApiRoutes.Prefix}/{ApiRoutes.UploadStart}",
            new StartUploadRequest { PackageId = PackageId, Version = version, Os = "windows", Arch = "x64" });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<StartUploadResponse>())!.SessionId;
    }

    private async Task<string> CreateKeyAsync(bool canUpload, bool canDownload)
    {
        using var scope = _server.Services.CreateScope();
        var (_, key) = await scope.ServiceProvider.GetRequiredService<AuthService>()
            .CreateApiKeyAsync("restricted", ApiKeyScope.Admin, canUpload, canDownload);
        return key;
    }

    private static async Task<string> AntiforgeryTokenAsync(HttpClient browser)
    {
        using var page = await browser.GetAsync("/login");
        var html = await page.Content.ReadAsStringAsync();
        var match = TokenInput().Match(html);
        Assert.That(match.Success, Is.True, "the login page renders an antiforgery token");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static string Sha(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")]
    private static partial Regex TokenInput();
}
