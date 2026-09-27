using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Platform.Windows;
using Instella.Core.Trust;
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Runtime.Runners;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Runners;

/// <summary>The handoff to another version's installer, outside the server round trip (see the contract tests).</summary>
[TestFixture]
public class InstallerHandoffTests
{
    private const string Key = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEk1bD9U9E2vGf7yQ9bVtSb1bq0mDk3jd1Vb6P4Jk1Yv0w5Zb1Y1y3Kp0vM1h0q4bqF1pXcLZr3Q2Q3y0nJk9y4A==";

    [Test]
    public void NewerVersionPrompt_WithoutServerOrPublisherKey_FailsTheBuild()
    {
        var noServer = Assert.Throws<InvalidOperationException>(() =>
            InstellaInstaller.Create().WithApp("App", "com.app", new Version(1, 0)).WithNewerVersionPrompt().Build());
        Assert.That(noServer!.Message, Does.Contain("WithNewerVersionPrompt() needs WithServer"));

        var unsigned = Assert.Throws<InvalidOperationException>(() =>
            InstellaInstaller.Create().WithApp("App", "com.app", new Version(1, 0))
                .WithServer("https://updates.example.com").AllowUnsignedUpdates().WithNewerVersionPrompt().Build());
        Assert.That(unsigned!.Message, Does.Contain("WithPublisherKey"));
    }

    [Test]
    public void VersionSelection_WithoutServerOrPublisherKey_FailsTheBuild()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            InstellaInstaller.Create().WithApp("App", "com.app", new Version(1, 0)).WithVersionSelection().Build());
        Assert.That(ex!.Message, Does.Contain("WithVersionSelection() needs WithServer"));
    }

    [Test]
    public void HandoffAndSelectionFlags_AreReserved_SoTheStrictParserAcceptsThem()
    {
        string[] args = [InstallerHandoff.NoNewerCheckFlag, VersionSelection.ListFlag, VersionSelection.ChooseFlag,
            VersionSelection.AppVersionFlag, "1.2.0"];
        foreach (var flag in args.Where(a => a.StartsWith("--", StringComparison.Ordinal)))
            Assert.That(ReservedCliFlags.All, Does.Contain(flag));
        Assert.DoesNotThrow(() => CliArgParser.Parse(args, [], strict: true));
    }

    [Test]
    public void HandedOverInstaller_GetsTheArgumentsWithoutTheSelectionFlags()
    {
        string[] args = ["--silent", "--app-version", "1.2.0", "--channel", "beta", "--path", @"C:\x",
            "--list-versions", "--choose-version", "--app-version=latest", "--channel=beta", "--accept-license"];
        Assert.That(VersionSelection.WithoutSelectionFlags(args), Is.EqualTo(new[] { "--silent", "--path", @"C:\x", "--accept-license" }));
    }

    [Test, Platform("Win"), SupportedOSPlatform("windows")]
    public async Task Run_ForwardsTheArguments_AddsNoNewerCheck_AndReturnsTheExitCode()
    {
        // The script exits 7 only when it got the forwarded arguments followed by exactly one
        // --no-newer-check; getting 7 back also proves it was waited for.
        var dir = Directory.CreateTempSubdirectory("instella-handoff-").FullName;
        var script = Path.Combine(dir, "setup.cmd");
        File.WriteAllText(script,
            "@if \"%~1\"==\"--path\" if \"%~2\"==\"C:\\x\" if \"%~3\"==\"--no-newer-check\" if \"%~4\"==\"\" exit /b 7\r\n" +
            "@exit /b 3\r\n");

        var exit = await InstallerHandoff.RunAsync(script, ["--path", @"C:\x", "--no-newer-check"], CancellationToken.None);
        Directory.Delete(dir, recursive: true);

        Assert.That(exit, Is.EqualTo(7));
    }

    // ---- the verified file can't be swapped before it runs ----

    [Test]
    public async Task Download_HoldsTheFile_SoNoWriterCanOpenIt_UntilDisposed()
    {
        var bytes = "online installer 2"u8.ToArray();
        await using var file = await Download(bytes, new FakeReader());

        Assert.Throws<IOException>(() => new FileStream(file.Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite).Dispose(),
            "a writer is refused while the verified file is held");
        Assert.That(File.ReadAllBytes(file.Path), Is.EqualTo(bytes), "readers (and the loader) are not");

        await file.DisposeAsync();
        await using (new FileStream(file.Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)) { }
    }

    // ---- the handed-over installer must have the same Authenticode signer ----

    [Test]
    public async Task Signer_SelfUnsigned_SkipsTheCheck()
    {
        var reader = new FakeReader { ["self"] = new(false, null, -1), ["other"] = new(false, null, -1) };
        await using var file = await Download("x"u8.ToArray(), reader);
        Assert.That(File.Exists(file.Path), Is.True);
    }

    [Test]
    public async Task Signer_SameSubject_IsAccepted()
    {
        var reader = new FakeReader { ["self"] = new(true, "CN=Acme", 0), ["other"] = new(true, "CN=Acme", 0) };
        await using var file = await Download("x"u8.ToArray(), reader);
        Assert.That(File.Exists(file.Path), Is.True);
    }

    [TestCase(false, null)]
    [TestCase(true, "CN=Mallory")]
    public void Signer_UnsignedOrAnotherSubject_IsRefused_AndTheFileDeleted(bool otherValid, string? otherSubject)
    {
        var reader = new FakeReader { ["self"] = new(true, "CN=Acme", 0), ["other"] = new(otherValid, otherSubject, otherValid ? 0 : -2146762496) };
        string? path = null;
        reader.OnRead = p => { if (!p.EndsWith("self", StringComparison.Ordinal)) path = p; };

        var ex = Assert.ThrowsAsync<InstallerSignerMismatchException>(() => Download("x"u8.ToArray(), reader));

        Assert.That(ex!.Message, Is.EqualTo("the downloaded installer is not signed by CN=Acme"));
        Assert.That(path, Is.Not.Null);
        Assert.That(File.Exists(path!), Is.False, "the refused installer is deleted");
    }

    private static readonly string RealKey = ReleaseKeys.PublicKeyOf(ReleaseKeys.Generate()).PublicKey;

    private static async Task<VerifiedInstallerFile> Download(byte[] bytes, FakeReader reader)
    {
        var config = ((InstellaInstallerImpl)InstellaInstaller.Create().WithApp("App", "com.app", new Version(1, 0))
            .WithServer("https://updates.example.com").WithPublisherKey(RealKey).WithNewerVersionPrompt().Build()).ConfigForTests;
        var verified = new VerifiedInstaller(new Version(2, 0),
            new ReleaseInstaller("online", "App-WebSetup-2.0.0.exe", bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes))));
        using var http = new HttpClient(new BytesHandler(bytes));
        return await InstallerHandoff.DownloadAsync(config, http, verified, CancellationToken.None, reader, selfPath: "self");
    }

    /// <summary>Answers "self" and "other" (any other path) from a table.</summary>
    private sealed class FakeReader : IAuthenticodeReader
    {
        private readonly Dictionary<string, AuthenticodeInfo> _table = new() { ["self"] = new(false, null, -1), ["other"] = new(false, null, -1) };
        public Action<string>? OnRead { get; set; }
        public AuthenticodeInfo this[string key] { set => _table[key] = value; }

        public AuthenticodeInfo Read(string path)
        {
            OnRead?.Invoke(path);
            return _table[path == "self" ? "self" : "other"];
        }
    }

    private sealed class BytesHandler(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
    }
}
