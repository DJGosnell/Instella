using System.Security.Cryptography;
using System.Text;
using Bunit;
using Instella.Core.Wire;
using Instella.Server.Components;
using Instella.Server.Data.Entities;
using Instella.Server.Models;
using Instella.Server.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Instella.Server.Tests.Integration;

/// <summary>
/// The public download page: open packages only, no sign-in, newest first, deprecated versions
/// hidden, and the newest version per platform linked through <c>latest</c>.
/// </summary>
[TestFixture]
public sealed class DownloadPageTests
{
    private ServerTestFixture _fixture = null!;

    [SetUp]
    public async Task SetUp()
    {
        _fixture = new ServerTestFixture();
        await _fixture.SeedPackageAsync("com.test.app", "Quick Notes");
    }

    [TearDown]
    public void TearDown() => _fixture.Dispose();

    [Test]
    public async Task Model_ListsVersionsWithInstallers_NewestFirst_LatestLinksForTheNewest()
    {
        await Publish("1.0.0", (InstallerKinds.Online, "Web-1.0.0.exe"), (InstallerKinds.Offline, "Setup-1.0.0.exe"));
        await Publish("1.1.0", (InstallerKinds.Online, "Web-1.1.0.exe"));
        await Publish("1.2.0", (InstallerKinds.Online, "Web-1.2.0.exe"));
        await Publish("1.3.0");   // no installers: not listed
        await Deprecate("1.2.0");

        var model = await new DownloadPageService(_fixture.Db).GetAsync("com.test.app", "stable");

        Assert.That(model!.Versions.Select(v => v.Version), Is.EqualTo(new[] { "1.1.0", "1.0.0" }),
            "deprecated 1.2.0 and installer-less 1.3.0 are not offered");
        // 1.3.0 is the newest build for windows/x64, so no listed version is "latest" there.
        Assert.That(model.Versions.SelectMany(v => v.Installers).Select(i => i.Url), Has.None.Contains("/latest/"));
        Assert.That(model.Versions[1].Installers.Single(i => i.Kind == InstallerKinds.Offline).Url,
            Is.EqualTo("/api/v1/installer/com.test.app/1.0.0/windows/x64/offline"));
    }

    [Test]
    public async Task Model_NewestVersionPerPlatform_UsesTheLatestLink()
    {
        await Publish("1.0.0", (InstallerKinds.Online, "Web-1.0.0.exe"));
        await Publish("2.0.0", (InstallerKinds.Online, "Web-2.0.0.exe"));

        var model = await new DownloadPageService(_fixture.Db).GetAsync("com.test.app", "stable");

        Assert.That(model!.Versions[0].Installers.Single().Url, Is.EqualTo("/api/v1/installer/com.test.app/latest/windows/x64/online"));
        Assert.That(model.Versions[1].Installers.Single().Url, Is.EqualTo("/api/v1/installer/com.test.app/1.0.0/windows/x64/online"));
    }

    [Test]
    public async Task Model_ACustomChannel_ListsItsVersions_WithChannelLinks()
    {
        // Channels are free names.
        await Publish("1.0.0", (InstallerKinds.Online, "Web-1.0.0.exe"));
        await PublishOn("rc", "1.1.0", (InstallerKinds.Online, "Web-1.1.0.exe"));

        var model = await new DownloadPageService(_fixture.Db).GetAsync("com.test.app", "RC");

        Assert.That(model!.Channel, Is.EqualTo("rc"));
        Assert.That(model.Versions.Select(v => v.Version), Is.EqualTo(new[] { "1.1.0" }));
        Assert.That(model.Versions[0].Installers.Single().Url,
            Is.EqualTo("/api/v1/installer/com.test.app/latest/windows/x64/online?channel=rc"));
    }

    [TestCase("nope")]
    [TestCase("not a channel")]
    public async Task Model_AChannelThePackageDoesNotHave_LooksLikeAnUnknownPackage(string channel)
    {
        await Publish("1.0.0", (InstallerKinds.Online, "Web-1.0.0.exe"));

        Assert.That(await new DownloadPageService(_fixture.Db).GetAsync("com.test.app", channel), Is.Null);
    }

    [Test]
    public async Task Model_PrivatePackage_HasNoPage()
    {
        await Publish("1.0.0", (InstallerKinds.Online, "Web-1.0.0.exe"));
        await _fixture.Db.Packages.ExecuteUpdateAsync(u => u.SetProperty(p => p.DownloadAccessMode, DownloadAccessMode.PackageKeyRequired));

        Assert.That(await new DownloadPageService(_fixture.Db).GetAsync("com.test.app", "stable"), Is.Null);
        Assert.That(await new DownloadPageService(_fixture.Db).GetAsync("com.unknown", "stable"), Is.Null);
    }

    [Test]
    public async Task Page_RendersForAnAnonymousVisitor_WithoutRedirectingToLogin()
    {
        await Publish("1.0.0", (InstallerKinds.Online, "Web-1.0.0.exe"));

        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddScoped(_ => new AuthService(_fixture.Db));
        ctx.Services.AddScoped(_ => new DownloadPageService(_fixture.Db));
        ctx.AddAuthorization();   // anonymous

        var navigation = ctx.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/download/com.test.app");
        var cut = ctx.Render<Routes>();

        cut.WaitForAssertion(() => Assert.That(cut.Find(".download-page h1").TextContent, Is.EqualTo("Quick Notes")));
        Assert.That(navigation.Uri, Does.Not.Contain("/login"));
        Assert.That(cut.Find("a[download='Web-1.0.0.exe']").GetAttribute("href"),
            Is.EqualTo("/api/v1/installer/com.test.app/latest/windows/x64/online"));
    }

    private Task Publish(string version, params (string Kind, string FileName)[] installers) =>
        PublishOn("stable", version, installers);

    private async Task PublishOn(string channel, string version, params (string Kind, string FileName)[] installers)
    {
        var session = await _fixture.UploadService.StartSessionAsync("com.test.app", version, channel, TargetOS.Windows, Architecture.X64);
        await _fixture.UploadService.UploadFileAsync(session.Id, "app.exe", Sha("app " + version), Stream("app " + version));
        foreach (var (kind, fileName) in installers)
            await _fixture.UploadService.UploadInstallerAsync(session.Id, kind, fileName, Sha(fileName), Stream(fileName));
        await _fixture.UploadService.CompleteSessionAsync(session.Id, "Changes in " + version);
        // Distinct release times, oldest first.
        await _fixture.Db.PackageVersions.Where(v => v.VersionString == version)
            .ExecuteUpdateAsync(u => u.SetProperty(v => v.ReleasedAt, DateTime.UtcNow.AddDays(Version.Parse(version).Minor)));
        _fixture.Db.ChangeTracker.Clear();
    }

    private Task Deprecate(string version) =>
        _fixture.Db.PackageVersions.Where(v => v.VersionString == version).ExecuteUpdateAsync(u => u.SetProperty(v => v.IsDeprecated, true));

    private static MemoryStream Stream(string s) => new(Encoding.UTF8.GetBytes(s));
    private static string Sha(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
}
