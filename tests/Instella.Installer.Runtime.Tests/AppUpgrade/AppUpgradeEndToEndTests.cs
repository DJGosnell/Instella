using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Diff;
using Instella.Core.Installation;
using Instella.Core.Manifest;
using Instella.Core.Platform;
using Instella.Core.Trust;
using Instella.Core.Update;
using Instella.Core.Wire;
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Runtime.Core.Update;
using Instella.Installer.Runtime.Migrations;
using Instella.Installer.Runtime.Tests.Migrations;
using Instella.Installer.Testing;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.AppUpgrade;

/// <summary>
/// One app's life with an upgrade program, on the fakes: installed at v1 (the program creates the
/// data at schema 1), updated in-app to v2 (schema 2), a v3 update whose program fails (rolled back to
/// v2, data untouched), a repair of v2, and an uninstall whose handler removes the data first.
/// </summary>
[TestFixture]
[NonParallelizable]
public class AppUpgradeEndToEndTests
{
    private static readonly string Upgrade = OperatingSystem.IsWindows() ? "ExampleApp.Upgrade.exe" : "ExampleApp.Upgrade";
    private const string Declaration = """{"contractVersion":1,"program":"ExampleApp.Upgrade","handlesUninstall":true}""";
    private const string AppId = "com.example.app";

    private ECDsa _key = null!;
    private InMemoryFileSystem _fs = null!;
    private FakePlatformServices _platform = null!;
    private readonly List<InstellaTestHarness> _harnesses = [];
    private readonly string _installPath = Path.Combine(Path.GetTempPath(), "instella-e2e-upgrade", Guid.NewGuid().ToString("N"), "ExampleApp");
    private string _schema = null!;

    [SetUp]
    public void SetUp()
    {
        _key = ReleaseKeys.Generate();
        _fs = new InMemoryFileSystem();
        _platform = new FakePlatformServices();
        _schema = Path.Combine(Path.GetTempPath(), "instella-e2e-upgrade-data", Guid.NewGuid().ToString("N"), "schema.txt");
    }

    [TearDown]
    public async Task TearDown()
    {
        _key.Dispose();
        foreach (var h in _harnesses) await h.DisposeAsync();
    }

    private static Dictionary<string, string> Files(string version) => new()
    {
        ["ExampleApp.exe"] = $"app {version}",
        [Upgrade] = $"upgrade program {version}",
        ["instella-upgrade.json"] = Declaration,
    };

    private InstellaTestHarness Installer(string version)
    {
        var installer = InstellaInstaller.Create()
            .WithApp("ExampleApp", AppId, Version.Parse(version))
            .WithExecutableName("ExampleApp.exe")
            .WithElevation(ElevationMode.PerUser)
            .WithServer("https://updates.example.test")
            .WithPublisherKey(ReleaseKeys.PublicKeyOf(_key).PublicKey)
            .Build();
        var harness = InstellaTestHarness.Create()
            .WithInstaller(installer).WithFileSystem(_fs).WithPlatformServices(_platform).WithPayload(Files(version)).Build();
        harness.WhenProgramRuns(TheApp);
        _harnesses.Add(harness);
        return harness;
    }

    /// <summary>The app's upgrade program: brings the data to its own schema, or removes it on uninstall.</summary>
    private ProgramOutcome TheApp(ProgramRun run) => Behave(run.Arguments);

    private ProgramOutcome Behave(IReadOnlyList<string> args)
    {
        var mode = args[args.ToList().IndexOf("--mode") + 1];
        var to = args[args.ToList().IndexOf("--to") + 1];
        if (mode == "uninstall")
        {
            _fs.DeleteFileAsync(_schema, CancellationToken.None).GetAwaiter().GetResult();
            return ProgramOutcome.Exit(0);
        }
        if (to.StartsWith("3.", StringComparison.Ordinal))
            return ProgramOutcome.Exit(1).WithErrorOutput("schema 3 needs a newer database engine");   // fails, changes nothing
        _fs.AddFile(_schema, Encoding.UTF8.GetBytes(to.Split('.')[0]));
        return ProgramOutcome.Exit(0, $"##instella progress 100 Schema {to.Split('.')[0]}");
    }

    private string? Schema() => _fs.Exists(_schema) ? Encoding.UTF8.GetString(_fs.Snapshot()[Path.GetFullPath(_schema)]) : null;

    private string? Live(string rel)
    {
        var path = Path.GetFullPath(Path.Combine(_installPath, rel));
        return _fs.Snapshot().TryGetValue(path, out var bytes) ? Encoding.UTF8.GetString(bytes) : null;
    }

    private Task<InstalledManifest?> Manifest() => new InstallManifestWriter(_fs).ReadAsync(_installPath, CancellationToken.None);

    private async Task<(UpdateResult Result, List<IReadOnlyList<string>> Runs)> UpdateTo(string version)
    {
        var installed = (await Manifest())!;
        var files = Files(version).ToDictionary(kv => kv.Key, kv => Encoding.UTF8.GetBytes(kv.Value));
        var release = ReleaseSigner.Sign(new ReleaseManifest
        {
            FormatVersion = ReleaseManifest.CurrentFormatVersion,
            AppId = AppId,
            Version = Instella.Core.Utilities.AppVersions.Normalize(Version.Parse(version)),
            Os = PlatformStrings.Os(installed.Platform),
            Arch = PlatformStrings.Arch(installed.Architecture ?? ArchitectureExtensions.Current),
            Channel = installed.Channel ?? "stable",
            CreatedAt = DateTimeOffset.UtcNow,
            Files = files.Select(kv => new ReleaseFile(kv.Key, kv.Value.Length, Convert.ToHexStringLower(SHA256.HashData(kv.Value)))).ToList(),
        }, _key);

        var runs = new List<IReadOnlyList<string>>();
        var programs = new FakePrograms
        {
            Behaviour = (start, line) =>
            {
                runs.Add(start.Arguments);
                var outcome = Behave(start.Arguments);
                foreach (var l in outcome.Output) line(ProgramStream.Output, l);
                foreach (var l in outcome.ErrorOutput) line(ProgramStream.Error, l);
                return outcome.ExitCode;
            },
        };
        var engine = new UpdaterEngine(new UpdaterArgs
        {
            AppPath = _installPath,
            AppExecutable = "ExampleApp.exe",
            FromVersion = installed.Version,
            ToVersion = Instella.Core.Utilities.AppVersions.Normalize(Version.Parse(version)),
            Restart = false,
            GracefulTimeout = TimeSpan.Zero,
        }, new ReleaseDownloader(release, files), _platform, _fs, BsDiffEngine.Instance, [TimeSpan.Zero]) { Programs = programs };
        return (await engine.RunAsync(CancellationToken.None), runs);
    }

    [Test]
    public async Task Install_Update_FailedUpdate_Repair_Uninstall()
    {
        // 1. First install: the program creates the data.
        var v1 = Installer("1.0");
        Assert.That(await v1.RunFullWithArgsAsync(["--install", "--silent", "--path", _installPath]), Is.EqualTo(0));
        Assert.That(Schema(), Is.EqualTo("1"));
        Assert.That(v1.ProgramRuns.Single().Arguments.Take(5), Is.EqualTo(new[] { "--instella-upgrade", "--contract", "1", "--mode", "first-install" }));

        // 2. In-app update to v2: new files, and the data follows.
        var (updated, runs) = await UpdateTo("2.0");
        Assert.That(updated.Success, Is.True, updated.Error);
        Assert.That(runs.Single().Skip(3).Take(6), Is.EqualTo(new[] { "--mode", "update", "--from", "1.0.0", "--to", "2.0.0" }));
        Assert.That(Live("ExampleApp.exe"), Is.EqualTo("app 2.0"));
        Assert.That(Schema(), Is.EqualTo("2"));
        Assert.That((await Manifest())!.Version, Is.EqualTo(new Version(2, 0, 0)));

        // 3. An update to v3 whose program fails: v2 stays, files and data alike.
        var (failed, _) = await UpdateTo("3.0");
        Assert.That(failed.ExitCode, Is.EqualTo(InstellaExitCode.UpdateAppUpgradeFailed));
        Assert.That(failed.Error, Does.Contain("schema 3 needs a newer database engine"));
        Assert.That(Live("ExampleApp.exe"), Is.EqualTo("app 2.0"));
        Assert.That(Live(Upgrade), Is.EqualTo("upgrade program 2.0"));
        Assert.That((await Manifest())!.Version, Is.EqualTo(new Version(2, 0, 0)));
        Assert.That(Schema(), Is.EqualTo("2"));

        // 4. Repair v2 with its installer: the program runs with the same version twice.
        var v2 = Installer("2.0");
        Assert.That(await v2.RunFullWithArgsAsync(["--install", "--silent", "--path", _installPath]), Is.EqualTo(0));
        Assert.That(v2.ProgramRuns.Single().Arguments.Skip(3).Take(6), Is.EqualTo(new[] { "--mode", "repair", "--from", "2.0.0", "--to", "2.0.0" }));

        // 5. Uninstall: the handler removes the data first, then Instella removes the rest.
        Assert.That(await v2.RunFullWithArgsAsync(["--uninstall", "--silent", "--path", _installPath]), Is.EqualTo(0));
        Assert.That(v2.ProgramRuns.Last().Arguments[0], Is.EqualTo("--instella-uninstall"));
        Assert.That(Schema(), Is.Null);
        Assert.That(Live("ExampleApp.exe"), Is.Null);
        Assert.That(await Manifest(), Is.Null);
    }

    /// <summary>Serves one signed release and its files.</summary>
    private sealed class ReleaseDownloader(SignedRelease release, Dictionary<string, byte[]> files) : IUpdateDownloader
    {
        public Task<SignedRelease?> GetReleaseAsync(CancellationToken ct) => Task.FromResult<SignedRelease?>(release);
        public Task<Stream> DownloadFileAsync(string relativePath, CancellationToken ct) => Task.FromResult<Stream>(new MemoryStream(files[relativePath]));
        public Task<PatchManifest?> DownloadPatchManifestAsync(CancellationToken ct) => Task.FromResult<PatchManifest?>(null);
        public Task<Stream> OpenPatchEntryAsync(string patchSha256, long maxBytes, CancellationToken ct) => throw new NotSupportedException();
        public Task<Stream> DownloadFullBuildAsync(CancellationToken ct) => throw new NotSupportedException();
    }
}
