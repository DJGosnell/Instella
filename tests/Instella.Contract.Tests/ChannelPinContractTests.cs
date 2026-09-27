using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Instella.Core.Platform;
using Instella.Core.Wire;
using Instella.Server.Data;
using Instella.Server.Models;
using Instella.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Instella.Contract.Tests;

/// <summary>Over HTTP: check-update and <c>installer/…/latest</c> follow the channel's pin.</summary>
[TestFixture]
public sealed class ChannelPinContractTests
{
    private const string PackageId = "com.instella.pin";
    private ContractServer _server = null!;

    [OneTimeSetUp]
    public async Task SetUp()
    {
        _server = new ContractServer();
        await _server.SeedPackageAndKeyAsync(PackageId);
        foreach (var version in new[] { "1.0.0", "1.1.0", "1.2.0" })
            await PublishAsync(version);
        using var scope = _server.Services.CreateScope();
        var packages = scope.ServiceProvider.GetRequiredService<PackageService>();
        var id = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Packages
            .Where(p => p.PackageId == PackageId).Select(p => p.Id).SingleAsync();
        await packages.SetChannelPinAsync(id, "stable", "1.1.0");
    }

    [OneTimeTearDown]
    public void TearDown() => _server.Dispose();

    [Test]
    public async Task CheckUpdate_OffersThePinnedVersion()
    {
        using var http = _server.CreateClient();
        var check = await http.GetFromJsonAsync(
            ApiRoutes.ForCheckUpdate(new Uri(_server.BaseUrl), PackageId, new Version(1, 0, 0), TargetPlatform.Windows,
                Instella.Core.Platform.Architecture.X64, "stable"),
            WireJsonContext.Default.CheckUpdateResponse);

        Assert.That(check!.UpdateAvailable, Is.True);
        Assert.That(check.Version, Is.EqualTo("1.1.0"), "1.2.0 is above the pin");
    }

    [Test]
    public async Task CheckUpdate_FromThePinnedVersion_OffersNothing()
    {
        using var http = _server.CreateClient();
        var check = await http.GetFromJsonAsync(
            ApiRoutes.ForCheckUpdate(new Uri(_server.BaseUrl), PackageId, new Version(1, 1), TargetPlatform.Windows,
                Instella.Core.Platform.Architecture.X64, "stable"),
            WireJsonContext.Default.CheckUpdateResponse);

        Assert.That(check!.UpdateAvailable, Is.False, "1.1 is 1.1.0, the pin");
    }

    [Test]
    public async Task InstallerLatest_IsThePinnedVersionsInstaller()
    {
        using var http = _server.CreateClient();
        using var response = await http.GetAsync($"api/v1/installer/{PackageId}/latest/windows/x64/online");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("setup 1.1.0"));
    }

    private async Task PublishAsync(string version)
    {
        using var scope = _server.Services.CreateScope();
        var upload = scope.ServiceProvider.GetRequiredService<UploadService>();
        var session = await upload.StartSessionAsync(PackageId, version, "stable", TargetOS.Windows, Instella.Server.Models.Architecture.X64);
        var app = Encoding.UTF8.GetBytes("app " + version);
        await upload.UploadFileAsync(session.Id, "App.exe", Sha(app), new MemoryStream(app));
        var setup = Encoding.UTF8.GetBytes("setup " + version);
        await upload.UploadInstallerAsync(session.Id, "online", $"Setup-{version}.exe", Sha(setup), new MemoryStream(setup));
        await upload.CompleteSessionAsync(session.Id, null);
    }

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
