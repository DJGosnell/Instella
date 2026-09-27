using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Installation;
using Instella.Core.Logging;
using Instella.Core.Manifest;
using Instella.Core.Platform;
using Instella.Core.Platform.Windows;
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Runtime.Core;
using Instella.Installer.Runtime.Core.Elevation;
using Instella.Installer.Runtime.Core.Update;
using Instella.Installer.Runtime.Runners;
using Instella.Installer.Testing;
using NUnit.Framework;
using Instella.Core.Update;

namespace Instella.Installer.Runtime.Tests.Installation;

/// <summary>Scope resolution, the UAC relaunch, and machine-scope targets.</summary>
[TestFixture]
public partial class ElevationTests
{
    // ---- 7.1 scope-resolution table ----

    [TestCase(ElevationMode.PerUser, false, null, false, "Proceed", InstallationScope.PerUser)]
    [TestCase(ElevationMode.PerUser, true, null, false, "Proceed", InstallationScope.PerUser)]
    [TestCase(ElevationMode.PerUser, false, "machine", false, "Proceed", InstallationScope.PerUser)]
    [TestCase(ElevationMode.SystemWide, false, null, true, "Proceed", InstallationScope.SystemWide)]
    [TestCase(ElevationMode.SystemWide, false, null, false, "RelaunchElevated", null)]
    [TestCase(ElevationMode.SystemWide, true, null, false, "RefuseNotElevated", null)]
    [TestCase(ElevationMode.SystemWide, true, null, true, "Proceed", InstallationScope.SystemWide)]
    [TestCase(ElevationMode.UserChoice, false, null, false, "AskUser", null)]
    [TestCase(ElevationMode.UserChoice, false, null, true, "AskUser", null)]
    [TestCase(ElevationMode.UserChoice, true, null, false, "Proceed", InstallationScope.PerUser)]
    [TestCase(ElevationMode.UserChoice, true, "user", false, "Proceed", InstallationScope.PerUser)]
    [TestCase(ElevationMode.UserChoice, true, "machine", false, "RefuseNotElevated", null)]
    [TestCase(ElevationMode.UserChoice, true, "machine", true, "Proceed", InstallationScope.SystemWide)]
    [TestCase(ElevationMode.UserChoice, false, "machine", false, "RelaunchElevated", null)]
    [TestCase(ElevationMode.UserChoice, false, "Machine", true, "Proceed", InstallationScope.SystemWide)]
    [TestCase(ElevationMode.UserChoice, false, "everyone", false, "InvalidScopeArgument", null)]
    public void ScopeResolution_Table(
        ElevationMode mode, bool silent, string? scopeArg, bool elevated, string action, InstallationScope? scope)
    {
        var decision = ScopeResolver.Resolve(mode, silent, scopeArg, elevated);
        Assert.That(decision.Action.ToString(), Is.EqualTo(action));
        Assert.That(decision.Scope, Is.EqualTo(scope));
    }

    // ---- 7.2 WindowsCommandLine round trip ----

    private static readonly string[][] QuotingTable =
    [
        ["plain"],
        [""],
        ["with space"],
        [@"C:\Program Files\App\"],
        [@"C:\path\"],
        ["quote\"inside"],
        [@"back\slash"],
        [@"trailing\\"],
        [@"a\\""b"],
        ["tab\there"],
        [@"\\server\share\dir with space\"],
        ["--path", @"C:\Users\x y\AppData\Local\Programs\Q N\", "--scope", "machine"],
        ["\"", @"\", @"\\", "\\\""],
    ];

    [TestCaseSource(nameof(QuotingTable))]
    public void WindowsCommandLine_RoundTripsThroughCommandLineToArgvW(string[] args)
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("CommandLineToArgvW is Windows-only");

        // CommandLineToArgvW parses argv[0] by different rules; put a plain program name first.
        var parsed = SplitWithShell32("prog.exe " + WindowsCommandLine.Join(args));
        Assert.That(parsed.Skip(1), Is.EqualTo(args));
    }

    [Test]
    public void ForwardArgs_ReplacesScopeAndMarksTheChild()
    {
        var forwarded = ElevationGate.ForwardArgs(["--install", "--scope", "user", "--path", @"C:\x", "--elevated-child", "--scope=user"], "machine");
        Assert.That(forwarded, Is.EqualTo(new[] { "--install", "--path", @"C:\x", "--scope", "machine", "--elevated-child" }));
    }

    [TestCase(@"C:\")]
    [TestCase(@"D:\Apps\QN")]
    [TestCase(@"C:\Program Files\Acme Inc\Quick Notes")]
    [Platform("Win")]
    public void UninstallCommand_ParsesBackToExactlyThePath(string installPath)
    {
        var stub = System.IO.Path.Combine(installPath, "instella.exe");
        var command = Instella.Installer.Runtime.Installation.BuiltIn.RegisterUninstallEntryStep.UninstallCommandFor(stub, installPath);

        Assert.That(SplitWithShell32(command), Is.EqualTo(new[] { stub, "--uninstall", "--path", installPath }), command);
    }

    [Test]
    public void ForwardArgs_ReplacesThePathWithTheNormalisedOne()
    {
        var forwarded = ElevationGate.ForwardArgs(["--uninstall", "--path", "rel", "--silent"], scope: null, installPath: @"C:\work\rel");
        Assert.That(forwarded, Is.EqualTo(new[] { "--uninstall", "--path", @"C:\work\rel", "--silent", "--elevated-child" }));
        var equalsForm = ElevationGate.ForwardArgs(["--install", "--path=rel"], "machine", installPath: @"C:\work\rel");
        Assert.That(equalsForm, Is.EqualTo(new[] { "--install", "--path", @"C:\work\rel", "--scope", "machine", "--elevated-child" }));
    }

    [Test]
    public void ForwardArgs_CopiesTheAppsArgumentsVerbatim()
    {
        var forwarded = ElevationGate.ForwardArgs(["--update", "--app-path", @"C:\x", "--extra-args", "--scope", "user", "--elevated-child"], scope: null);
        Assert.That(forwarded, Is.EqualTo(new[] { "--update", "--app-path", @"C:\x", "--elevated-child", "--extra-args", "--scope", "user", "--elevated-child" }));
    }

    // ---- relaunch behaviour ----

    [Test]
    public async Task SilentSystemWide_NotElevated_Exits51_WithoutPrompting()
    {
        var elevation = new FakeElevation(elevated: false);
        var installer = (InstellaInstallerImpl)InstellaInstaller.Create().WithApp("App", "com.app", new Version(1, 0))
            .WithElevation(ElevationMode.SystemWide).Build();

        var exit = await installer.RunAsync(["--install", "--silent", "--path", TempPath()],
            new InstallerServices(new FakePlatformServices(), new InMemoryFileSystem(), Elevation: elevation), CancellationToken.None);

        Assert.That(exit, Is.EqualTo((int)InstellaExitCode.InsufficientPrivileges));
        Assert.That(elevation.Relaunches, Is.Empty, "silent installs never show a UAC prompt");
    }

    [Test]
    public async Task InteractiveSystemWide_NotElevated_RelaunchesWithScopeMachine_AndReturnsChildExit()
    {
        var elevation = new FakeElevation(elevated: false) { ChildExit = 0 };
        var (dispatch, exit) = await ElevationGate.ResolveInstallScopeAsync(
            Config(ElevationMode.SystemWide), ["--install"], Dispatch(silent: false), elevation, (_, _) => "user", new NullLog(), CancellationToken.None);

        Assert.That(dispatch, Is.Null);
        Assert.That(exit, Is.EqualTo(InstellaExitCode.Success));
        Assert.That(elevation.Relaunches.Single(), Is.EqualTo(new[] { "--install", "--scope", "machine", "--elevated-child" }));
    }

    [Test]
    public void ScopePage_ShowsTheVersionUnderTheHeading()
    {
        // Hand test: the first window said "Install QuickNotes" with no version anywhere.
        IReadOnlyList<PageSpec>? shown = null;
        var prompt = ElevationGate.DefaultScopePrompt((title, pages, states) =>
        {
            shown = pages;
            return new ClosedHost();
        }, new Version(1, 2, 0));

        Assert.That(prompt("QuickNotes", null), Is.Null, "closed without choosing");

        var widgets = shown!.Single().Widgets;
        Assert.That(widgets[0], Is.EqualTo(new Instella.Installer.Runtime.UI.Widgets.Heading("Install QuickNotes")));
        Assert.That(widgets[1], Is.EqualTo(new Instella.Installer.Runtime.UI.Widgets.Paragraph("Version 1.2.0")));
    }

    /// <summary>A window the user closes straight away.</summary>
    private sealed class ClosedHost : IInteractiveHost
    {
        public event Action<int>? PageChanged { add { } remove { } }
        public void PostToUiThread(Action action) => action();
        public void LockNavigation() { }
        public void LockBackNavigation() { }
        public InteractiveHostOutcome Run() => InteractiveHostOutcome.Cancelled;
        public Func<bool>? CancelInterceptor { set { } }
        public Func<int, string?>? BeforeLeave { set { } }
        public void Close() { }
        public void Dispose() { }
    }

    [Test]
    public async Task UserChoice_DeclinedUac_ReturnsToTheScopePageWithANotice()
    {
        var elevation = new FakeElevation(elevated: false) { ChildExit = null };
        var answers = new Queue<string?>(["machine", "user"]);
        var notices = new List<string?>();

        var (dispatch, exit) = await ElevationGate.ResolveInstallScopeAsync(
            Config(ElevationMode.UserChoice), ["--install"], Dispatch(silent: false), elevation,
            (_, notice) => { notices.Add(notice); return answers.Dequeue(); }, new NullLog(), CancellationToken.None);

        Assert.That(exit, Is.Null);
        Assert.That(dispatch!.Value.Scope, Is.EqualTo(InstallationScope.PerUser));
        Assert.That(notices, Has.Count.EqualTo(2));
        Assert.That(notices[0], Is.Null);
        Assert.That(notices[1], Does.Contain("not granted"));
    }

    [Test]
    public async Task ElevatedChildStillNotElevated_Exits51_InsteadOfLooping()
    {
        var elevation = new FakeElevation(elevated: false);
        var (_, exit) = await ElevationGate.ResolveInstallScopeAsync(
            Config(ElevationMode.SystemWide), ["--install"], Dispatch(silent: false) with { ElevatedChild = true },
            elevation, (_, _) => "user", new NullLog(), CancellationToken.None);

        Assert.That(exit, Is.EqualTo(InstellaExitCode.InsufficientPrivileges));
        Assert.That(elevation.Relaunches, Is.Empty);
    }

    [TestCase(false, true)]
    [TestCase(true, false)]
    public async Task OperationsOnAMachineInstall_RelaunchElevated(bool perUserInstall, bool expectRelaunch)
    {
        var fs = new InMemoryFileSystem();
        var root = TempPath();
        await new InstallManifestWriter(fs).WriteAsync(root, new InstalledManifest
        {
            AppName = "App", AppId = "com.app", Version = new Version(1, 0), InstallDirectory = root,
            ExecutableName = "App.exe", InstalledAt = DateTime.UtcNow, InstalledPerUser = perUserInstall, Files = [],
        }, CancellationToken.None);
        var elevation = new FakeElevation(elevated: false) { ChildExit = 0 };

        var result = await ElevationGate.EnsureElevatedForInstallationAsync(
            root, ["--uninstall", "--path", root], Dispatch(silent: false), fs, elevation, false, new NullLog(), CancellationToken.None);

        Assert.That(result, expectRelaunch ? Is.EqualTo(InstellaExitCode.Success) : Is.Null);
        Assert.That(elevation.Relaunches.Count, Is.EqualTo(expectRelaunch ? 1 : 0));
        if (expectRelaunch)
            Assert.That(elevation.Relaunches[0][^1], Is.EqualTo("--elevated-child"));
    }

    [Test]
    public async Task SilentRecoverOfAMachineInstall_PromptsForElevation()
    {
        // The SDK starts recovery with --silent, but a user is there, so UAC may be shown:
        // refusing to elevate would leave a machine-wide install unrecoverable from the app.
        var fs = new InMemoryFileSystem();
        var root = await WriteManifest(fs, perUser: false);
        var elevation = new FakeElevation(elevated: false) { ChildExit = 0 };

        var exit = await WithStubIn(root, () => Installer().RunAsync(["--recover", "--path", root, "--silent"],
            new InstallerServices(new FakePlatformServices(), fs, Elevation: elevation), CancellationToken.None));

        Assert.That(exit, Is.EqualTo((int)InstellaExitCode.Success));
        Assert.That(elevation.Relaunches, Has.Count.EqualTo(1));
    }

    // ---- target binding: an elevated maintenance mode acts only on the stub's own folder ----

    [Test]
    public async Task ForeignMachineInstall_NotElevated_Exits51_WithoutAPrompt()
    {
        var fs = new InMemoryFileSystem();
        var root = await WriteManifest(fs, perUser: false);
        var elevation = new FakeElevation(elevated: false) { ChildExit = 0 };

        var result = await ElevationGate.EnsureElevatedForInstallationAsync(
            root, ["--uninstall", "--path", root], Dispatch(silent: false), fs, elevation, false, new NullLog(), CancellationToken.None,
            foreign: true);

        Assert.That(result, Is.EqualTo(InstellaExitCode.InsufficientPrivileges));
        Assert.That(elevation.Relaunches, Is.Empty, "the stub never elevates for a folder that is not its own");
    }

    [Test]
    public async Task ForeignMachineUninstall_ThroughRunAsync_Exits51_AndNeverRelaunches()
    {
        var fs = new InMemoryFileSystem();
        var root = await WriteManifest(fs, perUser: false);
        var elevation = new FakeElevation(elevated: false) { ChildExit = 0 };

        var exit = await WithStubIn(TempPath(), () => Installer().RunAsync(["--uninstall", "--silent", "--path", root],
            new InstallerServices(new FakePlatformServices(), fs, Elevation: elevation), CancellationToken.None));

        Assert.That(exit, Is.EqualTo((int)InstellaExitCode.InsufficientPrivileges));
        Assert.That(elevation.Relaunches, Is.Empty);
    }

    [Test]
    public async Task OwnMachineUninstall_ThroughRunAsync_StillRelaunchesElevated()
    {
        var fs = new InMemoryFileSystem();
        var root = await WriteManifest(fs, perUser: false);
        var elevation = new FakeElevation(elevated: false) { ChildExit = 0 };

        var exit = await WithStubIn(root + Path.DirectorySeparatorChar, () => Installer().RunAsync(["--uninstall", "--path", root],
            new InstallerServices(new FakePlatformServices(), fs, Elevation: elevation), CancellationToken.None));

        Assert.That(exit, Is.EqualTo((int)InstellaExitCode.Success));
        Assert.That(elevation.Relaunches, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task ForeignPerUserUninstall_Works()
    {
        var fs = new InMemoryFileSystem();
        var root = await WriteManifest(fs, perUser: true);
        var elevation = new FakeElevation(elevated: false);

        var exit = await WithStubIn(TempPath(), () => Installer().RunAsync(["--uninstall", "--silent", "--path", root],
            new InstallerServices(new FakePlatformServices(), fs, Elevation: elevation), CancellationToken.None));

        Assert.That(exit, Is.EqualTo((int)InstellaExitCode.Success));
        Assert.That(elevation.Relaunches, Is.Empty);
        Assert.That(fs.Exists(Path.Combine(root, InstellaOwnedPaths.InstalledManifest)), Is.False);
    }

    [TestCase("--uninstall")]
    [TestCase("--recover")]
    [TestCase("--manage")]
    public async Task ForeignTarget_InAnElevatedChild_Exits51(string mode)
    {
        var fs = new InMemoryFileSystem();
        var root = await WriteManifest(fs, perUser: true);
        var elevation = new FakeElevation(elevated: true);

        var exit = await WithStubIn(TempPath(), () => Installer().RunAsync([mode, "--silent", "--path", root, "--elevated-child"],
            new InstallerServices(new FakePlatformServices(), fs, Elevation: elevation), CancellationToken.None));

        Assert.That(exit, Is.EqualTo((int)InstellaExitCode.InsufficientPrivileges));
        Assert.That(fs.Exists(Path.Combine(root, InstellaOwnedPaths.InstalledManifest)), Is.True, "nothing was touched");
    }

    [Test]
    public async Task ForeignUpdateAppPath_Exits40_BeforeReadingTheManifestOrElevating()
    {
        var fs = new InMemoryFileSystem();
        var root = await WriteManifest(fs, perUser: false);
        var elevation = new FakeElevation(elevated: false) { ChildExit = 0 };
        var args = new UpdaterArgs
        {
            AppPath = root, AppExecutable = "App.exe", FromVersion = new Version(1, 0), ToVersion = new Version(1, 1),
        }.ToArgumentList().ToArray();

        var exit = await WithStubIn(TempPath(), () => Installer().RunAsync(args,
            new InstallerServices(new FakePlatformServices(), fs, Elevation: elevation), CancellationToken.None));

        Assert.That(exit, Is.EqualTo((int)InstellaExitCode.UsageInvalidArgs));
        Assert.That(elevation.Relaunches, Is.Empty);
    }

    private static async Task<int> WithStubIn(string folder, Func<Task<int>> run)
    {
        var saved = InstallPaths.StubDirectory;
        InstallPaths.StubDirectory = () => folder;
        try
        {
            return await run();
        }
        finally
        {
            InstallPaths.StubDirectory = saved;
        }
    }

    private static InstellaInstallerImpl Installer() =>
        (InstellaInstallerImpl)InstellaInstaller.Create().WithApp("App", "com.app", new Version(1, 0)).Build();

    private static async Task<string> WriteManifest(InMemoryFileSystem fs, bool perUser)
    {
        var root = TempPath();
        await new InstallManifestWriter(fs).WriteAsync(root, new InstalledManifest
        {
            AppName = "App", AppId = "com.app", Version = new Version(1, 0), InstallDirectory = root,
            ExecutableName = "App.exe", InstalledAt = DateTime.UtcNow, InstalledPerUser = perUser, Files = [],
        }, CancellationToken.None);
        return root;
    }

    // ---- 7.3 machine-scope targets ----

    [Test]
    public void MachineDefaultPath_IncludesPublisher()
    {
        var config = ((InstellaInstallerImpl)InstellaInstaller.Create().WithApp("QuickNotes", "com.q", new Version(1, 0))
            .WithPublisher("Acme: Inc.").Build()).ConfigForTests;
        var platform = new FakePlatformServices();

        Assert.That(InstallPaths.Default(config, platform, InstallationScope.SystemWide),
            Is.EqualTo(platform.GetDefaultInstallPath(Path.Combine("Acme Inc", "QuickNotes"), perUser: false)));
        Assert.That(InstallPaths.Default(config, platform, InstallationScope.PerUser),
            Is.EqualTo(platform.GetDefaultInstallPath("QuickNotes", perUser: true)));
    }

    // ---- helpers ----

    private static FrozenConfig Config(ElevationMode mode) =>
        ((InstellaInstallerImpl)InstellaInstaller.Create().WithApp("App", "com.app", new Version(1, 0)).WithElevation(mode).Build()).ConfigForTests;

    private static DispatchResult Dispatch(bool silent) =>
        new(DispatchKind.Mode, InstallerMode.FirstInstall, null, IsSilent: silent);

    private static string TempPath() => Path.Combine(Path.GetTempPath(), "instella-elev-tests", Guid.NewGuid().ToString("N"));

    private static string[] SplitWithShell32(string commandLine)
    {
        var argv = CommandLineToArgvW(commandLine, out var argc);
        try
        {
            var result = new string[argc];
            for (var i = 0; i < argc; i++)
                result[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!;
            return result;
        }
        finally
        {
            LocalFree(argv);
        }
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr CommandLineToArgvW(string lpCmdLine, out int pNumArgs);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr hMem);

    private sealed class FakeElevation(bool elevated) : IElevationService
    {
        public bool IsElevated => elevated;
        public int? ChildExit { get; init; }
        public List<IReadOnlyList<string>> Relaunches { get; } = [];

        public Task<int?> RelaunchElevatedAsync(IReadOnlyList<string> args, CancellationToken ct)
        {
            Relaunches.Add(args);
            return Task.FromResult(ChildExit);
        }
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
