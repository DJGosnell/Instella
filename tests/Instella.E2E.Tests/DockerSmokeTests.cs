using System.Net;
using System.Net.Sockets;
using Instella.Server.Data;
using Instella.Server.Data.Entities;
using Instella.Server.Services;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace Instella.E2E.Tests;

/// <summary>
/// The server image starts with empty volumes, keeps its database, keys and
/// setup token in <c>/config</c> and its blobs in <c>/packages</c>, answers <c>/healthz</c>,
/// and an uploaded package survives a container restart.
/// </summary>
/// <remarks>
/// The setup page is an interactive (SignalR) Blazor page, so instead of driving it this test
/// checks the setup token was created in the volume, then seeds the admin, package and API
/// key into the volume's database from the host while the container is stopped.
/// </remarks>
[TestFixture]
[Category("Docker")]
[Explicit("Builds and runs a Docker image; run through verify.ps1 -Stage Docker")]
[NonParallelizable]
public sealed class DockerSmokeTests
{
    private readonly string _suffix = Guid.NewGuid().ToString("N")[..8];
    private string Image => $"instella-server:smoke-{_suffix}";
    private string Container => $"instella-smoke-{_suffix}";
    private string _work = null!;

    [OneTimeSetUp]
    public void SetUp()
    {
        ProcessRunner.Result docker;
        try
        {
            docker = ProcessRunner.Run("docker", ["version", "--format", "{{.Server.Version}}"], timeout: TimeSpan.FromMinutes(1));
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Assert.Ignore("docker is not installed: " + ex.Message);
            return;
        }
        if (docker.ExitCode != 0)
            Assert.Ignore("docker is not available: " + docker.Output.Trim());
        _work = Directory.CreateTempSubdirectory("instella-docker-").FullName;
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        if (_work is null) return;
        ProcessRunner.Run("docker", ["rm", "-f", Container]);
        ProcessRunner.Run("docker", ["rmi", "-f", Image]);
        try { Directory.Delete(_work, recursive: true); } catch { }
    }

    [Test]
    public async Task ImageStartsEmpty_KeepsStateInVolumes_AndSurvivesARestart()
    {
        var repo = FindRepo();

        // Build the image the way scripts/build.ps1 does.
        var context = Path.Combine(_work, "context");
        ProcessRunner.Dotnet(repo, "publish", Path.Combine("src", "Instella.Server"), "-c", "Release", "-o", Path.Combine(context, "publish"), "-nodeReuse:false");
        Docker("build", "-t", Image, "-f", Path.Combine(repo, "src", "Instella.Server", "Dockerfile"), context);

        // A fixed uid, so host volumes can be prepared with chown -R 1654:1654.
        var id = ProcessRunner.Run("docker", ["run", "--rm", "--entrypoint", "id", Image], timeout: TimeSpan.FromMinutes(2));
        Assert.That(id.Output, Does.Contain("uid=1654").And.Contain("gid=1654"), ProcessRunner.Tail(id.Output));

        var config = Directory.CreateDirectory(Path.Combine(_work, "config")).FullName;
        var packages = Directory.CreateDirectory(Path.Combine(_work, "packages")).FullName;
        if (!OperatingSystem.IsWindows())
        {
            // The container runs as a non-root user; bind mounts keep the host's ownership.
            foreach (var dir in new[] { config, packages })
                File.SetUnixFileMode(dir, (UnixFileMode)0x1FF);
        }

        var port = FreePort();
        Docker("run", "-d", "--name", Container, "-p", $"127.0.0.1:{port}:8080",
            "-v", $"{config}:/config", "-v", $"{packages}:/packages", Image);
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        await WaitHealthyAsync(http);

        Assert.That(File.Exists(Path.Combine(config, "instella.db")), Is.True, "the database lives in /config");
        Assert.That(Directory.Exists(Path.Combine(config, "keys")), Is.True, "Data Protection keys live in /config");
        Assert.That(File.Exists(Path.Combine(config, "setup-token")), Is.True, "a first-run setup token was issued");

        // Stand in for completing setup: admin, package and API key, written while stopped.
        Docker("stop", Container);
        // On Linux the database belongs to the container's user (1654) and is not writable by the
        // user running this test. Open /config up for the seeding, then give everything back to
        // 1654, including any SQLite side files the host creates. Docker Desktop on Windows does
        // not apply Linux ownership to bind mounts, so there this is a no-op.
        AsRoot(config, "chmod", "-R", "a+rwX", "/config");
        var packageId = $"com.instella.smoke.{_suffix}";
        string apiKey;
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={Path.Combine(config, "instella.db")};Pooling=False").Options;
        await using (var db = new AppDbContext(options))
        {
            var auth = new AuthService(db);
            await auth.CreateAdminUserAsync("admin", "correct horse battery staple");
            db.Packages.Add(new Package { PackageId = packageId, DisplayName = packageId, DownloadAccessMode = DownloadAccessMode.Open, CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
            (_, apiKey) = await auth.CreateApiKeyAsync("smoke", ApiKeyScope.Admin, canUpload: true, canDownload: true, canManageVersions: true);
        }
        File.Delete(Path.Combine(config, "setup-token"));
        AsRoot(config, "chown", "-R", "1654:1654", "/config");
        Docker("start", Container);
        await WaitHealthyAsync(http);

        // Upload with the CLI.
        var files = Directory.CreateDirectory(Path.Combine(_work, "release")).FullName;
        File.WriteAllText(Path.Combine(files, "app.txt"), "smoke " + _suffix);
        var cli = Path.Combine(TestContext.CurrentContext.TestDirectory, "Instella.CLI.dll");
        var upload = ProcessRunner.Run("dotnet", ["exec", cli, "upload", "--server", http.BaseAddress!.ToString().TrimEnd('/'),
            "--api-key", apiKey, "--package", packageId, "--version", "1.0.0", "--path", files, "--os", "windows", "--arch", "x64", "--unsigned"]);
        Assert.That(upload.ExitCode, Is.Zero, upload.Output);

        // Restart; the package is still there, blob included.
        Docker("restart", Container);
        await WaitHealthyAsync(http);
        var versions = await http.GetStringAsync($"/api/v1/packages/{packageId}/versions");
        Assert.That(versions, Does.Contain("1.0.0"));
        Assert.That(Directory.EnumerateFiles(packages, "*", SearchOption.AllDirectories), Is.Not.Empty, "blobs live in /packages");
    }

    /// <summary>Runs a command as root in a throwaway container of the image, with <paramref name="config"/> at /config.</summary>
    private void AsRoot(string config, params string[] command) =>
        Docker(["run", "--rm", "--user", "0", "-v", $"{config}:/config", "--entrypoint", command[0], Image, .. command[1..]]);

    private static void Docker(params string[] args)
    {
        var result = ProcessRunner.Run("docker", args, timeout: TimeSpan.FromMinutes(10));
        Assert.That(result.ExitCode, Is.Zero, $"docker {string.Join(' ', args)}:\n{ProcessRunner.Tail(result.Output)}");
    }

    private async Task WaitHealthyAsync(HttpClient http)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);
        while (true)
        {
            try
            {
                using var response = await http.GetAsync("/healthz");
                if (response.StatusCode == HttpStatusCode.OK)
                    return;
            }
            catch (HttpRequestException)
            {
                // Not listening yet.
            }
            if (DateTime.UtcNow > deadline)
            {
                var logs = ProcessRunner.Run("docker", ["logs", Container]);
                Assert.Fail("/healthz did not answer 200:\n" + ProcessRunner.Tail(logs.Output));
            }
            await Task.Delay(500);
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
