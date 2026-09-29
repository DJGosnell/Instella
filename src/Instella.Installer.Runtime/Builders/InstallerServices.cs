using System.Net.Http;
using Instella.Core.FileSystem;
using Instella.Core.Platform;

namespace Instella.Installer.Runtime.Builders;

/// <summary>
/// Everything the installer touches outside its own process: platform integration, the
/// file system, and HTTP. <c>RunAsync</c> builds the real set; the test harness passes fakes
/// so a full run never reaches the host.
/// </summary>
/// <param name="Platform">Shortcuts, registry, PATH, processes, …</param>
/// <param name="FileSystem">Every file read and write.</param>
/// <param name="HttpHandler">Handler for server downloads; null uses a default client.</param>
/// <param name="Elevation">UAC detection and relaunch; null uses the host's.</param>
/// <param name="Payload">Opens the payload archive instead of the one appended to this exe (tests).</param>
/// <param name="Messages">Errors and questions shown outside a wizard window. Null: message boxes for the
/// real services, stderr only for injected ones (a test run must never open a window).</param>
/// <param name="HostFactory">Creates wizard and update windows; null uses Win32 windows.</param>
/// <param name="ProcessFinder">Finds programs using the app's files; null uses Restart Manager.</param>
/// <param name="AppLauncher">Starts the app after an install or update; null starts a real process.</param>
/// <param name="ScopePrompt">Asks "for me or for everyone"; null shows the scope window.</param>
/// <param name="StubDirectory">The folder the running stub is in; null uses the process's own folder.</param>
/// <param name="ManagerUi">The Modify / Repair / Uninstall window; null shows the Win32 one.</param>
/// <param name="KnownFolders">Where install migrations find the known folders; null uses the host's.</param>
/// <param name="ProcessCloser">Closes programs for install migrations; null asks them through the platform.</param>
/// <param name="Programs">Starts the app's upgrade program and the programs install migrations run; null starts real processes.</param>
/// <param name="Log">Where the run logs; null uses the configured log files (the harness records into its sink).</param>
internal sealed record InstallerServices(
    IPlatformServices Platform,
    IFileSystem FileSystem,
    HttpMessageHandler? HttpHandler = null,
    Core.Elevation.IElevationService? Elevation = null,
    System.Func<System.IO.Stream?>? Payload = null,
    Runners.IUserMessages? Messages = null,
    Runners.InteractiveHostFactory? HostFactory = null,
    Core.Processes.ILockingProcessFinder? ProcessFinder = null,
    Core.Processes.IAppLauncher? AppLauncher = null,
    Runners.ScopePrompt? ScopePrompt = null,
    System.Func<string?>? StubDirectory = null,
    Runners.ManagerUiLauncher? ManagerUi = null,
    Migrations.KnownFolderResolver? KnownFolders = null,
    Core.Processes.IProcessCloser? ProcessCloser = null,
    Migrations.IProgramRunner? Programs = null,
    Instella.Core.Logging.IInstellaLogger? Log = null)
{
    /// <summary><see cref="StubDirectory"/>, or the folder of the running process.</summary>
    public string? StubDirectoryOrDefault() => (StubDirectory ?? Runners.InstallPaths.StubDirectory)();

    /// <summary><see cref="Elevation"/>, or the host's real service.</summary>
    public Core.Elevation.IElevationService ElevationOrDefault =>
        Elevation ?? (System.OperatingSystem.IsWindows()
            ? new Core.Elevation.WindowsElevationService()
            : new Core.Elevation.NoElevationService());

    /// <summary>
    /// A client for the update server (<see cref="Instella.Core.Wire.ServerHttp.Create"/>): over
    /// <see cref="HttpHandler"/> when set, sending <paramref name="downloadToken"/> to that server only.
    /// </summary>
    public HttpClient CreateServerClient(string serverUrl, string? downloadToken, string userAgent) =>
        Instella.Core.Wire.ServerHttp.Create(new System.Uri(serverUrl, System.UriKind.Absolute), downloadToken, userAgent,
            System.TimeSpan.FromMinutes(30), HttpHandler);
}
