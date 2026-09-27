using System.Security.Cryptography;
using Instella.CLI.Services;
using Instella.Core.Installation;
using Instella.Core.Platform;
using Instella.Core.Trust;
using Instella.Core.Wire;
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Runtime.Runners;
using NUnit.Framework;

namespace Instella.Contract.Tests;

/// <summary>
/// <c>WithNewerVersionPrompt()</c> against the real server: an installer finds a newer version's
/// online installer, verifies it against its own publisher keys, downloads it and hands over;
/// every failure on the way leaves the install of its own version going.
/// </summary>
[TestFixture]
public class NewerVersionContractTests
{
    private const string PackageId = "com.instella.newer";
    private static readonly Version V1 = new(1, 0, 0);
    private static readonly Version V2 = new(2, 0, 0);

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
        _work = Directory.CreateTempSubdirectory("instella-newer-contract-").FullName;
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
    public async Task NewerVersion_Accepted_DownloadsItsVerifiedInstallerAndHandsOver()
    {
        await Publish(V1, "web installer 1");
        var v2Installer = await Publish(V2, "web installer 2", changelog: "Faster notes");
        AvailableVersion? offered = null;
        (string Path, IReadOnlyList<string> Args)? launched = null;

        var offer = new NewerVersionOffer(Config(V1), new NullLog(), (_, current, newer) =>
        {
            Assert.That(current, Is.EqualTo(V1));
            offered = newer;
            return NewerVersionChoice.InstallNewer;
        })
        {
            Launcher = (path, args, _) => { launched = (path, args); return Task.FromResult(0); },
            Authenticode = Instella.Core.Platform.Windows.NoAuthenticodeReader.Instance,
        };
        var exit = await offer.RunAsync(_server.CreateClient(), ["--path", @"C:\Apps\Notes"], CancellationToken.None);

        Assert.That(exit, Is.EqualTo(0), "the handed-over installer's exit code is returned");
        Assert.That(offered!.Version, Is.EqualTo(V2));
        Assert.That(offered.Changelog, Is.EqualTo("Faster notes"));
        Assert.That(File.ReadAllBytes(launched!.Value.Path), Is.EqualTo(File.ReadAllBytes(v2Installer)));
        Assert.That(Path.GetFileName(launched.Value.Path), Is.EqualTo(Path.GetFileName(v2Installer)));
        Assert.That(launched.Value.Args, Is.EqualTo(new[] { "--path", @"C:\Apps\Notes" }),
            "the original arguments are forwarded (RunAsync adds --no-newer-check)");
    }

    [TestCase(false, null)]
    [TestCase(true, (int)InstellaExitCode.UserCancelled)]
    public async Task NewerVersion_Declined_InstallsThisVersionOrStops(bool cancel, int? expected)
    {
        var choice = cancel ? NewerVersionChoice.Cancel : NewerVersionChoice.InstallThis;
        await Publish(V2, "web installer 2");
        var offer = new NewerVersionOffer(Config(V1), new NullLog(), (_, _, _) => choice)
        {
            Launcher = (_, _, _) => throw new AssertionException("nothing is handed over"),
        };

        Assert.That(await offer.RunAsync(_server.CreateClient(), [], CancellationToken.None), Is.EqualTo(expected));
    }

    [TestCase("2.0.0")]
    [TestCase("2.1.0")]
    public async Task NewerVersion_AlreadyInstalled_IsNotOffered(string installed)
    {
        // Hand test: with 1.4.0 installed, the 1.2.0 installer still offered to install 1.4.0.
        await Publish(V2, "web installer 2");
        var offer = new NewerVersionOffer(Config(V1), new NullLog(), (_, _, _) => throw new AssertionException("already installed"))
        {
            InstalledVersion = _ => Task.FromResult<Version?>(Version.Parse(installed)),
        };

        Assert.That(await offer.RunAsync(_server.CreateClient(), [], CancellationToken.None), Is.Null);
    }

    [Test]
    public async Task NewerVersion_OlderOneInstalled_IsStillOffered()
    {
        await Publish(V2, "web installer 2");
        var asked = false;
        var offer = new NewerVersionOffer(Config(V1), new NullLog(), (_, _, _) => { asked = true; return NewerVersionChoice.InstallThis; })
        {
            InstalledVersion = _ => Task.FromResult<Version?>(new Version(1, 5, 0)),
        };

        await offer.RunAsync(_server.CreateClient(), [], CancellationToken.None);

        Assert.That(asked, Is.True);
    }

    [Test]
    public async Task NoNewerVersion_AsksNothing()
    {
        await Publish(V1, "web installer 1");
        var offer = new NewerVersionOffer(Config(V1), new NullLog(), (_, _, _) => throw new AssertionException("nothing to offer"));

        Assert.That(await offer.RunAsync(_server.CreateClient(), [], CancellationToken.None), Is.Null);
    }

    [Test]
    public async Task NewerRelease_SignedByAKeyThisInstallerDoesNotTrust_IsNotOffered()
    {
        using var other = ReleaseKeys.Generate();
        await Publish(V2, "web installer 2", key: other);
        var offer = new NewerVersionOffer(Config(V1), new NullLog(), (_, _, _) => throw new AssertionException("an unverified release is never offered"));

        Assert.That(await offer.RunAsync(_server.CreateClient(), [], CancellationToken.None), Is.Null);
    }

    [Test]
    public async Task TamperedInstallerOnTheServer_IsNotRun_AndThisVersionIsInstalled()
    {
        var v2Installer = await Publish(V2, "web installer 2");
        var blob = _server.BlobPath(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(v2Installer))));
        File.WriteAllText(blob, "web installer X");   // same length, different content
        string? error = null;

        var offer = new NewerVersionOffer(Config(V1), new NullLog(), (_, _, _) => NewerVersionChoice.InstallNewer, e => error = e)
        {
            Launcher = (_, _, _) => throw new AssertionException("a tampered installer is never run"),
            Authenticode = Instella.Core.Platform.Windows.NoAuthenticodeReader.Instance,
        };

        Assert.That(await offer.RunAsync(_server.CreateClient(), [], CancellationToken.None), Is.Null);
        Assert.That(error, Does.Contain("does not match the signed release"));
    }

    [Test]
    public async Task ServerUnreachable_CarriesOnSilently()
    {
        var config = Config(V1) with { ServerUrl = "http://127.0.0.1:9/" };
        var offer = new NewerVersionOffer(config, new NullLog(), (_, _, _) => throw new AssertionException("nothing to offer"));

        using var http = new HttpClient();
        Assert.That(await offer.RunAsync(http, [], CancellationToken.None), Is.Null);
    }

    private FrozenConfig Config(Version version) => ((InstellaInstallerImpl)InstellaInstaller.Create()
        .WithApp("Notes", PackageId, version)
        .WithServer(_server.BaseUrl)
        .WithPublisherKey(_publicKey.PublicKey)
        .WithNewerVersionPrompt()
        .Build()).ConfigForTests;

    /// <summary>Uploads <paramref name="version"/> for this machine's platform with an online installer; returns the installer's path.</summary>
    private async Task<string> Publish(Version version, string installerContent, string? changelog = null, ECDsa? key = null)
    {
        var dir = Path.Combine(_work, "build-" + version);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Notes.exe"), "notes " + version);
        var installerDir = Path.Combine(_work, "installer-" + version);
        Directory.CreateDirectory(installerDir);
        var installer = Path.Combine(installerDir, $"Notes-WebSetup-{version}.exe");
        File.WriteAllText(installer, installerContent);

        using var client = new UploadClient(_server.CreateClient(), _server.BaseUrl, _apiKey);
        var result = await client.UploadVersionAsync(new UploadRequest
        {
            PackageId = PackageId, Version = version, SourceDirectory = dir, Channel = "stable",
            Platform = PlatformDetector.Current, Architecture = ArchitectureExtensions.Current,
            SigningKey = key ?? _signingKey, Changelog = changelog,
            Installers = [new InstallerUpload(InstallerKinds.Online, installer)],
        });
        Assert.That(result.Success, Is.True, result.Error);
        return installer;
    }

    private sealed class NullLog : Instella.Core.Logging.IInstellaLogger
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
