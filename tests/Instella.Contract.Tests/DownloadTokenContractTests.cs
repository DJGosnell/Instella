using System.Net;
using System.Net.Http.Headers;
using Instella.Core.Wire;
using Instella.Server.Data;
using Instella.Server.Data.Entities;
using Instella.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Instella.Contract.Tests;

/// <summary>8.5 over HTTP: a download token opens its own private package, over the real middleware.</summary>
[TestFixture]
public sealed class DownloadTokenContractTests
{
    private ContractServer _server = null!;
    private string _tokenA = null!;

    [OneTimeSetUp]
    public async Task SetUp()
    {
        _server = new ContractServer();
        await _server.SeedPackageAndKeyAsync("com.instella.token-a");
        await _server.SeedPackageAndKeyAsync("com.instella.token-b");
        using var scope = _server.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Packages.ExecuteUpdateAsync(u => u.SetProperty(p => p.DownloadAccessMode, DownloadAccessMode.PackageKeyRequired));
        var a = await db.Packages.SingleAsync(p => p.PackageId == "com.instella.token-a");
        (_, _tokenA) = await scope.ServiceProvider.GetRequiredService<DownloadTokenService>().CreateAsync(a.Id, "contract", null);
    }

    [OneTimeTearDown]
    public void TearDown() => _server.Dispose();

    [Test]
    public async Task TheToken_ReadsItsPackage()
    {
        using var response = await Get("com.instella.token-a", _tokenA);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), await response.Content.ReadAsStringAsync());
    }

    [Test]
    public async Task TheToken_DoesNotReadAnotherPackage()
    {
        using var response = await Get("com.instella.token-b", _tokenA);

        Assert.That(response.IsSuccessStatusCode, Is.False);
    }

    private async Task<HttpResponseMessage> Get(string packageId, string token)
    {
        var http = _server.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await http.GetAsync($"{ApiRoutes.Prefix}/packages/{packageId}");
    }
}
