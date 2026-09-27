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
using Instella.Core.Platform;
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Runtime.Core;
using Instella.Installer.Runtime.Installation;
using Instella.Installer.Runtime.Installation.BuiltIn;
using Instella.Installer.Runtime.Runners;
using Instella.Installer.Testing;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Installation;

/// <summary>
/// Uninstall of an installation that used every registration feature leaves no
/// trace in the fake platform (snapshot before install equals snapshot after uninstall).
/// </summary>
[TestFixture]
public class UninstallCompletenessTests
{
    [TestCase(ElevationMode.PerUser)]
    [TestCase(ElevationMode.SystemWide)]
    public async Task UninstallAfterFullInstall_LeavesNoTrace(ElevationMode elevation)
    {
        var fs = new InMemoryFileSystem();
        var platform = new FakePlatformServices();
        var root = Path.Combine(Path.GetTempPath(), "instella-uninstall-tests", Guid.NewGuid().ToString("N"), "QuickNotes");
        var outside = Path.Combine(Path.GetTempPath(), "instella-uninstall-tests", Guid.NewGuid().ToString("N"), "tool.cfg");
        var hookRan = false;

        var config = ((InstellaInstallerImpl)InstellaInstaller.Create()
            .WithApp("QuickNotes", "com.test.quicknotes", new Version(1, 0))
            .WithExecutableName("QuickNotes.exe")
            .WithElevation(elevation)
            .WithShortcuts(s => s.Desktop().StartMenu())
            .WithFileAssociation(".qnote", "QuickNotes note")
            .WithPathRegistration()
            .WithAutoStart()
            .OnWindows(w => w.AddRegistryKey(RegistryHive.CurrentUser, @"Software\QuickNotes\Settings", k => k.SetString("Theme", "dark")))
            .AddStep("write-config", s => s
                .InStage(InstallStage.Finalize)
                .Execute(async (ctx, p, ct) =>
                {
                    await ctx.FileSystem.WriteAllBytesAsync(outside, Encoding.UTF8.GetBytes("cfg"), ct);
                    ctx.TrackFile(outside);
                    await ctx.Platform.WriteRegistryValueAsync(RegistryHive.CurrentUser, @"Software\QuickNotesTool", "Enabled",
                        InstellaRegistryValueKind.DWord, 1, true, ct);
                    ctx.TrackRegistryValue(RegistryHive.CurrentUser, @"Software\QuickNotesTool", "Enabled");
                    return StepResult.Ok;
                })
                .UseTrackedRollback()
                .OnUninstall((ctx, ct) => { hookRan = true; return Task.CompletedTask; }))
            .Build()).ConfigForTests;

        var before = platform.Snapshot();

        // Install through the real built-in pipeline on the fakes.
        var options = new InstallOptions
        {
            InstallPath = root,
            CreateDesktopShortcut = true,
            CreateStartMenuShortcut = true,
            AddToPath = true,
            ConfigureAutoStart = true,
            RegisterFileAssociations = true,
            Elevation = elevation,
        };
        var context = InstallContextFactory.Create(config, InstallerMode.FirstInstall, root, options, platform, fs, new NullLog(),
            payload: Zip(new() { ["QuickNotes.exe"] = "exe", ["lib/core.dll"] = "dll" }));
        var builtIns = new List<IInstallStepExecution>(OfflineInstallRunner.BuildDefaultSteps());
        if (config.RegistryWrites.Count > 0) builtIns.Add(new WriteRegistrySpecsStep(config.RegistryWrites));
        var install = await new StepExecutor(StepOrdering.BuildOrderedSteps(builtIns, config.UserSteps))
            .ExecuteAsync(context, null, CancellationToken.None);
        Assert.That(install.Success, Is.True, install.Error);
        Assert.That(platform.Snapshot().SameAs(before), Is.False, "the install registered something");

        var manifest = (await new InstallManifestWriter(fs).ReadAsync(root, CancellationToken.None))!;
        Assert.That(manifest.TrackedItems!.Select(t => t.Kind), Is.EquivalentTo(new[] { "file", "registry-value" }),
            "a custom Finalize step's tracked items reach the manifest even though it ran after the commit");
        Assert.That(manifest.InstalledPerUser, Is.EqualTo(elevation == ElevationMode.PerUser));

        var exit = await new UninstallModeRunner(config, new NullLog(), platform, fs)
            .RunAsync(new DispatchResult(DispatchKind.Mode, InstallerMode.Uninstall, root, IsSilent: true), CancellationToken.None);

        Assert.That(exit, Is.EqualTo(InstellaExitCode.Success));
        var after = platform.Snapshot();
        Assert.That(after.SameAs(before), Is.True, $"left behind: {after}");
        Assert.That(hookRan, Is.True, "OnUninstall ran");
        Assert.That(fs.Exists(outside), Is.False, "a tracked file outside the install dir is removed");
        Assert.That(fs.EnumerateFiles(root, "*", recursive: true).Where(f => !f.EndsWith(InstellaOwnedPaths.StubFileName)), Is.Empty);
    }

    [Test]
    public async Task ManifestEntryOutsideTheFolder_IsNotDeleted_AndIsNamedInTheLog()
    {
        var fs = new InMemoryFileSystem();
        var parent = Path.Combine(Path.GetTempPath(), "instella-uninstall-tests", Guid.NewGuid().ToString("N"));
        var root = Path.Combine(parent, "a", "App");
        var evil = Path.Combine(parent, "evil.txt");
        fs.AddFile(evil, Encoding.UTF8.GetBytes("keep me"));
        fs.AddFile(Path.Combine(root, "App.exe"), Encoding.UTF8.GetBytes("exe"));
        await new InstallManifestWriter(fs).WriteAsync(root, new InstalledManifest
        {
            AppName = "App", AppId = "com.app", Version = new Version(1, 0), InstallDirectory = root, ExecutableName = "App.exe",
            InstalledAt = DateTime.UtcNow, InstalledPerUser = true,
            Files = [new InstalledFile("App.exe", new string('0', 64), 3), new InstalledFile(@"..\..\evil.txt", new string('0', 64), 7)],
        }, CancellationToken.None);
        var config = ((InstellaInstallerImpl)InstellaInstaller.Create().WithApp("App", "com.app", new Version(1, 0)).Build()).ConfigForTests;
        var log = new RecordingLog();

        await new UninstallModeRunner(config, log, new FakePlatformServices(), fs)
            .RunAsync(new DispatchResult(DispatchKind.Mode, InstallerMode.Uninstall, root, IsSilent: true), CancellationToken.None);

        Assert.That(fs.Exists(evil), Is.True, "an entry that escapes the install folder is never deleted");
        Assert.That(fs.Exists(Path.Combine(root, "App.exe")), Is.False);
        Assert.That(log.Warnings, Has.Some.Contains(@"..\..\evil.txt"));
    }

    private sealed class RecordingLog : IInstellaLogger
    {
        public List<string> Warnings { get; } = [];
        public void Trace(string message) { }
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warn(string message) => Warnings.Add(message);
        public void Error(string message, Exception? exception = null) { }
        public IDisposable Scope(string segment) => new MemoryStream();
        public bool IsEnabled(InstellaLogLevel level) => true;
    }

    [Test]
    public async Task FailedPlatformCall_IsNotRecordedAsInstalled()
    {
        var fs = new InMemoryFileSystem();
        var platform = new RefusingShortcutsPlatform();
        var root = Path.Combine(Path.GetTempPath(), "instella-uninstall-tests", Guid.NewGuid().ToString("N"));
        var config = ((InstellaInstallerImpl)InstellaInstaller.Create().WithApp("App", "com.app", new Version(1, 0)).Build()).ConfigForTests;
        var options = new InstallOptions
        {
            InstallPath = root, CreateDesktopShortcut = true, CreateStartMenuShortcut = false, AddToPath = false,
            ConfigureAutoStart = false, RegisterFileAssociations = false, Elevation = ElevationMode.PerUser,
        };
        var context = InstallContextFactory.Create(config, InstallerMode.FirstInstall, root, options, platform, fs, new NullLog(),
            payload: Zip(new() { ["App.exe"] = "x" }));

        var result = await new StepExecutor(OfflineInstallRunner.BuildDefaultSteps()).ExecuteAsync(context, null, CancellationToken.None);

        Assert.That(result.Success, Is.True, result.Error);
        var manifest = (await new InstallManifestWriter(fs).ReadAsync(root, CancellationToken.None))!;
        Assert.That(manifest.HasDesktopShortcut, Is.False, "the manifest records outcomes, not requests");
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

    private sealed class RefusingShortcutsPlatform : IPlatformServices
    {
        private readonly FakePlatformServices _inner = new();
        public TargetPlatform Platform => _inner.Platform;
        public string GetDefaultInstallPath(string appName, bool perUser) => _inner.GetDefaultInstallPath(appName, perUser);
        public Task<PlatformResult> CreateShortcutAsync(ShortcutInfo info, CancellationToken ct) => Task.FromResult(PlatformResult.Fail("refused by the test"));
        public Task<PlatformResult> RemoveShortcutAsync(ShortcutInfo info, CancellationToken ct) => _inner.RemoveShortcutAsync(info, ct);
        public Task<PlatformResult> RegisterFileAssociationAsync(FileAssociationInfo info, CancellationToken ct) => _inner.RegisterFileAssociationAsync(info, ct);
        public Task<PlatformResult> UnregisterFileAssociationAsync(string extension, string appId, CancellationToken ct) => _inner.UnregisterFileAssociationAsync(extension, appId, ct);
        public Task<PlatformResult> AddToPathAsync(string directory, bool perUser, CancellationToken ct) => _inner.AddToPathAsync(directory, perUser, ct);
        public Task<PlatformResult> RemoveFromPathAsync(string directory, bool perUser, CancellationToken ct) => _inner.RemoveFromPathAsync(directory, perUser, ct);
        public Task<PlatformResult> ConfigureAutoStartAsync(AutoStartInfo info, CancellationToken ct) => _inner.ConfigureAutoStartAsync(info, ct);
        public Task<PlatformResult> RemoveAutoStartAsync(string appId, CancellationToken ct) => _inner.RemoveAutoStartAsync(appId, ct);
        public IReadOnlyList<System.Diagnostics.Process> GetRunningProcesses(string processName, string? installPath) => [];
        public Task<PlatformResult> TerminateProcessAsync(System.Diagnostics.Process process, TimeSpan timeout, CancellationToken ct) => Task.FromResult(PlatformResult.Ok);
        public Task<PlatformResult> UpdateShortcutAsync(string oldTarget, string newTarget, string shortcutPath, CancellationToken ct) => Task.FromResult(PlatformResult.Ok);
        public Task<PlatformResult> RegisterUninstallEntryAsync(UninstallEntryInfo info, CancellationToken ct) => _inner.RegisterUninstallEntryAsync(info, ct);
        public Task<PlatformResult> UnregisterUninstallEntryAsync(string appId, bool perUser, CancellationToken ct) => _inner.UnregisterUninstallEntryAsync(appId, perUser, ct);
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
