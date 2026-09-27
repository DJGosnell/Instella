using Instella.Core.Wire;
using Instella.Server.Auth;
using Instella.Server.Data.Entities;
using Instella.Server.Services;
using Instella.Server.Tests.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NUnit.Framework;

namespace Instella.Server.Tests.Services;

/// <summary>Download tokens admit their own PackageKeyRequired package, and nothing else.</summary>
[TestFixture]
public sealed class DownloadTokenTests
{
    private DatabaseFixture _db = null!;
    private MemoryCache _cache = null!;
    private DateTime _now;
    private DownloadTokenService _tokens = null!;
    private DownloadAccess _access = null!;
    private Package _a = null!;
    private Package _b = null!;

    [SetUp]
    public async Task SetUp()
    {
        _db = new DatabaseFixture();
        _cache = new MemoryCache(new MemoryCacheOptions());
        _now = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        _tokens = new DownloadTokenService(_db.Context, _cache) { UtcNow = () => _now };
        _access = new DownloadAccess(new AuthService(_db.Context), _tokens, new SecurityLogService(_db.Context));
        _a = await _db.SeedPackageAsync("com.test.a");
        _b = await _db.SeedPackageAsync("com.test.b");
        await _db.Context.Packages.ExecuteUpdateAsync(u => u.SetProperty(p => p.DownloadAccessMode, DownloadAccessMode.PackageKeyRequired));
        _db.Context.ChangeTracker.Clear();
        _a = await _db.Context.Packages.SingleAsync(p => p.Id == _a.Id);
        _b = await _db.Context.Packages.SingleAsync(p => p.Id == _b.Id);
    }

    [TearDown]
    public void TearDown()
    {
        _cache.Dispose();
        _db.Dispose();
    }

    [Test]
    public async Task ATokenHasTheDocumentedShape_AndOnlyItsHashIsStored()
    {
        var (token, plain) = await _tokens.CreateAsync(_a.Id, "installers", null);

        Assert.That(plain, Has.Length.EqualTo(47).And.StartsWith("idt_"));
        Assert.That(DownloadTokens.LooksLikeToken(plain), Is.True);
        Assert.That(token.TokenHash, Is.EqualTo(DownloadTokens.Hash(plain)).And.Not.Contain(plain[4..]));
        Assert.That(token.DisplayPrefix, Is.EqualTo(plain[4..12]));
        Assert.That(await _db.Context.SecurityEvents.Select(e => e.EventType).SingleAsync(),
            Is.EqualTo(SecurityEventType.DownloadTokenCreated));
    }

    [Test]
    public async Task PackageKeyRequired_WithItsToken_IsAllowed()
    {
        var (_, plain) = await _tokens.CreateAsync(_a.Id, "installers", null);

        Assert.That(await CheckAsync(_a, plain), Is.Null);
    }

    [Test]
    public async Task AnotherPackagesToken_IsDenied()
    {
        var (_, plain) = await _tokens.CreateAsync(_a.Id, "installers", null);

        Assert.That(await CheckAsync(_b, plain), Is.Not.Null);
    }

    [Test]
    public async Task ARevokedToken_IsDenied_AtOnce()
    {
        var (token, plain) = await _tokens.CreateAsync(_a.Id, "installers", null);
        Assert.That(await CheckAsync(_a, plain), Is.Null, "valid and now cached");

        await _tokens.RevokeAsync(token.Id);

        Assert.That(await CheckAsync(_a, plain), Is.Not.Null, "the revoke dropped the cached lookup");
    }

    [Test]
    public async Task AnExpiredToken_IsDenied()
    {
        var (_, plain) = await _tokens.CreateAsync(_a.Id, "installers", _now.AddDays(1));
        Assert.That(await CheckAsync(_a, plain), Is.Null);

        _now = _now.AddDays(2);

        Assert.That(await CheckAsync(_a, plain), Is.Not.Null);
    }

    [Test]
    public async Task MasterKeyRequired_NeverAcceptsAToken()
    {
        var (_, plain) = await _tokens.CreateAsync(_a.Id, "installers", null);
        _a.DownloadAccessMode = DownloadAccessMode.MasterKeyRequired;

        Assert.That(await CheckAsync(_a, plain), Is.Not.Null);
    }

    [Test]
    public async Task AnOpenPackage_IgnoresEvenAGarbageToken()
    {
        _a.DownloadAccessMode = DownloadAccessMode.Open;

        Assert.That(await CheckAsync(_a, "idt_" + new string('x', 43)), Is.Null);
    }

    [Test]
    public async Task LastUsedAt_IsWrittenAtMostOnceAnHour()
    {
        var (token, plain) = await _tokens.CreateAsync(_a.Id, "installers", null);

        await CheckAsync(_a, plain);
        var first = await LastUsed(token.Id);
        _now = _now.AddMinutes(30);
        await CheckAsync(_a, plain);
        var second = await LastUsed(token.Id);
        _now = _now.AddMinutes(31);
        await CheckAsync(_a, plain);
        var third = await LastUsed(token.Id);

        Assert.That(first, Is.EqualTo(new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc)));
        Assert.That(second, Is.EqualTo(first), "not rewritten within the hour");
        Assert.That(third, Is.EqualTo(_now));
    }

    [Test]
    public async Task AnApiKey_InTheSameHeader_StillWorks()
    {
        var (_, apiKey) = await new AuthService(_db.Context).CreateApiKeyAsync("ci", ApiKeyScope.Admin, canUpload: false, canDownload: true);

        Assert.That(await CheckAsync(_a, apiKey), Is.Null);
    }

    private async Task<Microsoft.AspNetCore.Mvc.IActionResult?> CheckAsync(Package package, string bearer)
    {
        var http = new DefaultHttpContext();
        http.Request.Headers.Authorization = "Bearer " + bearer;
        http.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
        return await _access.CheckAsync(http, package, CancellationToken.None);
    }

    private async Task<DateTime?> LastUsed(long id)
    {
        using var db = _db.CreateNewContext();
        return await db.DownloadTokens.Where(t => t.Id == id).Select(t => t.LastUsedAt).SingleAsync();
    }
}
