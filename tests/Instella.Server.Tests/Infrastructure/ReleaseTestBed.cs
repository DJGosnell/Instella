using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Instella.Core.Trust;
using Instella.Server.Data.Entities;
using Instella.Server.Models;
using Instella.Server.Services;
using Instella.Server.Tests.Integration;
using Microsoft.EntityFrameworkCore;

namespace Instella.Server.Tests.Infrastructure;

/// <summary>
/// A package with a registered publisher key and helpers for signed, draft and key-owned uploads,
/// for the release approval tests.
/// </summary>
public sealed class ReleaseTestBed : IDisposable
{
    public const string PackageId = "com.test.app";

    public ServerTestFixture Fixture { get; } = new();
    public ECDsa Key { get; } = ReleaseKeys.Generate();
    public Package Package { get; private set; } = null!;
    public ApiKey Uploader { get; private set; } = null!;

    public static async Task<ReleaseTestBed> CreateAsync(ReleaseApproval approval = ReleaseApproval.Automatic, bool registerKey = true)
    {
        var bed = new ReleaseTestBed();
        bed.Package = await bed.Fixture.SeedPackageAsync(PackageId);
        if (registerKey)
            await bed.Fixture.PackageService.AddPublisherKeyAsync(bed.Package.Id, ReleaseKeys.PublicKeyOf(bed.Key).PublicKey, "ci");
        (bed.Uploader, _) = await bed.Fixture.AuthService.CreateApiKeyAsync("ci-upload", ApiKeyScope.Package, canUpload: true,
            packageId: bed.Package.Id);
        if (approval != ReleaseApproval.Automatic)
            await bed.Fixture.ReleaseApprovals.SetReleaseApprovalAsync(bed.Package.Id, approval, ReleaseApprovalService.DefaultDelayMinutes, "setup");
        bed.Fixture.Db.ChangeTracker.Clear();
        return bed;
    }

    /// <summary>Uploads a signed release of <paramref name="version"/> by <see cref="Uploader"/>; returns the build.</summary>
    public async Task<VersionBuild> UploadSignedAsync(string version, TargetOS os = TargetOS.Windows, ECDsa? key = null)
    {
        var session = await StartAsync(version, os);
        await Fixture.UploadService.CompleteSessionAsync(session, null, ReleaseSigner.Sign(Manifest(version, os), key ?? Key));
        return await BuildAsync(version, os);
    }

    /// <summary>Uploads <paramref name="version"/> as an unsigned draft; returns the build.</summary>
    public async Task<VersionBuild> UploadDraftAsync(string version, TargetOS os = TargetOS.Windows)
    {
        var session = await StartAsync(version, os);
        await Fixture.UploadService.CompleteDraftSessionAsync(session, null,
            JsonSerializer.SerializeToUtf8Bytes(Manifest(version, os), TrustJsonContext.Default.ReleaseManifest));
        return await BuildAsync(version, os);
    }

    /// <summary>Signs the stored draft manifest of <paramref name="build"/> the way <c>instella publish</c> does.</summary>
    public Task<DraftPublishResult?> SignDraftAsync(VersionBuild build) =>
        Fixture.UploadService.PublishDraftAsync(PackageId, build.Version.VersionString, build.OS, build.Architecture,
            ReleaseSigner.Sign(build.ReleaseManifestBytes, Key));

    public async Task<VersionBuild> BuildAsync(string version, TargetOS os = TargetOS.Windows)
    {
        Fixture.Db.ChangeTracker.Clear();
        return await Fixture.Db.VersionBuilds.AsNoTracking().Include(b => b.Version)
            .SingleAsync(b => b.Version.VersionString == version && b.OS == os);
    }

    public static string Hash(VersionBuild build) => ReleaseApprovalService.ManifestSha256(build.ReleaseManifestBytes!);

    public Task<List<SecurityEvent>> EventsAsync(SecurityEventType type)
    {
        Fixture.Db.ChangeTracker.Clear();
        return Fixture.Db.SecurityEvents.AsNoTracking().Where(e => e.EventType == type).OrderBy(e => e.Id).ToListAsync();
    }

    public async Task<string?> LatestAsync(TargetOS os = TargetOS.Windows) =>
        (await Fixture.PackageService.GetLatestVersionAsync(Package.Id, "stable", os, Architecture.X64))?.VersionString;

    private async Task<Guid> StartAsync(string version, TargetOS os)
    {
        var session = await Fixture.UploadService.StartSessionAsync(PackageId, version, "stable", os, Architecture.X64, Uploader.Id);
        var bytes = Content(version, os);
        await Fixture.UploadService.UploadFileAsync(session.Id, "app.bin", Sha(bytes), new MemoryStream(bytes));
        return session.Id;
    }

    private static ReleaseManifest Manifest(string version, TargetOS os)
    {
        var bytes = Content(version, os);
        return new ReleaseManifest
        {
            FormatVersion = ReleaseManifest.CurrentFormatVersion,
            AppId = PackageId,
            Version = Version.Parse(version),
            Os = PlatformMapping.ToWire(os),
            Arch = "x64",
            Channel = "stable",
            CreatedAt = DateTimeOffset.UtcNow,
            Files = [new ReleaseFile("app.bin", bytes.Length, Sha(bytes))],
        };
    }

    private static byte[] Content(string version, TargetOS os) => Encoding.UTF8.GetBytes($"app {version} {os}");

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public void Dispose()
    {
        Key.Dispose();
        Fixture.Dispose();
    }
}
