using System.Net;
using System.Text;
using System.Text.Json;
using Instella.Core.Platform;
using Instella.Core.Trust;
using Instella.Core.Wire;
using Instella.Sdk.Internal;
using NUnit.Framework;

namespace Instella.Sdk.Tests;

/// <summary>What <see cref="HttpUpdateClient"/> accepts from a server it does not trust.</summary>
[TestFixture]
public class UpdateCheckTrustTests
{
    private const string AppId = "com.example.app";

    [Test]
    public async Task AReleaseSignedForAnotherChannel_IsRejected()
    {
        using var key = ReleaseKeys.Generate();
        var info = Info(ReleaseKeys.PublicKeyOf(key));

        var beta = await CheckAsync(info, "stable", Release(key, "beta"));
        Assert.That(beta.UpdateAvailable, Is.False);
        Assert.That(beta.Error, Does.Contain("channel mismatch"));

        var stable = await CheckAsync(info, "stable", Release(key, "stable"));
        Assert.That(stable.Error, Is.Null);
        Assert.That(stable.UpdateAvailable, Is.True);

        var optedIn = await CheckAsync(info, "beta", Release(key, "beta"));
        Assert.That(optedIn.UpdateAvailable, Is.True, "an app that asks for beta accepts beta");
    }

    [Test]
    public async Task APlainHttpServerUrl_IsRefusedBeforeAnyRequest_UnlessTheInstallerAllowedIt()
    {
        using var key = ReleaseKeys.Generate();
        var info = Info(ReleaseKeys.PublicKeyOf(key)) with { ServerUrl = "http://updates.example.test" };

        var refused = await CheckAsync(info, "stable", Release(key, "stable"));
        Assert.That(refused.Error, Does.Contain("plain http"));

        var allowed = await CheckAsync(info with { AllowInsecureServer = true }, "stable", Release(key, "stable"));
        Assert.That(allowed.UpdateAvailable, Is.True, allowed.Error);
    }

    [Test]
    public async Task TheDownloadToken_IsSentWithTheCheck()
    {
        // A PackageKeyRequired package answers only a request that carries its token.
        using var key = ReleaseKeys.Generate();
        var info = Info(ReleaseKeys.PublicKeyOf(key)) with { DownloadToken = "idt_" + new string('A', 43) };
        var body = JsonSerializer.Serialize(new CheckUpdateResponse
        {
            UpdateAvailable = true, Version = "2.0.0", FullSize = 1, Release = Release(key, "stable"),
        }, JsonSerializerOptions.Web);
        var stub = new StubHandler(body);

        var result = await new HttpUpdateClient(new HttpClient(stub)).CheckAsync(info, "stable", CancellationToken.None);

        Assert.That(result.UpdateAvailable, Is.True, result.Error);
        Assert.That(stub.Authorization, Is.EqualTo("Bearer idt_" + new string('A', 43)));
    }

    [Test]
    public async Task WithoutAToken_NoAuthorizationIsSent()
    {
        using var key = ReleaseKeys.Generate();
        var body = JsonSerializer.Serialize(new CheckUpdateResponse { UpdateAvailable = false }, JsonSerializerOptions.Web);
        var stub = new StubHandler(body);

        await new HttpUpdateClient(new HttpClient(stub)).CheckAsync(Info(ReleaseKeys.PublicKeyOf(key)), "stable", CancellationToken.None);

        Assert.That(stub.Authorization, Is.Null);
    }

    private static InstellaInfo Info(PublisherKey key) => new()
    {
        AppName = "App", AppId = AppId, Version = new Version(1, 0, 0), InstallRoot = Path.GetTempPath(),
        ServerUrl = "https://updates.example.test", Platform = TargetPlatform.Windows, Architecture = Architecture.X64,
        TrustedKeys = [key],
    };

    private static SignedRelease Release(System.Security.Cryptography.ECDsa key, string channel) => ReleaseSigner.Sign(new ReleaseManifest
    {
        FormatVersion = ReleaseManifest.CurrentFormatVersion,
        AppId = AppId,
        Version = new Version(2, 0, 0),
        Os = "windows",
        Arch = "x64",
        Channel = channel,
        CreatedAt = DateTimeOffset.UtcNow,
        Files = [new ReleaseFile("App.exe", 1, new string('a', 64), Executable: true)],
    }, key);

    private static Task<Core.Update.UpdateCheckResult> CheckAsync(InstellaInfo info, string channel, SignedRelease release)
    {
        var body = JsonSerializer.Serialize(new CheckUpdateResponse
        {
            UpdateAvailable = true, Version = "2.0.0", FullSize = 1, Release = release,
        }, JsonSerializerOptions.Web);
        var http = new HttpClient(new StubHandler(body));
        return new HttpUpdateClient(http).CheckAsync(info, channel, CancellationToken.None);
    }

    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        public string? Authorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Authorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }
}
