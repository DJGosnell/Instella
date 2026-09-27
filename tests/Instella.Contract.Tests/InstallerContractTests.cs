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
/// Installers published with a build: the CLI uploads them and lists them in the signed
/// release; the server serves them by version and as <c>latest</c>, and lists them with the
/// versions. The latest link never falls back to an older version's installer.
/// </summary>
[TestFixture]
public class InstallerContractTests
{
    private const string PackageId = "com.instella.installers";
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
        _work = Directory.CreateTempSubdirectory("instella-installer-contract-").FullName;
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
    public async Task SignedUpload_WithInstallers_SignsThemAndServesThemByVersionAndAsLatest()
    {
        var online = WriteFile("QuickNotes-WebSetup-1.0.0.exe", "online installer v1");
        var offline = WriteFile("QuickNotes-Setup-1.0.0.exe", "offline installer v1, with the app inside");
        Assert.That((await Upload(new Version(1, 0, 0), [new(InstallerKinds.Online, online), new(InstallerKinds.Offline, offline)])).Success, Is.True);

        using var http = _server.CreateClient();
        var server = new Uri(_server.BaseUrl);

        var signed = await http.GetFromJsonAsync(ApiRoutes.ForRelease(server, PackageId, new Version(1, 0, 0), TargetPlatform.Windows, Architecture.X64),
            WireJsonContext.Default.SignedRelease);
        var release = ReleaseVerifier.Verify(signed!, new TrustPolicy([_publicKey], PackageId, "windows", "x64", null, new Version(1, 0, 0)));
        Assert.That(release.Installers!.Select(i => (i.Kind, i.FileName, i.Sha256)), Is.EquivalentTo(new[]
        {
            (InstallerKinds.Online, "QuickNotes-WebSetup-1.0.0.exe", Sha(online)),
            (InstallerKinds.Offline, "QuickNotes-Setup-1.0.0.exe", Sha(offline)),
        }), "the signed release lists both installers");

        using (var byVersion = await http.GetAsync(ApiRoutes.ForDownloadInstaller(server, PackageId, new Version(1, 0, 0),
                   TargetPlatform.Windows, Architecture.X64, InstallerKinds.Offline)))
        {
            Assert.That(byVersion.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(await byVersion.Content.ReadAsByteArrayAsync(), Is.EqualTo(File.ReadAllBytes(offline)));
            Assert.That(byVersion.Content.Headers.ContentDisposition?.FileName?.Trim('"'), Is.EqualTo("QuickNotes-Setup-1.0.0.exe"));
        }

        var online2 = WriteFile("QuickNotes-WebSetup-2.0.0.exe", "online installer v2");
        Assert.That((await Upload(new Version(2, 0, 0), [new(InstallerKinds.Online, online2)])).Success, Is.True);
        using (var latest = await http.GetAsync(ApiRoutes.ForLatestInstaller(server, PackageId, TargetPlatform.Windows,
                   Architecture.X64, InstallerKinds.Online)))
            Assert.That(await latest.Content.ReadAsByteArrayAsync(), Is.EqualTo(File.ReadAllBytes(online2)), "latest is v2");

        using (var noOffline = await http.GetAsync(ApiRoutes.ForLatestInstaller(server, PackageId, TargetPlatform.Windows,
                   Architecture.X64, InstallerKinds.Offline)))
            Assert.That(noOffline.StatusCode, Is.EqualTo(HttpStatusCode.NotFound),
                "v2 has no offline installer; v1's is never handed out as the latest");

        var versions = await http.GetFromJsonAsync(ApiRoutes.ForPackageVersions(server, PackageId), WireJsonContext.Default.VersionSummaryArray);
        var v1 = versions!.Single(v => v.VersionString == "1.0.0").Builds.Single();
        Assert.That(v1.Installers.Select(i => (i.Kind, i.FileName, i.Size)), Is.EquivalentTo(new[]
        {
            (InstallerKinds.Offline, "QuickNotes-Setup-1.0.0.exe", new FileInfo(offline).Length),
            (InstallerKinds.Online, "QuickNotes-WebSetup-1.0.0.exe", new FileInfo(online).Length),
        }));
    }

    [Test]
    public async Task Latest_SkipsDeprecatedVersions_AndHonoursTheChannel()
    {
        var stable = WriteFile("App-1.0.0.exe", "stable");
        var beta = WriteFile("App-2.0.0-beta.exe", "beta");
        var broken = WriteFile("App-1.1.0.exe", "deprecated");
        Assert.That((await Upload(new Version(1, 0, 0), [new(InstallerKinds.Online, stable)])).Success, Is.True);
        Assert.That((await Upload(new Version(2, 0, 0), [new(InstallerKinds.Online, beta)], channel: "beta")).Success, Is.True);
        Assert.That((await Upload(new Version(1, 1, 0), [new(InstallerKinds.Online, broken)])).Success, Is.True);
        await _server.QueryAsync(async db =>
        {
            var v = await db.PackageVersions.SingleAsync(x => x.VersionString == "1.1.0");
            v.IsDeprecated = true;
            return await db.SaveChangesAsync();
        });

        using var http = _server.CreateClient();
        var server = new Uri(_server.BaseUrl);
        Assert.That(await http.GetByteArrayAsync(ApiRoutes.ForLatestInstaller(server, PackageId, TargetPlatform.Windows, Architecture.X64, InstallerKinds.Online)),
            Is.EqualTo(File.ReadAllBytes(stable)), "stable by default; the deprecated 1.1.0 is skipped");
        Assert.That(await http.GetByteArrayAsync(ApiRoutes.ForLatestInstaller(server, PackageId, TargetPlatform.Windows, Architecture.X64, InstallerKinds.Online, "beta")),
            Is.EqualTo(File.ReadAllBytes(beta)));
    }

    [Test]
    public async Task InstallerWithAnUnsafeFileName_IsRefusedBeforeUpload()
    {
        var bad = WriteFile("setup;rm -rf.exe", "x");
        var result = await Upload(new Version(1, 0, 0), [new(InstallerKinds.Online, bad)]);
        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Does.Contain("Invalid installer file name"));
        Assert.That(await _server.QueryAsync(db => db.VersionBuilds.CountAsync()), Is.Zero);
    }

    [Test]
    public async Task ExternallySignedUpload_LikeAKmsSigningTheDigest_IsAcceptedAndVerifies()
    {
        // What --sign-command does: the signer only sees SHA-256(message) and answers in DER.
        var dir = Path.Combine(_work, "kms");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "App.exe"), "app");
        using var client = new UploadClient(_server.CreateClient(), _server.BaseUrl, _apiKey);
        var result = await client.UploadVersionAsync(new UploadRequest
        {
            PackageId = PackageId, Version = new Version(5, 0, 0), SourceDirectory = dir, Channel = "stable",
            Platform = TargetPlatform.Windows, Architecture = Architecture.X64,
            SignWith = (manifest, _) =>
            {
                var digest = SHA256.HashData(ReleaseSigner.MessageFor(manifest));
                var der = _signingKey.SignHash(digest, DSASignatureFormat.Rfc3279DerSequence);
                var raw = CommandSigningKey.ParseSignature(Convert.ToBase64String(der));
                return Task.FromResult(new SignedRelease(Convert.ToBase64String(manifest), Convert.ToBase64String(raw), _publicKey.KeyId));
            },
        });
        Assert.That(result.Success, Is.True, result.Error);

        using var http = _server.CreateClient();
        var signed = await http.GetFromJsonAsync(ApiRoutes.ForRelease(new Uri(_server.BaseUrl), PackageId, new Version(5, 0, 0),
            TargetPlatform.Windows, Architecture.X64), WireJsonContext.Default.SignedRelease);
        Assert.DoesNotThrow(() => ReleaseVerifier.Verify(signed!, new TrustPolicy([_publicKey], PackageId, "windows", "x64", null, new Version(5, 0, 0))));
    }

    private async Task<UploadResult> Upload(Version version, IReadOnlyList<InstallerUpload> installers, string channel = "stable")
    {
        var dir = Path.Combine(_work, "build-" + version + "-" + channel);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "App.exe"), "app " + version);
        using var client = new UploadClient(_server.CreateClient(), _server.BaseUrl, _apiKey);
        return await client.UploadVersionAsync(new UploadRequest
        {
            PackageId = PackageId, Version = version, SourceDirectory = dir, Channel = channel,
            Platform = TargetPlatform.Windows, Architecture = Architecture.X64, SigningKey = _signingKey,
            Installers = installers,
        });
    }

    private string WriteFile(string name, string content)
    {
        var dir = Path.Combine(_work, "installers", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static string Sha(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
}
