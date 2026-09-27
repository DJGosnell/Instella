using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.FileSystem;
using Instella.Core.Installation;
using Instella.Core.Logging;
using Instella.Core.Platform;
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Runtime.Core;
using Instella.Installer.Runtime.Runners;
using Instella.Installer.Runtime.UI.Windows;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Installation;

/// <summary>
/// Action-dispatch behaviour of <see cref="ManageModeRunner"/>
/// (what runs when the staged <c>instella.exe</c> is double-clicked from an
/// install directory). The Win32 manager window and the uninstall hand-off are
/// stubbed via the runner's internal <see cref="ManagerUiLauncher"/> /
/// <see cref="UninstallHandoff"/> seams so these tests exercise the routing on
/// any host without popping a native window, a confirmation MessageBox, or the
/// detached cmd.exe self-delete trampoline the real uninstaller schedules.
/// </summary>
[TestFixture]
public sealed class ManageModeRunnerDispatchTests
{
    private readonly List<string> _tempDirs = new();

    [TearDown]
    public void Cleanup()
    {
        foreach (var dir in _tempDirs)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch { /* best-effort */ }
        }
        _tempDirs.Clear();
    }

    private static FrozenConfig ConfigWithServer(string? serverUrl = null)
    {
        var b = new InstallerBuilder();
        b.WithApp("Manage Test", "com.example.manage", new Version(1, 0, 0));
        if (!string.IsNullOrEmpty(serverUrl)) b.WithServer(serverUrl).AllowUnsignedUpdates();
        var installer = (InstellaInstallerImpl)b.Build();
        return installer.ConfigForTests;
    }

    private string NewInstallDirWithManifest(
        string appName = "Manage Test",
        string version = "1.2.3",
        string? serverUrl = "https://updates.example.com")
    {
        var dir = Path.Combine(Path.GetTempPath(), $"manage-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);

        var manifest = new InstalledManifest
        {
            AppName = appName,
            AppId = "com.example.manage",
            Version = Version.Parse(version),
            InstallDirectory = dir,
            ExecutableName = "ManageTest.exe",
            InstalledAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            InstalledPerUser = true,
            Platform = PlatformDetector.Current,
            ServerUrl = serverUrl,
            Files = Array.Empty<InstalledFile>(),
        };

        // Write through the same reader the runner uses so the round-trip is real.
        new InstallManifestWriter(RealFileSystem.Instance)
            .WriteAsync(dir, manifest, CancellationToken.None)
            .GetAwaiter().GetResult();

        return dir;
    }

    private ManageModeRunner NewRunner(
        FrozenConfig config,
        ManagerUiLauncher launcher,
        UninstallHandoff? uninstallHandoff = null,
        UpdateHandoff? updateHandoff = null)
        => new(config, new NullLogger(), new NoOpPlatformServices(PlatformDetector.Current),
            RealFileSystem.Instance, launcher, uninstallHandoff,
            updateHandoff ?? ((m, p, repair, ct) => Task.FromResult(InstellaExitCode.Success)));

    // ---- the update check never crashes ------------------------------------------

    [Test]
    public async Task UpdateCheck_Timeout_Exits21_WithAMessage()
    {
        var (exit, messages) = await CheckForUpdatesAsync(new ThrowingHandler(new TaskCanceledException("timed out")));

        Assert.That(exit, Is.EqualTo(InstellaExitCode.UpdateServerUnreachable));
        Assert.That(messages.Errors.Single().Text, Does.StartWith("Could not check for updates: the server did not answer in time."));
    }

    [Test]
    public async Task UpdateCheck_ReplyThatIsNotJson_Exits21_WithAMessage()
    {
        // A captive portal answers 200 with an HTML page.
        var (exit, messages) = await CheckForUpdatesAsync(new HtmlHandler());

        Assert.That(exit, Is.EqualTo(InstellaExitCode.UpdateServerUnreachable));
        Assert.That(messages.Errors.Single().Text, Does.StartWith("Could not check for updates: the server's reply was not an update check."));
    }

    private async Task<(InstellaExitCode Exit, Tests.Runners.RecordingMessages Messages)> CheckForUpdatesAsync(System.Net.Http.HttpMessageHandler handler)
    {
        var dir = NewInstallDirWithManifest();
        var manifest = await new InstallManifestWriter(RealFileSystem.Instance).ReadAsync(dir, CancellationToken.None);
        var messages = new Tests.Runners.RecordingMessages();
        var runner = new ManageModeRunner(ConfigWithServer("https://updates.example.com"), new NullLogger(),
            new NoOpPlatformServices(PlatformDetector.Current), RealFileSystem.Instance)
        {
            Messages = messages, HttpHandler = handler,
        };
        var exit = await runner.DefaultUpdateHandoffAsync(manifest!, dir, repair: false, CancellationToken.None);
        return (exit, messages);
    }

    private sealed class ThrowingHandler(Exception ex) : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken ct) =>
            Task.FromException<System.Net.Http.HttpResponseMessage>(ex);
    }

    private sealed class HtmlHandler : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new System.Net.Http.StringContent("<html><body>Sign in to the Wi-Fi</body></html>", System.Text.Encoding.UTF8, "text/html"),
            });
    }

    [Test]
    public async Task Silent_returnsSuccess_withoutLaunchingUi()
    {
        var dir = NewInstallDirWithManifest();
        var calls = 0;
        ManagerUiLauncher launcher = (a, v, p, s, u, r) => { calls++; return ManagerAction.None; };
        var runner = NewRunner(ConfigWithServer(), launcher);

        var dispatch = new DispatchResult(DispatchKind.Mode, InstallerMode.Manage, dir, IsSilent: true);
        var exit = await runner.RunAsync(dispatch, CancellationToken.None);

        Assert.That(exit, Is.EqualTo(InstellaExitCode.Success));
        Assert.That(calls, Is.EqualTo(0), "silent manage must stay headless and never launch the UI");
    }

    [Test]
    public async Task MissingManifest_returnsSuccess_withoutLaunchingUi()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"manage-empty-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);

        var calls = 0;
        ManagerUiLauncher launcher = (a, v, p, s, u, r) => { calls++; return ManagerAction.None; };
        var runner = NewRunner(ConfigWithServer(), launcher);

        var dispatch = new DispatchResult(DispatchKind.Mode, InstallerMode.Manage, dir, IsSilent: false);
        var exit = await runner.RunAsync(dispatch, CancellationToken.None);

        Assert.That(exit, Is.EqualTo(InstellaExitCode.Success));
        Assert.That(calls, Is.EqualTo(0), "no manifest means no manager UI");
    }

    [Test]
    public async Task ManagerClosed_None_returnsSuccess()
    {
        var dir = NewInstallDirWithManifest();
        var runner = NewRunner(ConfigWithServer(), (a, v, p, s, u, r) => ManagerAction.None);

        var dispatch = new DispatchResult(DispatchKind.Mode, InstallerMode.Manage, dir, IsSilent: false);
        var exit = await runner.RunAsync(dispatch, CancellationToken.None);

        Assert.That(exit, Is.EqualTo(InstellaExitCode.Success));
    }

    [TestCase(2, false)] // ManagerAction.Update (internal, so passed as its value)
    [TestCase(3, true)]  // ManagerAction.Repair
    public async Task ManagerUpdateAndRepair_delegateToUpdateHandoff(int actionValue, bool expectRepair)
    {
        var action = (ManagerAction)actionValue;
        var dir = NewInstallDirWithManifest(version: "2.0.0");
        (string Path, bool Repair, Version Version)? seen = null;
        UpdateHandoff handoff = (m, p, repair, ct) =>
        {
            seen = (p, repair, m.Version);
            return Task.FromResult(InstellaExitCode.UpdateRolledBack);
        };
        var runner = NewRunner(ConfigWithServer(), (a, v, p, s, u, r) => action, updateHandoff: handoff);

        var dispatch = new DispatchResult(DispatchKind.Mode, InstallerMode.Manage, dir, IsSilent: false);
        var exit = await runner.RunAsync(dispatch, CancellationToken.None);

        Assert.That(exit, Is.EqualTo(InstellaExitCode.UpdateRolledBack), "the engine's exit code is propagated");
        Assert.That(seen, Is.EqualTo((dir, expectRepair, new Version(2, 0, 0))));
    }

    [Test]
    public async Task NoServerUrl_hidesUpdateAndRepair()
    {
        var dir = NewInstallDirWithManifest(serverUrl: null);
        bool? seenUpdate = null, seenRepair = null;
        var runner = NewRunner(ConfigWithServer("https://ignored.example.com"),
            (a, v, p, s, u, r) => { seenUpdate = u; seenRepair = r; return ManagerAction.None; });

        await runner.RunAsync(new DispatchResult(DispatchKind.Mode, InstallerMode.Manage, dir), CancellationToken.None);

        Assert.That(seenUpdate, Is.False);
        Assert.That(seenRepair, Is.False);
    }

    [Test]
    public async Task ManagerUninstall_delegatesToUninstallHandoff()
    {
        var dir = NewInstallDirWithManifest();
        DispatchResult? seen = null;
        var handoffCalls = 0;
        // Sentinel return value distinct from Success proves the hand-off's
        // result is propagated verbatim.
        UninstallHandoff handoff = (d, ct) =>
        {
            handoffCalls++;
            seen = d;
            return Task.FromResult(InstellaExitCode.UninstallFilesLocked);
        };
        var runner = NewRunner(ConfigWithServer(), (a, v, p, s, u, r) => ManagerAction.Uninstall, handoff);

        var dispatch = new DispatchResult(DispatchKind.Mode, InstallerMode.Manage, dir, IsSilent: false);
        var exit = await runner.RunAsync(dispatch, CancellationToken.None);

        Assert.That(handoffCalls, Is.EqualTo(1));
        Assert.That(exit, Is.EqualTo(InstellaExitCode.UninstallFilesLocked), "manager returns the uninstall pipeline's exit code");
        Assert.That(seen, Is.Not.Null);
        Assert.That(seen!.Value.Mode, Is.EqualTo(InstallerMode.Uninstall));
        Assert.That(seen.Value.InstallPath, Is.EqualTo(dir));
        Assert.That(seen.Value.IsSilent, Is.False, "interactive uninstall keeps the confirmation dialog");
    }

    [Test]
    public async Task Launcher_receivesManifestMetadataAndServerUrl()
    {
        var dir = NewInstallDirWithManifest(appName: "Acme Suite", version: "4.5.6");
        string? seenApp = null, seenVer = null, seenPath = null, seenServer = null;
        bool seenUpdate = true, seenRepair = false;
        ManagerUiLauncher launcher = (app, ver, path, server, update, repair) =>
        {
            seenApp = app; seenVer = ver; seenPath = path; seenServer = server;
            seenUpdate = update; seenRepair = repair;
            return ManagerAction.None;
        };
        var runner = NewRunner(ConfigWithServer(), launcher);

        var dispatch = new DispatchResult(DispatchKind.Mode, InstallerMode.Manage, dir, IsSilent: false);
        var exit = await runner.RunAsync(dispatch, CancellationToken.None);

        Assert.That(exit, Is.EqualTo(InstellaExitCode.Success));
        Assert.That(seenApp, Is.EqualTo("Acme Suite"), "app name comes from the on-disk manifest");
        Assert.That(seenVer, Is.EqualTo("4.5.6"));
        Assert.That(seenPath, Is.EqualTo(dir));
        Assert.That(seenServer, Is.EqualTo("https://updates.example.com"), "server url comes from the installed manifest");
        Assert.That(seenUpdate, Is.True, "Check for Updates is offered when the installation has a server");
        Assert.That(seenRepair, Is.True, "repair is offered");
    }

    [Test]
    public void DefaultManagerUiLauncher_onNonWindows_returnsNone()
    {
        if (OperatingSystem.IsWindows())
            Assert.Ignore("On Windows the default launcher shows a real window; the non-Windows headless contract is asserted on other hosts.");

        var action = ManageModeRunner.DefaultManagerUiLauncher(
            "App", "1.0.0", "/opt/app", serverUrl: null, updateAvailable: false, repairAvailable: true);

        Assert.That(action, Is.EqualTo(ManagerAction.None));
    }

    private sealed class NullLogger : IInstellaLogger
    {
        public bool IsEnabled(InstellaLogLevel level) => true;
        public void Trace(string m) { }
        public void Debug(string m) { }
        public void Info(string m) { }
        public void Warn(string m) { }
        public void Error(string m, Exception? e = null) { }
        public IDisposable Scope(string s) => NullScope.Instance;
        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
