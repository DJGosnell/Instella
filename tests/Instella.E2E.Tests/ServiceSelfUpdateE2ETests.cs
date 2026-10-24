using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text.Json;
using NUnit.Framework;

namespace Instella.E2E.Tests;

/// <summary>
/// A Windows service that updates itself: a generated worker service (Assets/ServiceApp, referencing
/// Instella.Sdk) is installed machine-wide as a LocalSystem service, finds 1.1.0 on the real server
/// and calls <c>InstellaClient.StartServiceUpdateAsync</c>; the updater stops the service, swaps the
/// files and starts it again, now running 1.1.0. Needs an elevated run (it registers a service).
/// </summary>
[TestFixture]
[Category("E2E")]
[Explicit("Installs a machine-wide Windows service; run elevated through verify.ps1 -Stage E2E")]
[Platform("Win")]
[SupportedOSPlatform("windows")]
[NonParallelizable]
public sealed class ServiceSelfUpdateE2ETests
{
    private const string AppExe = "E2EService.exe";
    private const string Publisher = "Instella E2E";
    private readonly string _suffix = Guid.NewGuid().ToString("N")[..8];
    private string AppId => $"com.instella.e2e.svc.{_suffix}";
    private string AppName => $"InstellaE2ESvc{_suffix}";
    private string ServiceName => $"Instella.E2E.Svc.{_suffix}";

    private string _repo = null!;
    private string _work = null!;
    private string _markers = null!;
    private E2EServer? _server;

    /// <summary>The machine-wide default install folder: {ProgramFiles}\{publisher}\{app}.</summary>
    private string InstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Publisher, AppName);

    [OneTimeSetUp]
    public void SetUp()
    {
        if (!Environment.IsPrivilegedProcess)
            Assert.Ignore("registering a Windows service needs an elevated test run");
        _repo = FindRepo();
        _work = Directory.CreateTempSubdirectory("instella-e2e-svc-").FullName;
        // The service runs as LocalSystem: it can write here whatever the folder's ACL.
        _markers = Path.Combine(_work, "markers");
        Directory.CreateDirectory(_markers);
        _server = new E2EServer(Path.Combine(_work, "server"), FreePort());
        _server.StartServer();
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        _server?.Dispose();
        if (_work is null) return;
        // Best effort: never leave a service behind, even when the uninstall failed.
        try { ProcessRunner.Run("sc.exe", ["stop", ServiceName], timeout: TimeSpan.FromMinutes(1)); } catch { }
        try { ProcessRunner.Run("sc.exe", ["delete", ServiceName], timeout: TimeSpan.FromMinutes(1)); } catch { }
        if (Environment.GetEnvironmentVariable("INSTELLA_E2E_KEEP") != "1")
            try { Directory.Delete(_work, recursive: true); } catch { }
        else
            TestContext.Out.WriteLine($"E2E work directory kept: {_work}");
    }

    [Test]
    public async Task Service_UpdatesItself_AndRunsTheNewVersion()
    {
        var server = _server!;
        var apiKey = await server.SeedPackageAndKeyAsync(AppId);
        var keyFile = Path.Combine(_work, "publisher.key.pem");
        var keys = Cli("keys", "generate", "--out", keyFile);
        var publicKey = keys.Split('\n').Select(l => l.Trim()).First(l => l.StartsWith("Public key:", StringComparison.Ordinal))["Public key:".Length..].Trim();

        var payloads = BuildService();
        var installer = BuildInstaller(publicKey, payloads["1.0.0"]);

        try
        {
            // 1. Install v1.0.0 machine-wide; the Manual service is registered, not started.
            Upload(apiKey, keyFile, "1.0.0", payloads["1.0.0"]);
            var install = ProcessRunner.Run(installer, ["--silent", "--scope", "machine"]);
            Assert.That(install.ExitCode, Is.Zero, install.Output);
            Assert.That(InstalledVersion(), Is.EqualTo("1.0.0"));

            // 2. Upload v1.1.0, then start the service: it finds the update and starts the updater.
            Upload(apiKey, keyFile, "1.1.0", payloads["1.1.0"]);
            await server.WaitForPatchJobAsync(AppId, "1.1.0", TimeSpan.FromMinutes(2));
            var start = ProcessRunner.Run("sc.exe", ["start", ServiceName]);
            Assert.That(start.ExitCode, Is.Zero, start.Output);

            var running = await WaitForFileAsync("running-1.0.0.txt", TimeSpan.FromMinutes(1));
            Assert.That(running, Is.EqualTo("IsRunningAsService=True"));
            var started = await WaitForFileAsync("start-1.0.0.txt", TimeSpan.FromMinutes(1));
            Assert.That(started, Does.StartWith("IsStarted=True;"), started);

            // 3. The updater stopped the service, updated and started it again: 1.1.0 runs.
            var updated = await WaitForFileAsync("running-1.1.0.txt", TimeSpan.FromMinutes(3));
            Assert.That(updated, Is.EqualTo("IsRunningAsService=True"));
            await WaitUntilAsync(() => !Directory.Exists(Path.Combine(InstallDir, ".instella", "txn")), TimeSpan.FromMinutes(1),
                "the update transaction is cleaned up");
            Assert.That(InstalledVersion(), Is.EqualTo("1.1.0"));
            Assert.That(File.ReadAllText(Path.Combine(InstallDir, "version.txt")), Is.EqualTo("1.1.0"));
        }
        finally
        {
            // 4. Uninstall stops and deletes the service.
            var stub = Path.Combine(InstallDir, "instella.exe");
            if (File.Exists(stub))
            {
                var uninstall = ProcessRunner.Run(stub, ["--uninstall", "--silent"]);
                TestContext.Out.WriteLine(uninstall.Output);
            }
        }
    }

    // ---- Build ------------------------------------------------------------------------

    /// <summary>Publishes the worker service once and lays out releases 1.0.0 and 1.1.0.</summary>
    private Dictionary<string, string> BuildService()
    {
        var project = Path.Combine(_work, "E2EService");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "E2EService.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <AssemblyName>E2EService</AssemblyName>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.Extensions.Hosting.WindowsServices" Version="10.0.0" />
                <ProjectReference Include="{Path.Combine(_repo, "src", "Instella.Sdk", "Instella.Sdk.csproj")}" />
              </ItemGroup>
            </Project>
            """);
        File.Copy(Path.Combine(_repo, "tests", "Instella.E2E.Tests", "Assets", "ServiceApp", "Program.cs"), Path.Combine(project, "Program.cs"));

        var publish = Path.Combine(_work, "service-publish");
        ProcessRunner.Dotnet(project, "publish", "-c", "Release", "-o", publish, "-nodeReuse:false",
            "-p:UseArtifactsOutput=true", $"-p:ArtifactsPath={Path.Combine(_work, "artifacts")}");

        var releases = new Dictionary<string, string>();
        foreach (var version in new[] { "1.0.0", "1.1.0" })
        {
            var dir = Path.Combine(_work, "payload-" + version);
            foreach (var file in Directory.EnumerateFiles(publish, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(dir, Path.GetRelativePath(publish, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
            File.WriteAllText(Path.Combine(dir, "version.txt"), version);
            releases[version] = dir;
        }
        return releases;
    }

    /// <summary>A single-file, self-contained v1.0.0 installer declaring the LocalSystem service.</summary>
    private string BuildInstaller(string publicKey, string payload)
    {
        ProcessRunner.Dotnet(_repo, "build", Path.Combine("src", "Instella.Installer.Build"), "-c", "Release", "-nodeReuse:false");

        var build = Path.Combine(_repo, "src", "Instella.Installer.Build");
        var project = Path.Combine(_work, "E2EServiceInstaller");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "E2E.ServiceInstaller.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <Import Project="{Path.Combine(build, "build", "Instella.Installer.Build.props")}" />
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <AssemblyName>E2E.ServiceInstaller</AssemblyName>
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
                .WithServer("{{_server!.BaseUrl}}")
                .WithPublisherKey("{{publicKey}}")
                .WithPublisher("{{Publisher}}")
                .WithExecutableName("{{AppExe}}")
                .WithElevation(ElevationMode.SystemWide)
                .OnWindows(w => w.AddService("{{ServiceName}}", s => s
                    .WithArguments(@"{{_markers}}")
                    .WithStartType(ServiceStartMode.Manual)
                    .RunAs(ServiceAccount.LocalSystem)))
                .Build()
                .RunAsync(args);
            """);

        var output = Path.Combine(_work, "installer-out");
        ProcessRunner.Dotnet(project, "publish", "-c", "Release", "-r", "win-x64", "-nodeReuse:false",
            "-p:SelfContained=true", "-p:PublishSingleFile=true", "-p:PublishAot=false",
            "-p:UseArtifactsOutput=true", $"-p:ArtifactsPath={Path.Combine(_work, "artifacts")}", $"-p:PublishDir={output}{Path.DirectorySeparatorChar}");
        var exe = Path.Combine(output, "E2E.ServiceInstaller.exe");
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
        Cli("upload", "--server", _server!.BaseUrl, "--api-key", apiKey, "--package", AppId, "--version", version,
            "--path", payload, "--os", "windows", "--arch", "x64", "--signing-key", keyFile);

    // ---- Helpers ----------------------------------------------------------------------

    private string InstalledVersion()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(InstallDir, ".instella-manifest.json")));
        return doc.RootElement.GetProperty("version").GetString()!;
    }

    /// <summary>Waits for a marker the service writes; on a timeout, says what the service and the updater left.</summary>
    private async Task<string> WaitForFileAsync(string name, TimeSpan timeout)
    {
        var path = Path.Combine(_markers, name);
        await WaitUntilAsync(() => File.Exists(path), timeout,
            $"{name}; markers: {string.Join(", ", Directory.EnumerateFiles(_markers).Select(f => $"{Path.GetFileName(f)}={File.ReadAllText(f)}"))}");
        await Task.Delay(200);
        return File.ReadAllText(path);
    }

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
