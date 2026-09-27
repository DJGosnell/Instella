using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Installation;
using Instella.Core.Logging;
using Instella.Core.Manifest;
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Runtime.Core;
using Instella.Installer.Runtime.Installation;
using Instella.Installer.Runtime.Runners;
using Instella.Installer.Runtime.UI.Widgets;
using Instella.Installer.Testing;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Installation;

/// <summary>
/// The builder's icon is a build-machine path; the build embeds it as <c>.instella/app.ico</c>,
/// and the running installer must use that, never the build path (found running the
/// QuickNotes sample: <c>../SampleApp/Assets/icon.ico</c> failed every install with an
/// UnsafePathException in create-shortcuts).
/// </summary>
[TestFixture]
public class AppIconTests
{
    private const string BuildRelativeIcon = "../SampleApp/Assets/icon.ico";

    [Test]
    public async Task BuildRelativeIcon_ResolvesToTheEmbeddedIcon_ForShortcutsAssociationsAndArp()
    {
        var (result, platform, root) = await InstallAsync(embedIcon: true);

        Assert.That(result.Success, Is.True, result.Error);
        var embedded = Path.Combine(root, ".instella", "app.ico");
        Assert.That(platform.Shortcuts.Select(s => s.IconPath), Is.All.EqualTo(embedded).And.Not.Empty);
        Assert.That(platform.FileAssociations.Single(a => a.Extension == ".qnote").IconPath, Is.EqualTo(embedded),
            "an association that reuses the app icon's source gets the embedded icon");
        Assert.That(platform.FileAssociations.Single(a => a.Extension == ".qn").IconPath, Is.EqualTo(embedded),
            "an association without an icon falls back to the app icon");
        Assert.That(platform.UninstallEntries.Single().DisplayIcon, Is.EqualTo(embedded));
    }

    [Test]
    public async Task PayloadWithoutTheEmbeddedIcon_InstallsWithoutAnIcon()
    {
        // A lite installer's server payload never carries Instella-owned files.
        var (result, platform, root) = await InstallAsync(embedIcon: false);

        Assert.That(result.Success, Is.True, result.Error);
        Assert.That(platform.Shortcuts.Select(s => s.IconPath), Is.All.Null);
        Assert.That(platform.UninstallEntries.Single().DisplayIcon, Is.EqualTo(Path.Combine(root, "QuickNotes.exe")));
    }

    [Test]
    public async Task ExecutableNameWithoutExtension_GetsExeOnWindows_EverywhereItIsRegistered()
    {
        // Found installing the QuickNotes sample: WithExecutableName("QuickNotes") made the
        // shortcuts, the .qnote association and the auto-start entry point at a missing file.
        if (!OperatingSystem.IsWindows()) Assert.Ignore("the .exe rule is Windows-only");
        var (result, platform, root) = await InstallAsync(embedIcon: true, executableName: "QuickNotes");

        Assert.That(result.Success, Is.True, result.Error);
        var exe = Path.Combine(root, "QuickNotes.exe");
        Assert.That(platform.Shortcuts.Select(s => s.TargetPath), Is.All.EqualTo(exe).And.Not.Empty);
        Assert.That(platform.FileAssociations.Select(a => a.ExecutablePath), Is.All.EqualTo(exe));
        Assert.That(platform.UninstallEntries.Single().InstallLocation, Is.EqualTo(root));
    }

    private static async Task<(ExecutionResult Result, FakePlatformServices Platform, string Root)> InstallAsync(
        bool embedIcon, string executableName = "QuickNotes.exe")
    {
        var fs = new InMemoryFileSystem();
        var platform = new FakePlatformServices();
        var root = Path.Combine(Path.GetTempPath(), "instella-icon-tests", Guid.NewGuid().ToString("N"), "QuickNotes");
        var icon = ImageSource.FromFile(BuildRelativeIcon);
        var config = ((InstellaInstallerImpl)InstellaInstaller.Create()
            .WithApp("QuickNotes", "com.test.quicknotes", new Version(1, 0))
            .WithExecutableName(executableName)
            .WithIcon(icon)
            .WithShortcuts(s => s.Desktop().StartMenu())
            .WithFileAssociation(".qnote", "QuickNotes Document", icon)
            .WithFileAssociation(".qn", "QuickNotes File")
            .Build()).ConfigForTests;

        var payload = new Dictionary<string, string> { ["QuickNotes.exe"] = "exe" };
        if (embedIcon) payload[InstellaOwnedPaths.AppIcon] = "ico";
        var options = new InstallOptions
        {
            InstallPath = root, CreateDesktopShortcut = true, CreateStartMenuShortcut = true, AddToPath = false,
            ConfigureAutoStart = false, RegisterFileAssociations = true, Elevation = ElevationMode.PerUser,
        };
        var context = InstallContextFactory.Create(config, InstallerMode.FirstInstall, root, options, platform, fs, new NullLog(),
            payload: Zip(payload));
        var result = await new StepExecutor(OfflineInstallRunner.BuildDefaultSteps()).ExecuteAsync(context, null, CancellationToken.None);
        return (result, platform, root);
    }

    private static MemoryStream Zip(Dictionary<string, string> files)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, text) in files)
            {
                using var s = zip.CreateEntry(path).Open();
                s.Write(Encoding.UTF8.GetBytes(text));
            }
        }
        ms.Position = 0;
        return ms;
    }

    private sealed class NullLog : IInstellaLogger
    {
        public void Trace(string message) { }
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
        public IDisposable Scope(string segment) => new MemoryStream();
        public bool IsEnabled(InstellaLogLevel level) => false;
    }
}
