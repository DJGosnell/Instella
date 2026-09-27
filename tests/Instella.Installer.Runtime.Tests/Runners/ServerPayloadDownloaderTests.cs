using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Logging;
using Instella.Core.Platform;
using Instella.Core.Trust;
using Instella.Core.Wire;
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Runtime.Runners;
using Instella.Installer.Testing;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Runners;

/// <summary>The lite installer's payload download is bounded by the verified release.</summary>
[TestFixture]
public class ServerPayloadDownloaderTests
{
    [Test]
    public async Task OversizedPayload_FailsAsATrustError_AndDeletesThePartialFile()
    {
        using var key = ReleaseKeys.Generate();
        var platform = PlatformDetector.Current;
        var arch = ArchitectureExtensions.Current;
        var release = new ReleaseManifest
        {
            FormatVersion = ReleaseManifest.CurrentFormatVersion,
            AppId = "com.app",
            Version = new Version(1, 0, 0),
            Os = PlatformStrings.Os(platform),
            Arch = PlatformStrings.Arch(arch),
            Channel = "stable",
            CreatedAt = DateTimeOffset.UtcNow,
            Files = [new ReleaseFile("App.exe", 1000, new string('a', 64))],
        };
        var signed = ReleaseSigner.Sign(release, key);
        var config = ((InstellaInstallerImpl)InstellaInstaller.Create().WithApp("App", "com.app", new Version(1, 0, 0))
            .WithServer("https://updates.example.com").WithPublisherKey(ReleaseKeys.PublicKeyOf(key).PublicKey).Build()).ConfigForTests;
        var destination = Path.Combine(Path.GetTempPath(), $"instella-lite-{Guid.NewGuid():N}.zip");
        var handler = new Handler(request => request.RequestUri!.AbsolutePath.Contains("/release/")
            ? JsonSerializer.SerializeToUtf8Bytes(signed, WireJsonContext.Default.SignedRelease)
            : new byte[1024 * 1024]);   // far more than 1000 bytes + ZIP overhead

        var ex = Assert.ThrowsAsync<UpdateTrustException>(() => ServerPayloadDownloader.DownloadAsync(
            config, destination, new RecordingLogger(new RecordingSink()), new HttpClient(handler), CancellationToken.None));

        Assert.That(ex!.Message, Does.Contain("larger than"));
        Assert.That(File.Exists(destination), Is.False, "the partial download is deleted");
        await Task.CompletedTask;
    }

    private sealed class Handler(Func<HttpRequestMessage, byte[]> body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body(request)) });
    }
}
