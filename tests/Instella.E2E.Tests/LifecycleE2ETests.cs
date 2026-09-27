using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Instella.Core.Update;
using Microsoft.Win32;
using NUnit.Framework;

namespace Instella.E2E.Tests;

/// <summary>
/// The whole product, as processes. A generated app (referencing Instella.Sdk) and
/// installer (referencing the runtime and the build targets from this repository) are built
/// in a temp directory; the real server runs on Kestrel; the real CLI generates the key and
/// uploads signed releases. Then install, update from inside the app, crash mid-commit and
/// recover, refuse a tampered blob, uninstall, and check nothing leaked.
/// </summary>
/// <remarks>
/// A generated console app stands in for the Avalonia sample, so the test needs neither a
/// display nor the C++ toolchain (the installer is published self-contained single-file instead
/// of AOT) and does not change with the sample.
/// </remarks>
[TestFixture]
[Category("E2E")]
[Explicit("Installs software into the user profile; run through verify.ps1 -Stage E2E")]
[Platform("Win")]
[SupportedOSPlatform("windows")]
[NonParallelizable]
public sealed class LifecycleE2ETests
{
    private const string AppExe = "E2EApp.exe";
    private readonly string _suffix = Guid.NewGuid().ToString("N")[..8];
    private string AppId => $"com.instella.e2e.{_suffix}";
    private string AppName => $"InstellaE2E{_suffix}";

    private string _repo = null!;
    private string _work = null!;
    private string _installDir = null!;
    private string _markers = null!;
    private E2EServer _server = null!;

    [OneTimeSetUp]
    public void SetUp()
    {
        _repo = FindRepo();
        _work = Directory.CreateTempSubdirectory("instella-e2e-").FullName;
        _installDir = Path.Combine(_work, "app");
        _markers = Path.Combine(_work, "markers");
        Directory.CreateDirectory(_markers);
        _server = new E2EServer(Path.Combine(_work, "server"), FreePort());
        _server.StartServer();
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        _server?.Dispose();
        // Best effort: never leave an installation behind, even when a step failed.
        try { Registry.CurrentUser.DeleteSubKeyTree($@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{AppId}", throwOnMissingSubKey: false); } catch { }
        try { File.Delete(StartMenuShortcut); } catch { }
        // INSTELLA_E2E_KEEP=1 keeps the work directory (builds, server data, logs) for inspection.
        if (Environment.GetEnvironmentVariable("INSTELLA_E2E_KEEP") != "1")
            try { Directory.Delete(_work, recursive: true); } catch { }
        else
            TestContext.Out.WriteLine($"E2E work directory kept: {_work}");
    }

    private string StartMenuShortcut =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk");

    [Test]
    public async Task InstallUpdateRecoverTamperUninstall()
    {
        // 1. Server, API key, publisher key.
        var apiKey = await _server.SeedPackageAndKeyAsync(AppId);
        var keyFile = Path.Combine(_work, "publisher.key.pem");
        var keys = Cli("keys", "generate", "--out", keyFile);
        var publicKey = keys.Split('\n').Select(l => l.Trim()).First(l => l.StartsWith("Public key:", StringComparison.Ordinal))["Public key:".Length..].Trim();

        // 2. The app, three releases of it, and the v1.0.0 installer.
        var payloads = BuildApp();
        var installer = BuildInstaller(publicKey, payloads["1.0.0"]);

        // 3. Upload 1.0.0 and 1.1.0, signed; wait for the patch job.
        Upload(apiKey, keyFile, "1.0.0", payloads["1.0.0"]);
        Upload(apiKey, keyFile, "1.1.0", payloads["1.1.0"]);
        await _server.WaitForPatchJobAsync(AppId, "1.1.0", TimeSpan.FromMinutes(2));

        var before = Snapshot();

        // 4. Install v1.0.0 silently.
        var install = ProcessRunner.Run(installer, ["--silent", "--scope", "user", "--path", _installDir, "--accept-license"]);
        TestContext.Out.WriteLine(install.Output);
        Assert.That(install.ExitCode, Is.Zero, install.Output);
        AssertTree(payloads["1.0.0"]);
        Assert.That(InstalledVersion(), Is.EqualTo("1.0.0"));
        Assert.That(Registry.CurrentUser.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{AppId}"), Is.Not.Null, "ARP entry");
        Assert.That(File.Exists(StartMenuShortcut), Is.True, "Start-menu shortcut");
        Assert.That(File.Exists(Path.Combine(_installDir, "instella.exe")), Is.True, "stub");

        // 5. The app finds the update and starts the updater; the updater restarts it.
        var check = ProcessRunner.Run(Path.Combine(_installDir, AppExe), ["--e2e-check-update"],
            environment: new Dictionary<string, string?> { ["E2E_MARKER_DIR"] = _markers });
        Assert.That(check.ExitCode, Is.Zero, check.Output);
        // On a timeout, the updater's own log says how far the update and the relaunch got.
        var restarted = await WaitForFileAsync(Path.Combine(_markers, "started-1.1.0.txt"), TimeSpan.FromMinutes(3),
            () => File.ReadAllText(Path.Combine(_markers, "check.txt")) + "\n" +
                  string.Join("\n", UpdateLogs().Select(f => $"--- {Path.GetFileName(f)}\n{ProcessRunner.Tail(ReadShared(f), 40)}")));
        Assert.That(restarted, Is.EqualTo("IsPostUpdate=True;PreviousVersion=1.0.0"));
        await WaitForStableAsync();
        Assert.That(InstalledVersion(), Is.EqualTo("1.1.0"));
        AssertTree(payloads["1.1.0"]);

        // 6. Crash recovery: the updater dies after the third commit rename.
        Upload(apiKey, keyFile, "1.2.0", payloads["1.2.0"]);
        await _server.WaitForPatchJobAsync(AppId, "1.2.0", TimeSpan.FromMinutes(2));
        var crashed = RunUpdater(new Dictionary<string, string?> { ["INSTELLA_TEST_FAULT"] = "commit-after:3" });
        Assert.That(crashed.ExitCode, Is.Not.Zero, "the fault hook killed the updater mid-commit:\n" + crashed.Output);
        var recover = ProcessRunner.Run(Path.Combine(_installDir, "instella.exe"), ["--recover", "--path", _installDir]);
        Assert.That(recover.ExitCode, Is.Zero, recover.Output);
        AssertTree(payloads["1.1.0"]);
        Assert.That(InstalledVersion(), Is.EqualTo("1.1.0"));

        // 7. Tamper: a blob the update must download no longer matches the signed release.
        var tampered = Path.Combine(payloads["1.2.0"], "new-in-1.2.txt");
        var blob = await _server.BlobPathAsync(Sha(tampered));
        File.WriteAllText(blob, "not what was signed");
        // Log files are named per second, so this run may append to the crashed run's file:
        // read what each log gained, not only new files.
        var logsBefore = UpdateLogs().ToDictionary(f => f, f => new FileInfo(f).Length);
        var refused = RunUpdater();
        Assert.That(refused.ExitCode, Is.EqualTo(20), refused.Output);
        AssertTree(payloads["1.1.0"]);
        var log = string.Concat(UpdateLogs().Select(f => AppendedText(f, logsBefore.GetValueOrDefault(f))));
        Assert.That(log, Does.Contain("new-in-1.2.txt"), "the log names the file whose hash did not match");

        // 8. Uninstall; the cleanup copy removes the directory.
        var cleanupCopiesBefore = CleanupCopies();
        var uninstall = ProcessRunner.Run(Path.Combine(_installDir, "instella.exe"), ["--uninstall", "--silent"]);
        Assert.That(uninstall.ExitCode, Is.Zero, uninstall.Output);
        await WaitUntilAsync(() => !Directory.Exists(_installDir), TimeSpan.FromMinutes(2), "the install directory is removed");
        Assert.That(Registry.CurrentUser.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{AppId}"), Is.Null, "ARP entry removed");
        Assert.That(File.Exists(StartMenuShortcut), Is.False, "shortcut removed");
        Assert.That(CleanupCopies().Except(cleanupCopiesBefore).Count(), Is.LessThanOrEqualTo(1), "at most one cleanup temp copy");

        // 9. Leak check.
        var after = Snapshot();
        Assert.That(after.Path, Is.EqualTo(before.Path), "user PATH unchanged");
        Assert.That(after.StartMenu.Except(before.StartMenu), Is.Empty, "no new Start-menu entries");
        Assert.That(after.Registry.Except(before.Registry).Where(k => k.Contains(_suffix, StringComparison.OrdinalIgnoreCase)), Is.Empty,
            "no registry keys or values of this app remain");
    }

    // ---- Build ------------------------------------------------------------------------

    /// <summary>Publishes the app once and lays out three releases that differ in data files.</summary>
    private Dictionary<string, string> BuildApp()
    {
        var project = Path.Combine(_work, "E2EApp");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "E2EApp.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <AssemblyName>E2EApp</AssemblyName>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="{Path.Combine(_repo, "src", "Instella.Sdk", "Instella.Sdk.csproj")}" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(project, "Program.cs"), """
            using Instella.Sdk;

            var markers = Environment.GetEnvironmentVariable("E2E_MARKER_DIR");
            var info = InstellaClient.GetCurrentInfo();
            if (markers is not null)
                File.WriteAllText(Path.Combine(markers, $"started-{info.Version}.txt"),
                    $"IsPostUpdate={InstellaClient.IsPostUpdate};PreviousVersion={InstellaClient.PreviousVersion}");

            if (args.Contains("--e2e-check-update"))
            {
                var result = await InstellaClient.CheckForUpdateAsync();
                File.WriteAllText(Path.Combine(markers!, "check.txt"), $"{result.Status};{result.Update?.Version};{result.Error}");
                if (result.Update is { } update)
                    await InstellaClient.StartUpdaterAsync(update, new UpdateOptions { Silent = true, RestartAfterUpdate = true });
            }
            return 0;
            """);

        var publish = Path.Combine(_work, "app-publish");
        ProcessRunner.Dotnet(project, "publish", "-c", "Release", "-o", publish, "-nodeReuse:false",
            "-p:UseArtifactsOutput=true", $"-p:ArtifactsPath={Path.Combine(_work, "artifacts")}");

        var releases = new Dictionary<string, string>();
        foreach (var version in new[] { "1.0.0", "1.1.0", "1.2.0" })
        {
            var dir = Path.Combine(_work, "payload-" + version);
            CopyDirectory(publish, dir);
            File.WriteAllText(Path.Combine(dir, "version.txt"), version);
            File.WriteAllText(Path.Combine(dir, "data", "a.txt"), "a " + version);
            File.WriteAllText(Path.Combine(dir, "data", "b.txt"), "b " + version);
            if (version != "1.2.0")
                File.WriteAllText(Path.Combine(dir, "removed-in-1.2.txt"), "gone in 1.2");
            if (version == "1.2.0")
                File.WriteAllText(Path.Combine(dir, "new-in-1.2.txt"), "new in 1.2");
            releases[version] = dir;
        }
        return releases;
    }

    /// <summary>A single-file, self-contained v1.0.0 installer with the test hooks compiled in.</summary>
    private string BuildInstaller(string publicKey, string payload)
    {
        ProcessRunner.Dotnet(_repo, "build", Path.Combine("src", "Instella.Installer.Build"), "-c", "Release", "-nodeReuse:false");

        var build = Path.Combine(_repo, "src", "Instella.Installer.Build");
        var project = Path.Combine(_work, "E2EInstaller");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "E2E.Installer.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <Import Project="{Path.Combine(build, "build", "Instella.Installer.Build.props")}" />
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <AssemblyName>E2E.Installer</AssemblyName>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <InstellaBuildTasksAssembly>{Path.Combine(build, "bin", "Release", "net10.0", "Instella.Installer.Build.dll")}</InstellaBuildTasksAssembly>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="{Path.Combine(_repo, "src", "Instella.Installer.Runtime", "Instella.Installer.Runtime.csproj")}" />
                <InstellaPayload Include="{payload}" />
              </ItemGroup>
              <Import Project="{Path.Combine(build, "build", "Instella.Installer.Build.targets")}" />
            </Project>
            """);
        File.WriteAllText(Path.Combine(project, "Program.cs"), $$"""
            using Instella.Installer.Runtime.Builders;

            return await InstellaInstaller.Create()
                .WithApp("{{AppName}}", "{{AppId}}", new Version(1, 0, 0))
                .WithServer("{{_server.BaseUrl}}")
                .WithPublisherKey("{{publicKey}}")
                .WithPublisher("Instella E2E")
                .WithExecutableName("{{AppExe}}")
                .WithShortcuts(s => s.StartMenu())
                .AddCliFlag<bool>("accept-license")
                .AddPage("license", p => p.Heading("Licence").CheckBox("agree", "I accept").ContinueWhen(s => s.Bool("agree")))
                .MapCliFlag("accept-license", "license.agree")
                .Build()
                .RunAsync(args);
            """);

        var output = Path.Combine(_work, "installer-out");
        ProcessRunner.Dotnet(project, "publish", "-c", "Release", "-r", "win-x64", "-nodeReuse:false",
            "-p:SelfContained=true", "-p:PublishSingleFile=true", "-p:PublishAot=false", "-p:InstellaTestHooks=true",
            "-p:UseArtifactsOutput=true", $"-p:ArtifactsPath={Path.Combine(_work, "artifacts")}", $"-p:PublishDir={output}{Path.DirectorySeparatorChar}");
        var exe = Path.Combine(output, "E2E.Installer.exe");
        Assert.That(File.Exists(exe), Is.True, "installer published");
        return exe;
    }

    // ---- Server and CLI ---------------------------------------------------------------

    private string Cli(params string[] args)
    {
        var cli = Path.Combine(TestContext.CurrentContext.TestDirectory, "Instella.CLI.dll");
        var result = ProcessRunner.Run("dotnet", new[] { "exec", cli }.Concat(args), _work);
        Assert.That(result.ExitCode, Is.Zero, $"instella {string.Join(' ', args)}:\n{result.Output}");
        return result.Output;
    }

    private void Upload(string apiKey, string keyFile, string version, string payload) =>
        Cli("upload", "--server", _server.BaseUrl, "--api-key", apiKey, "--package", AppId, "--version", version,
            "--path", payload, "--os", "windows", "--arch", "x64", "--signing-key", keyFile);

    /// <summary>Runs the installed stub as the updater, 1.1.0 → 1.2.0, silent, no restart.</summary>
    private ProcessRunner.Result RunUpdater(IReadOnlyDictionary<string, string?>? environment = null)
    {
        var args = new UpdaterArgs
        {
            AppPath = _installDir, AppExecutable = AppExe,
            FromVersion = new Version(1, 1, 0), ToVersion = new Version(1, 2, 0),
            Silent = true, Restart = false,
        }.ToArgumentList();
        return ProcessRunner.Run(Path.Combine(_installDir, "instella.exe"), args, environment: environment);
    }

    // ---- Assertions -------------------------------------------------------------------

    /// <summary>The installed payload files equal <paramref name="release"/> exactly (Instella's own files aside).</summary>
    private void AssertTree(string release)
    {
        var expected = Files(release).ToDictionary(f => f, f => Sha(Path.Combine(release, f)));
        var actual = Files(_installDir).Where(f => !IsOwned(f)).ToDictionary(f => f, f => Sha(Path.Combine(_installDir, f)));
        Assert.That(actual.Keys, Is.EquivalentTo(expected.Keys), "installed file set");
        Assert.That(actual.Where(kv => expected[kv.Key] != kv.Value).Select(kv => kv.Key), Is.Empty, "file contents");
    }

    private string InstalledVersion()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_installDir, ".instella-manifest.json")));
        return doc.RootElement.GetProperty("version").GetString()!;
    }

    private static bool IsOwned(string relative) =>
        relative.Equals("instella.exe", StringComparison.OrdinalIgnoreCase)
        || relative.Equals(".instella-manifest.json", StringComparison.OrdinalIgnoreCase)
        || relative.StartsWith(".instella/", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> Files(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'));

    private static string Sha(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private static string AppendedText(string path, long from)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        stream.Position = Math.Min(from, stream.Length);
        return new StreamReader(stream).ReadToEnd();
    }

    /// <summary>Reads a file another process may still be writing (the updater keeps its log open).</summary>
    private static string ReadShared(string path)
    {
        using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        return reader.ReadToEnd();
    }

    private IEnumerable<string> UpdateLogs() =>
        Directory.EnumerateFiles(Path.GetTempPath(), $"instella-update-*{_suffix}*.log");

    private static IEnumerable<string> CleanupCopies()
    {
        var dir = Path.Combine(Path.GetTempPath(), "Instella", "cleanup");
        return Directory.Exists(dir) ? Directory.EnumerateFiles(dir).ToList() : [];
    }

    private sealed record UserState(string? Path, IReadOnlyList<string> StartMenu, IReadOnlyList<string> Registry);

    private static UserState Snapshot()
    {
        using var env = Registry.CurrentUser.OpenSubKey("Environment");
        var path = env?.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        var startMenu = Directory.EnumerateFiles(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "*", SearchOption.AllDirectories).ToList();

        var registry = new List<string>();
        void Keys(string path)
        {
            using var key = Registry.CurrentUser.OpenSubKey(path);
            if (key is null) return;
            registry.AddRange(key.GetSubKeyNames().Select(n => $@"{path}\{n}"));
            registry.AddRange(key.GetValueNames().Select(n => $@"{path}:{n}"));
        }
        Keys("Software");
        Keys(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
        Keys(@"Software\Microsoft\Windows\CurrentVersion\Run");
        Keys(@"Software\Classes");
        return new UserState(path, startMenu, registry);
    }

    // ---- Helpers ----------------------------------------------------------------------

    private async Task<string> WaitForFileAsync(string path, TimeSpan timeout, Func<string> diagnostics)
    {
        await WaitUntilAsync(() => File.Exists(path), timeout, $"{Path.GetFileName(path)} (check: {Safe(diagnostics)})");
        await Task.Delay(200);
        return File.ReadAllText(path);
    }

    private static string Safe(Func<string> f)
    {
        try { return f(); } catch (Exception ex) { return ex.Message; }
    }

    /// <summary>The updater may still be finishing (transaction clean-up) after the app restarted.</summary>
    private async Task WaitForStableAsync() =>
        await WaitUntilAsync(() => !Directory.Exists(Path.Combine(_installDir, ".instella", "txn")), TimeSpan.FromMinutes(1),
            "the update transaction is cleaned up");

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string what)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail($"timed out waiting: {what}");
            await Task.Delay(250);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        Directory.CreateDirectory(Path.Combine(destination, "data"));
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string FindRepo()
    {
        var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Instella.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
