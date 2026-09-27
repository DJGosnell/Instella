using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Manifest;
using Instella.Core.Platform;
using Instella.Installer.Runtime.Installation;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Installation;

/// <summary>
/// End to end: pour a tiny zip through the runner, assert every
/// Register-stage platform call happened, and assert the manifest was
/// written. <see cref="StageUninstallerStubStep"/> is exercised but expected
/// to warn (no running installer exe in unit-test context) and therefore
/// <see cref="Instella.Installer.Runtime.Installation.BuiltIn.RegisterUninstallEntryStep"/>
/// short-circuits — matching the documented graceful degradation path.
/// </summary>
[TestFixture]
public class OfflineInstallRunnerTests
{
    [Test]
    public async Task Full_Install_Writes_Files_Calls_Platform_And_Writes_Manifest()
    {
        var manifest = new InstellaManifest
        {
            AppName = "QuickNotes",
            AppId = "com.example.quicknotes",
            Version = new Version(1, 2, 0),
            Publisher = "Example",
            ServerUrl = "https://example.com",
            ExecutableName = "QuickNotes",
            Shortcuts = new ShortcutConfig(Desktop: true, StartMenu: false),
            PathRegistration = false,
        };

        var platform = new StubPlatformServices();
        var fs = new StubFileSystem();
        var installPath = Path.Combine(Path.GetTempPath(), $"instella-runner-{Guid.NewGuid():N}");

        var options = new Instella.Installer.Runtime.Core.InstallOptions
        {
            InstallPath = installPath,
            CreateDesktopShortcut = true,
            CreateStartMenuShortcut = false,
            AddToPath = false,
            ConfigureAutoStart = false,
            RegisterFileAssociations = false,
            Elevation = ElevationMode.PerUser,
        };

        using var archive = BuildTinyArchive(("QuickNotes.exe", "binary-data"), ("readme.txt", "docs"));

        var runner = new OfflineInstallRunner(manifest, platform, fs);
        var result = await runner.RunAsync(archive, options, progress: null, CancellationToken.None);

        Assert.That(result.Success, Is.True, result.Error);
        Assert.That(fs.Files.Keys, Has.Some.Contains("QuickNotes.exe"));
        Assert.That(fs.Files.Keys, Has.Some.Contains("readme.txt"));

        // Platform integration: exactly one desktop shortcut (start-menu off).
        Assert.That(platform.Calls, Has.Some.StartsWith("CreateShortcut:QuickNotes:Desktop"));
        Assert.That(platform.Calls, Has.None.StartsWith("CreateShortcut:QuickNotes:StartMenu"));

        // Manifest is written.
        var manifestFile = Path.Combine(installPath, ".instella-manifest.json");
        Assert.That(fs.Files.ContainsKey(manifestFile), Is.True);
    }

    [Test]
    public async Task Missing_Payload_Fails_Without_Side_Effects()
    {
        var platform = new StubPlatformServices();
        var fs = new StubFileSystem();
        var manifest = new InstellaManifest
        {
            AppName = "NoPayload",
            AppId = "np",
            Version = new Version(1, 0, 0),
            ServerUrl = "x",
            ExecutableName = "NoPayload",
        };
        var installPath = Path.Combine(Path.GetTempPath(), $"instella-nopayload-{Guid.NewGuid():N}");
        var options = new Instella.Installer.Runtime.Core.InstallOptions
        {
            InstallPath = installPath,
            Elevation = ElevationMode.PerUser,
        };

        var runner = new OfflineInstallRunner(manifest, platform, fs);
        // PayloadArchive is required for ExtractPayloadStep; passing null
        // stream exposes the fail-fast path.
        var result = await runner.RunAsync(Stream.Null, options, progress: null, CancellationToken.None);

        // Stream.Null as a ZIP archive is invalid — step either throws or
        // fails, and the runner maps that to Failed. Platform integrations
        // should not have been called.
        Assert.That(result.Success, Is.False);
        Assert.That(platform.Calls, Has.None.StartsWith("CreateShortcut"));
    }

    private static MemoryStream BuildTinyArchive(params (string Name, string Content)[] entries)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = zip.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
                writer.Write(content);
            }
        }
        ms.Position = 0;
        return ms;
    }
}
