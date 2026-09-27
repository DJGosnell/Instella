using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Instella.Core.Platform;
using Instella.Core.Wire;
using Instella.Installer.Runtime.Core.Update;
using Instella.Sdk;
using Instella.Sdk.Internal;
using Instella.Server.Data;
using Instella.Server.Data.Entities;
using Instella.Server.Models;
using Instella.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Instella.Contract.Tests;

/// <summary>
/// An app installed from a PackageKeyRequired package keeps updating with the token its
/// installer was built with: check-update, the patch and the full download all carry it; a
/// revoked token looks like "no update".
/// </summary>
[TestFixture]
public sealed class DownloadTokenLifecycleContractTests
{
    private const string PackageId = "com.instella.private-app";
    private static readonly Version V1 = new(1, 0, 0);
    private static readonly Version V2 = new(1, 1, 0);

    private ContractServer _server = null!;
    private string _token = null!;
    private long _tokenId;
    private byte[] _v2 = null!;

    [OneTimeSetUp]
    public async Task SetUp()
    {
        _server = new ContractServer();
        await _server.SeedPackageAndKeyAsync(PackageId);
        using var scope = _server.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Packages.ExecuteUpdateAsync(u => u.SetProperty(p => p.DownloadAccessMode, DownloadAccessMode.PackageKeyRequired));
        var package = await db.Packages.SingleAsync(p => p.PackageId == PackageId);
        var (token, plain) = await scope.ServiceProvider.GetRequiredService<DownloadTokenService>().CreateAsync(package.Id, "installers", null);
        (_token, _tokenId) = (plain, token.Id);

        var v1 = new byte[64 * 1024];
        new Random(7).NextBytes(v1);
        _v2 = (byte[])v1.Clone();
        for (var i = 0; i < 32; i++) _v2[500 + i * 1009] ^= 0x5A;
        await PublishAsync(V1, v1, installer: false);
        await PublishAsync(V2, _v2, installer: true);

        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (await _server.QueryAsync(d => d.BuildPatches.CountAsync()) == 0)
        {
            Assert.That(DateTime.UtcNow, Is.LessThan(deadline), "patch job did not complete within 60 s");
            await Task.Delay(250);
        }
    }

    [OneTimeTearDown]
    public void TearDown() => _server.Dispose();

    [Test, Order(1)]
    public async Task WithTheToken_CheckPatchAndFullDownload_Work()
    {
        var check = await new HttpUpdateClient(_server.CreateClient()).CheckAsync(Info(_token), "stable", CancellationToken.None);
        Assert.That(check.Error, Is.Null);
        Assert.That(check.UpdateAvailable, Is.True);
        Assert.That(check.Update!.PatchAvailable, Is.True);

        using var downloader = new HttpUpdateDownloader(
            ServerHttp.Create(new Uri(_server.BaseUrl), _token, "Instella-Updater/1.0", TimeSpan.FromMinutes(1), _server.Server.CreateHandler()),
            ownsHttp: true, _server.BaseUrl, PackageId, V1, V2, TargetPlatform.Windows, Instella.Core.Platform.Architecture.X64,
            check.Update.PatchSha256);
        var manifest = await downloader.DownloadPatchManifestAsync(CancellationToken.None);
        Assert.That(manifest!.PatchedFiles.Select(f => f.RelativePath), Is.EqualTo(new[] { "app.bin" }));
        await using var entry = await downloader.OpenPatchEntryAsync(manifest.PatchedFiles[0].PatchSha256, 1024 * 1024, CancellationToken.None);
        Assert.That(entry.ReadByte(), Is.Not.EqualTo(-1), "the patch archive downloads");
        await using var file = await downloader.DownloadFileAsync("app.bin", CancellationToken.None);
        using var copy = new MemoryStream();
        await file.CopyToAsync(copy);
        Assert.That(copy.ToArray(), Is.EqualTo(_v2), "the full file downloads");
    }

    [Test, Order(2)]
    public async Task WithoutTheToken_ThereIsNoUpdate()
    {
        var check = await new HttpUpdateClient(_server.CreateClient()).CheckAsync(Info(null), "stable", CancellationToken.None);

        Assert.That(check.UpdateAvailable, Is.False);
        Assert.That(check.Error, Is.Null, "a private package answers like an unknown one (8.9)");
    }

    [Test, Order(3)]
    public async Task InstallerLatest_WorksWithTheToken()
    {
        using var http = _server.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);

        using var response = await http.GetAsync($"{ApiRoutes.Prefix}/installer/{PackageId}/latest/windows/x64/online");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test, Order(4)]
    public async Task ARevokedToken_SeesNoUpdate()
    {
        using (var scope = _server.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<DownloadTokenService>().RevokeAsync(_tokenId);

        var check = await new HttpUpdateClient(_server.CreateClient()).CheckAsync(Info(_token), "stable", CancellationToken.None);

        Assert.That(check.UpdateAvailable, Is.False);
    }

    private InstellaInfo Info(string? token) => new()
    {
        AppName = "Private App", AppId = PackageId, Version = V1, InstallRoot = Path.GetTempPath(),
        ServerUrl = _server.BaseUrl, Platform = TargetPlatform.Windows, Architecture = Instella.Core.Platform.Architecture.X64,
        AllowUnsignedUpdates = true, DownloadToken = token,
    };

    private async Task PublishAsync(Version version, byte[] content, bool installer)
    {
        using var scope = _server.Services.CreateScope();
        var upload = scope.ServiceProvider.GetRequiredService<UploadService>();
        var session = await upload.StartSessionAsync(PackageId, version.ToString(), "stable", TargetOS.Windows,
            Instella.Server.Models.Architecture.X64);
        await upload.UploadFileAsync(session.Id, "app.bin", Convert.ToHexStringLower(SHA256.HashData(content)), new MemoryStream(content));
        if (installer)
        {
            var setup = "setup"u8.ToArray();
            await upload.UploadInstallerAsync(session.Id, "online", "Setup.exe", Convert.ToHexStringLower(SHA256.HashData(setup)), new MemoryStream(setup));
        }
        await upload.CompleteSessionAsync(session.Id, null);
    }
}
