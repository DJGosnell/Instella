using System.Security.Cryptography;
using Instella.CLI.Services;
using Instella.Core.Installation;
using Instella.Core.Platform;
using Instella.Core.Trust;
using Instella.Core.Wire;
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Runtime.Runners;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace Instella.Contract.Tests;

/// <summary>
/// <c>WithVersionSelection()</c> against the real server: <c>--list-versions</c>,
/// <c>--app-version</c> and <c>--choose-version</c>. Another version is always installed by
/// its own verified installer; the installer's own version installs here.
/// </summary>
[TestFixture]
public class VersionSelectionContractTests
{
    private const string PackageId = "com.instella.selection";
    private static readonly Version V1 = new(1, 0, 0);
    private static readonly Version V2 = new(2, 0, 0);
    private static readonly Version V3 = new(3, 0, 0);

    private ContractServer _server = null!;
    private string _apiKey = null!;
    private string _work = null!;
    private ECDsa _signingKey = null!;
    private PublisherKey _publicKey = null!;
    private readonly Dictionary<Version, string> _installers = [];

    [SetUp]
    public async Task SetUp()
    {
        _server = new ContractServer();
        _apiKey = await _server.SeedPackageAndKeyAsync(PackageId);
        _work = Directory.CreateTempSubdirectory("instella-selection-contract-").FullName;
        _signingKey = ReleaseKeys.Generate();
        _publicKey = ReleaseKeys.PublicKeyOf(_signingKey);
        _installers.Clear();

        await Publish(V1, "First release");
        await Publish(V2, "Second release\nwith two lines");
        await Publish(V3, "Broken release");
        await Publish(new Version(4, 0, 0), "Beta", channel: "beta");
        await _server.QueryAsync(async db =>
        {
            (await db.PackageVersions.SingleAsync(v => v.VersionString == "3.0.0")).IsDeprecated = true;
            return await db.SaveChangesAsync();
        });
    }

    [TearDown]
    public void TearDown()
    {
        _signingKey.Dispose();
        _server.Dispose();
        Directory.Delete(_work, recursive: true);
    }

    [Test]
    public async Task ListVersions_PrintsTheChannelsInstallableVersions_WithChangelogs()
    {
        var output = new StringWriter();
        var exit = await Selection(V2, output).RunAsync(_server.CreateClient(), ["--list-versions"], silent: false, CancellationToken.None);

        Assert.That(exit, Is.EqualTo(0));
        var text = output.ToString();
        Assert.That(text, Does.Contain("2.0.0").And.Contain("(latest, this installer)"));
        Assert.That(text, Does.Contain("1.0.0").And.Contain("First release"));
        Assert.That(text, Does.Contain("      with two lines"), "every changelog line is printed, indented");
        Assert.That(text, Does.Not.Contain("3.0.0"), "deprecated versions are hidden");
        Assert.That(text, Does.Not.Contain("4.0.0"), "other channels are not listed");

        var beta = new StringWriter();
        await Selection(V2, beta).RunAsync(_server.CreateClient(), ["--list-versions", "--channel", "beta"], silent: false, CancellationToken.None);
        Assert.That(beta.ToString(), Does.Contain("4.0.0").And.Not.Contain("1.0.0"));
    }

    [Test]
    public async Task AppVersion_Older_HandsOverToThatVersionsVerifiedInstaller_WithoutTheSelectionFlags()
    {
        (string Path, IReadOnlyList<string> Args)? launched = null;
        var selection = Launching(V2, l => launched = l);

        var exit = await selection.RunAsync(_server.CreateClient(), ["--silent", "--app-version", "1.0.0", "--path", "C:\\x"], silent: true, CancellationToken.None);

        Assert.That(exit, Is.EqualTo(42), "the handed-over installer's exit code");
        Assert.That(File.ReadAllBytes(launched!.Value.Path), Is.EqualTo(File.ReadAllBytes(_installers[V1])));
        Assert.That(launched.Value.Args, Is.EqualTo(new[] { "--silent", "--path", "C:\\x" }));
    }

    [Test]
    public void AReleaseOfAnotherChannel_IsRefusedByTheHandoff()
    {
        // 4.0.0 is on beta: a stable selection must not accept its signed release.
        var ex = Assert.ThrowsAsync<UpdateTrustException>(() =>
            InstallerHandoff.VerifyAsync(Config(V2), _server.CreateClient(), new Version(4, 0, 0), "stable", CancellationToken.None));
        Assert.That(ex!.Message, Does.Contain("channel mismatch"));
    }

    [Test]
    public async Task AppVersion_ThisInstallersOwnOrLatestWhenItIsTheLatest_InstallsHere()
    {
        Assert.That(await Selection(V2).RunAsync(_server.CreateClient(), ["--app-version", "2.0.0"], silent: true, CancellationToken.None), Is.Null);
        Assert.That(await Selection(V2).RunAsync(_server.CreateClient(), ["--app-version=latest"], silent: true, CancellationToken.None), Is.Null);
    }

    [Test]
    public async Task AppVersion_Latest_FromAnOlderInstaller_HandsOverToTheNewest()
    {
        (string Path, IReadOnlyList<string> Args)? launched = null;
        var exit = await Launching(V1, l => launched = l)
            .RunAsync(_server.CreateClient(), ["--app-version", "latest"], silent: true, CancellationToken.None);

        Assert.That(exit, Is.EqualTo(42));
        Assert.That(File.ReadAllBytes(launched!.Value.Path), Is.EqualTo(File.ReadAllBytes(_installers[V2])), "3.0.0 is deprecated");
    }

    [TestCase("3.0.0")]
    [TestCase("9.9.9")]
    [TestCase("not-a-version")]
    public async Task AppVersion_NotInstallable_IsAUsageError(string version)
    {
        var exit = await Selection(V2).RunAsync(_server.CreateClient(), ["--app-version", version], silent: true, CancellationToken.None);
        Assert.That(exit, Is.EqualTo((int)InstellaExitCode.UsageInvalidArgs));
    }

    [Test]
    public async Task ChooseVersion_OffersNewestFirst_IncludingThisInstaller_AndHandsOverToThePick()
    {
        IReadOnlyList<Version>? offered = null;
        (string Path, IReadOnlyList<string> Args)? launched = null;
        var selection = new VersionSelection(Config(new Version(1, 5, 0)), new NullLog(), picker: (_, _, versions) =>
        {
            offered = versions.Select(v => v.Version).ToList();
            return V1;
        })
        {
            Launcher = (path, args, _) => { launched = (path, args); return Task.FromResult(0); },
            Authenticode = Instella.Core.Platform.Windows.NoAuthenticodeReader.Instance,
        };

        Assert.That(await selection.RunAsync(_server.CreateClient(), ["--choose-version"], silent: false, CancellationToken.None), Is.EqualTo(0));
        Assert.That(offered, Is.EqualTo(new[] { V2, new Version(1, 5, 0), V1 }), "this installer's own version is offered even though it is not on the server");
        Assert.That(File.ReadAllBytes(launched!.Value.Path), Is.EqualTo(File.ReadAllBytes(_installers[V1])));
    }

    [Test]
    public async Task ChooseVersion_CancelledOrSilent_Stops()
    {
        var cancelled = new VersionSelection(Config(V2), new NullLog(), picker: (_, _, _) => null);
        Assert.That(await cancelled.RunAsync(_server.CreateClient(), ["--choose-version"], silent: false, CancellationToken.None),
            Is.EqualTo((int)InstellaExitCode.UserCancelled));
        Assert.That(await cancelled.RunAsync(_server.CreateClient(), ["--choose-version", "--silent"], silent: true, CancellationToken.None),
            Is.EqualTo((int)InstellaExitCode.UsageInvalidArgs));
    }

    [Test]
    public async Task WithoutWithVersionSelection_TheFlagsAreRefused()
    {
        var config = Config(V2) with { AllowVersionSelection = false };
        var exit = await new VersionSelection(config, new NullLog()).RunAsync(_server.CreateClient(), ["--list-versions"], silent: false, CancellationToken.None);
        Assert.That(exit, Is.EqualTo((int)InstellaExitCode.UsageInvalidArgs));
    }

    private VersionSelection Selection(Version own, TextWriter? output = null) =>
        new(Config(own), new NullLog(), output ?? TextWriter.Null, (_, _, _) => throw new AssertionException("no picker expected"));

    /// <summary>A selection whose handed-over installer is recorded instead of run, and "exits" 42.</summary>
    private VersionSelection Launching(Version own, Action<(string Path, IReadOnlyList<string> Args)> record) =>
        new(Config(own), new NullLog(), TextWriter.Null, (_, _, _) => throw new AssertionException("no picker expected"))
        {
            Launcher = (path, args, _) => { record((path, args)); return Task.FromResult(42); },
            Authenticode = Instella.Core.Platform.Windows.NoAuthenticodeReader.Instance,
        };

    private FrozenConfig Config(Version version) => ((InstellaInstallerImpl)InstellaInstaller.Create()
        .WithApp("Selection", PackageId, version)
        .WithServer(_server.BaseUrl)
        .WithPublisherKey(_publicKey.PublicKey)
        .WithVersionSelection()
        .Build()).ConfigForTests;

    private async Task Publish(Version version, string changelog, string channel = "stable")
    {
        var dir = Path.Combine(_work, "build-" + version);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "App.exe"), "app " + version);
        var installerDir = Path.Combine(_work, "installer-" + version);
        Directory.CreateDirectory(installerDir);
        var installer = Path.Combine(installerDir, $"Selection-WebSetup-{version}.exe");
        File.WriteAllText(installer, "online installer " + version);
        _installers[version] = installer;

        using var client = new UploadClient(_server.CreateClient(), _server.BaseUrl, _apiKey);
        var result = await client.UploadVersionAsync(new UploadRequest
        {
            PackageId = PackageId, Version = version, SourceDirectory = dir, Channel = channel,
            Platform = PlatformDetector.Current, Architecture = ArchitectureExtensions.Current,
            SigningKey = _signingKey, Changelog = changelog,
            Installers = [new InstallerUpload(InstallerKinds.Online, installer)],
        });
        Assert.That(result.Success, Is.True, result.Error);
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
