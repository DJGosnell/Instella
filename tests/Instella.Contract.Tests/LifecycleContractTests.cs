using System.IO.Compression;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Instella.CLI.Services;
using Instella.Core.Diff;
using Instella.Core.FileSystem;
using Instella.Core.Platform;
using Instella.Core.Trust;
using Instella.Core.Wire;
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Runtime.Core;
using Instella.Installer.Runtime.Core.Update;
using Instella.Installer.Runtime.Runners;
using Instella.Sdk.Internal;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Instella.Core.Installation;
using Instella.Core.Update;

namespace Instella.Contract.Tests;

/// <summary>
/// Walks the wire protocol end to end with the real clients against the real server:
/// signed upload v1 → signed upload v2 → patch job → check-update (with embedded signed
/// release) → release endpoint → patch manifest + archive → full build → single file →
/// verified lite-installer download → list → delete.
/// </summary>
[TestFixture]
public class LifecycleContractTests
{
    private const string PackageId = "com.instella.contract";
    private static readonly Version V1 = new(1, 0, 0);
    private static readonly Version V2 = new(1, 1, 0);

    private ContractServer _server = null!;
    private string _apiKey = null!;
    private string _work = null!;
    private ECDsa _signingKey = null!;
    private PublisherKey _publicKey = null!;

    [OneTimeSetUp]
    public async Task SetUp()
    {
        _server = new ContractServer();
        _apiKey = await _server.SeedPackageAndKeyAsync(PackageId);
        _work = Directory.CreateTempSubdirectory("instella-contract-work-").FullName;
        _signingKey = ReleaseKeys.Generate();
        _publicKey = ReleaseKeys.PublicKeyOf(_signingKey);
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        _signingKey.Dispose();
        _server.Dispose();
        Directory.Delete(_work, recursive: true);
    }

    private TrustPolicy Policy(Version? newerThan = null, Version? equal = null) =>
        new([_publicKey], PackageId, "windows", "x64", newerThan, equal);

    [Test]
    public async Task FullProtocolWalk()
    {
        // --- Build two versions on disk ---------------------------------------------------
        var rng = new Random(1234);
        var exeV1 = new byte[96 * 1024];
        rng.NextBytes(exeV1);
        var exeV2 = (byte[])exeV1.Clone();
        for (var i = 0; i < 64; i++) exeV2[1000 + i * 997] ^= 0x5A;   // a small, patchable change
        var sharedDll = new byte[8 * 1024];
        rng.NextBytes(sharedDll);

        var dirV1 = WriteTree("v1", new()
        {
            ["App.exe"] = exeV1,
            ["lib/Shared.dll"] = sharedDll,
            ["old notes.txt"] = "removed in v2"u8.ToArray(),
        });
        var dirV2 = WriteTree("v2", new()
        {
            ["App.exe"] = exeV2,
            ["lib/Shared.dll"] = sharedDll,
            ["new notes.txt"] = "added in v2"u8.ToArray(),
        });

        // --- Signed uploads with the CLI client --------------------------------------------
        foreach (var (version, dir) in new[] { (V1, dirV1), (V2, dirV2) })
        {
            var result = await UploadAsync(version, dir, _signingKey);
            Assert.That(result.Success, Is.True, result.Error);
        }

        // --- Wait for the background patch job --------------------------------------------
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (await _server.QueryAsync(db => db.BuildPatches.CountAsync()) == 0)
        {
            Assert.That(DateTime.UtcNow, Is.LessThan(deadline), "patch job did not complete within 60 s");
            await Task.Delay(250);
        }

        // --- SDK check-update embeds the signed release of the target version -------------
        var sdk = new HttpUpdateClient(_server.CreateClient());
        var check = await sdk.CheckAsync(SdkInfo(V1), "stable", CancellationToken.None);
        Assert.That(check.Error, Is.Null);
        Assert.That(check.UpdateAvailable, Is.True);
        Assert.That(check.Update!.Version, Is.EqualTo(V2));
        Assert.That(check.Update.PatchAvailable, Is.True);
        Assert.That(check.Update.PatchSha256, Has.Length.EqualTo(64));

        using (var http = _server.CreateClient())
        {
            var raw = await http.GetFromJsonAsync(
                ApiRoutes.ForCheckUpdate(new Uri(_server.BaseUrl), PackageId, V1, TargetPlatform.Windows, Architecture.X64, "stable"),
                WireJsonContext.Default.CheckUpdateResponse);
            var embedded = ReleaseVerifier.Verify(raw!.Release!, Policy(newerThan: V1));
            Assert.That(embedded.Version, Is.EqualTo(V2));
            Assert.That(embedded.Files.Select(f => f.Path), Is.EquivalentTo(new[] { "App.exe", "lib/Shared.dll", "new notes.txt" }));

            // Replay protection: the same signed release is refused as an "update" to itself.
            Assert.Throws<UpdateTrustException>(() => ReleaseVerifier.Verify(raw.Release!, Policy(newerThan: V2)));

            // The release endpoint relays the exact signed bytes.
            var fromEndpoint = await http.GetFromJsonAsync(
                ApiRoutes.ForRelease(new Uri(_server.BaseUrl), PackageId, V2, TargetPlatform.Windows, Architecture.X64),
                WireJsonContext.Default.SignedRelease);
            Assert.That(fromEndpoint, Is.EqualTo(raw.Release));
        }

        var upToDate = await sdk.CheckAsync(SdkInfo(V2), "stable", CancellationToken.None);
        Assert.That(upToDate.UpdateAvailable, Is.False);
        Assert.That(upToDate.Error, Is.Null);

        // --- Updater: patch manifest, patch archive entries, single files, full build -----
        using (var downloader = new HttpUpdateDownloader(
                   _server.CreateClient(), ownsHttp: true, _server.BaseUrl, PackageId, V1, V2,
                   TargetPlatform.Windows, Architecture.X64))
        {
            var manifest = (await downloader.DownloadPatchManifestAsync(CancellationToken.None))!;
            Assert.That(manifest.FromVersion, Is.EqualTo(V1));
            Assert.That(manifest.ToVersion, Is.EqualTo(V2));
            Assert.That(manifest.PatchedFiles.Select(f => f.RelativePath), Is.EquivalentTo(new[] { "App.exe" }));
            Assert.That(manifest.NewFiles.Select(f => f.RelativePath), Is.EquivalentTo(new[] { "new notes.txt" }));
            Assert.That(manifest.DeletedFiles, Is.EquivalentTo(new[] { "old notes.txt" }));

            await using (var patch = await downloader.OpenPatchEntryAsync(manifest.PatchedFiles[0].PatchSha256, long.MaxValue, CancellationToken.None))
            {
                using var output = new MemoryStream();
                await BsDiffEngine.Instance.ApplyPatchAsync(new MemoryStream(exeV1), patch, output);
                Assert.That(output.ToArray(), Is.EqualTo(exeV2), "patch from the archive must reproduce v2");
            }

            await using (var file = await downloader.DownloadFileAsync("new notes.txt", CancellationToken.None))
                Assert.That(ReadAll(file), Is.EqualTo("added in v2"u8.ToArray()));

            await using (var file = await downloader.DownloadFileAsync("lib/Shared.dll", CancellationToken.None))
                Assert.That(ReadAll(file), Is.EqualTo(sharedDll));

            await using (var full = await downloader.DownloadFullBuildAsync(CancellationToken.None))
            {
                using var zip = new ZipArchive(new MemoryStream(ReadAll(full)), ZipArchiveMode.Read);
                Assert.That(zip.Entries.Select(e => e.FullName),
                    Is.EquivalentTo(new[] { "App.exe", "lib/Shared.dll", "new notes.txt" }));
            }
        }

        // --- Update engine: a v1 installation on disk is patched to v2 through the server --
        var appDir = InstallV1(dirV1);
        using (var downloader = new HttpUpdateDownloader(
                   _server.CreateClient(), ownsHttp: true, _server.BaseUrl, PackageId, V1, V2,
                   TargetPlatform.Windows, Architecture.X64))
        {
            var engine = new UpdaterEngine(new UpdaterArgs
            {
                AppPath = appDir, AppExecutable = "App.exe",
                FromVersion = V1, ToVersion = V2, UsePatch = true, Restart = false, GracefulTimeout = TimeSpan.Zero,
            }, downloader, new NoOpPlatformServices(TargetPlatform.Windows), RealFileSystem.Instance, BsDiffEngine.Instance);
            var updated = await engine.RunAsync(CancellationToken.None);
            Assert.That(updated.Success, Is.True, updated.Error);
        }
        Assert.That(File.ReadAllBytes(Path.Combine(appDir, "App.exe")), Is.EqualTo(exeV2));
        Assert.That(File.Exists(Path.Combine(appDir, "old notes.txt")), Is.False);
        Assert.That(File.ReadAllText(Path.Combine(appDir, "new notes.txt")), Is.EqualTo("added in v2"));
        Assert.That(Directory.Exists(Path.Combine(appDir, ".instella", "txn"))
                    && Directory.EnumerateFileSystemEntries(Path.Combine(appDir, ".instella", "txn")).Any(), Is.False);
        var installedAfter = await new InstallManifestWriter(RealFileSystem.Instance).ReadAsync(appDir, CancellationToken.None);
        Assert.That(installedAfter!.Version, Is.EqualTo(V2));

        // --- Lite installer: signed release pinned to V2, every file verified -------------
        await using (var lite = await ServerPayloadDownloader.DownloadAsync(
                         LiteConfig(), Path.Combine(_work, "lite.zip"), new NullLogger(), _server.CreateClient(), CancellationToken.None))
        {
            Assert.That(lite.Length, Is.GreaterThan(0));
        }

        // --- CLI list ---------------------------------------------------------------------
        using (var api = new ApiClient(_server.CreateClient(), _server.BaseUrl, _apiKey))
        {
            var packages = await api.ListPackagesAsync();
            Assert.That(packages.IsSuccess, Is.True, packages.Error);
            Assert.That(packages.Data!.Single(p => p.PackageId == PackageId).VersionCount, Is.EqualTo(2));

            var versions = await api.ListVersionsAsync(PackageId);
            Assert.That(versions.IsSuccess, Is.True, versions.Error);
            Assert.That(versions.Data!.Select(v => v.VersionString), Is.EquivalentTo(new[] { "1.0.0", "1.1.0" }));
            Assert.That(versions.Data!.SelectMany(v => v.Builds).Select(b => (b.Os, b.Arch)).Distinct(),
                Is.EquivalentTo(new[] { ("windows", "x64") }));
        }

        // --- Tamper: a modified blob on the server is caught by the lite installer --------
        var dllHash = Convert.ToHexStringLower(SHA256.HashData(sharedDll));
        var blob = _server.BlobPath(dllHash);
        var tampered = File.ReadAllBytes(blob);
        tampered[0] ^= 0xFF;
        File.WriteAllBytes(blob, tampered);

        var ex = Assert.ThrowsAsync<UpdateTrustException>(() => ServerPayloadDownloader.DownloadAsync(
            LiteConfig(), Path.Combine(_work, "lite-tampered.zip"), new NullLogger(), _server.CreateClient(), CancellationToken.None));
        Assert.That(ex!.Message, Does.Contain("lib/Shared.dll"));
    }

    [Test]
    public async Task SignedUpload_StoresTheSignedReleaseOnTheBuild()
    {
        var dir = WriteTree("other-platform", new() { ["a.txt"] = "a"u8.ToArray() });
        using var client = new UploadClient(_server.CreateClient(), _server.BaseUrl, _apiKey);

        var result = await client.UploadVersionAsync(new UploadRequest
        {
            PackageId = PackageId,
            Version = new Version(9, 0, 0),
            SourceDirectory = dir,
            Channel = "beta",
            Platform = TargetPlatform.Linux,
            Architecture = Architecture.ARM64,
            SigningKey = _signingKey,
        });
        Assert.That(result.Success, Is.True, "a consistent signed upload succeeds: " + result.Error);

        var build = await _server.QueryAsync(db => db.VersionBuilds.SingleAsync(b => b.Version.VersionString == "9.0.0"));
        Assert.That(build.ReleaseKeyId, Is.EqualTo(_publicKey.KeyId));
        Assert.That(build.ReleaseManifestBytes, Is.Not.Null);
    }

    [Test]
    public async Task RegisteredPublisherKey_RejectsUnsignedAndForeignSignedUploads()
    {
        const string strictPackage = "com.instella.strict";
        var key = await _server.SeedPackageAndKeyAsync(strictPackage);
        await _server.QueryAsync(async db =>
        {
            var pkg = await db.Packages.SingleAsync(p => p.PackageId == strictPackage);
            db.PackagePublisherKeys.Add(new() { PackageId = pkg.Id, KeyId = _publicKey.KeyId, PublicKey = _publicKey.PublicKey });
            return await db.SaveChangesAsync();
        });

        var dir = WriteTree("strict", new() { ["a.txt"] = "a"u8.ToArray() });
        using var foreign = ReleaseKeys.Generate();

        foreach (var signingKey in new ECDsa?[] { null, foreign })
        {
            using var client = new UploadClient(_server.CreateClient(), _server.BaseUrl, key);
            var result = await client.UploadVersionAsync(new UploadRequest
            {
                PackageId = strictPackage, Version = V1, SourceDirectory = dir, Channel = "stable",
                Platform = TargetPlatform.Windows, Architecture = Architecture.X64, SigningKey = signingKey,
            });
            Assert.That(result.Success, Is.False, signingKey is null ? "unsigned" : "foreign key");
        }

        using (var client = new UploadClient(_server.CreateClient(), _server.BaseUrl, key))
        {
            var ok = await client.UploadVersionAsync(new UploadRequest
            {
                PackageId = strictPackage, Version = V1, SourceDirectory = dir, Channel = "stable",
                Platform = TargetPlatform.Windows, Architecture = Architecture.X64, SigningKey = _signingKey,
            });
            Assert.That(ok.Success, Is.True, ok.Error);
        }
    }

    [Test]
    public async Task DeleteVersion_RouteIsReachableWithUploadKey()
    {
        // Pins the routing contract only: the CLI's URL reaches the server's DeleteVersion action
        // and an unknown version is a 404 with an ApiError body, not a routing miss.
        using var api = new ApiClient(_server.CreateClient(), _server.BaseUrl, _apiKey);
        var result = await api.DeleteVersionAsync(PackageId, "9.9.9");
        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Error, Is.EqualTo("Version not found"));
    }

    /// <summary>What the SDK reads from an installed manifest, as the check client sees it.</summary>
    private Instella.Sdk.InstellaInfo SdkInfo(Version installed) => new()
    {
        AppName = "Contract", AppId = PackageId, Version = installed, InstallRoot = _work,
        ServerUrl = _server.BaseUrl, Architecture = Architecture.X64, Platform = TargetPlatform.Windows,
        TrustedKeys = [_publicKey],
    };

    private FrozenConfig LiteConfig() => ((InstellaInstallerImpl)InstellaInstaller.Create()
        .WithApp("Contract", PackageId, V2)
        .WithServer(_server.BaseUrl)
        .WithPublisherKey(_publicKey.PublicKey)
        .Build()).ConfigForTests;

    private async Task<UploadResult> UploadAsync(Version version, string dir, ECDsa? key)
    {
        using var upload = new UploadClient(_server.CreateClient(), _server.BaseUrl, _apiKey);
        return await upload.UploadVersionAsync(new UploadRequest
        {
            PackageId = PackageId,
            Version = version,
            SourceDirectory = dir,
            Channel = "stable",
            Platform = TargetPlatform.Windows,
            Architecture = Architecture.X64,
            Changelog = $"release {version}",
            SigningKey = key,
        });
    }

    private string WriteTree(string name, Dictionary<string, byte[]> files)
    {
        var root = Path.Combine(_work, name);
        foreach (var (rel, content) in files)
        {
            var path = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
        }
        return root;
    }

    /// <summary>Lays out a v1 installation of <paramref name="source"/>: files, stub and an installed manifest.</summary>
    private string InstallV1(string source)
    {
        var appDir = Path.Combine(_work, "installed");
        var files = new List<InstalledFile>();
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, file).Replace('\\', '/');
            var dest = Path.Combine(appDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest);
            var bytes = File.ReadAllBytes(file);
            files.Add(new InstalledFile(rel, Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length));
        }
        File.WriteAllText(Path.Combine(appDir, "instella.exe"), "stub");

        var manifest = new InstalledManifest
        {
            AppName = "Contract", AppId = PackageId, Version = V1, InstallDirectory = appDir,
            ExecutableName = "App.exe", InstalledAt = DateTime.UtcNow, Platform = TargetPlatform.Windows,
            Architecture = Architecture.X64, ServerUrl = _server.BaseUrl, TrustedKeys = [_publicKey], Files = files,
        };
        new InstallManifestWriter(RealFileSystem.Instance).WriteAsync(appDir, manifest, CancellationToken.None).GetAwaiter().GetResult();
        return appDir;
    }

    private static byte[] ReadAll(Stream s)
    {
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private sealed class NullLogger : Instella.Core.Logging.IInstellaLogger
    {
        public void Trace(string message) { }
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
        public IDisposable Scope(string segment) => new MemoryStream();
        public bool IsEnabled(Instella.Core.Logging.InstellaLogLevel level) => false;
    }
}
