using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.FileSystem;
using Instella.Core.Installation;
using Instella.Core.Manifest;
using Instella.Core.Platform;
using Instella.Installer.Runtime.Logging;
using Instella.Installer.Runtime.Runners;
using Instella.Core.Update;

namespace Instella.Installer.Runtime.Builders;

/// <summary>
/// Frozen <see cref="IInstellaInstaller"/> implementation produced by
/// <see cref="InstallerBuilder.Build"/>. Entry point for the installer
/// binary: bootstraps logging, resolves the dispatch decision, and hands off
/// to the appropriate mode runner.
/// </summary>
internal sealed class InstellaInstallerImpl : IInstellaInstaller
{
    /// <summary>Logged on every run outside Windows.</summary>
    internal const string ExperimentalPlatformBanner =
        "Instella: Linux/macOS support is experimental and not covered by compatibility guarantees.";

    private readonly FrozenConfig _config;

    public InstellaInstallerImpl(FrozenConfig config) => _config = config;

    /// <summary>Test-only handle on the frozen config so tests can assert opt-in state.</summary>
    internal FrozenConfig ConfigForTests => _config;

    /// <summary>
    /// Builds the real <see cref="InstallerServices"/>. Tests replace it with a throwing set to
    /// prove a harness run never reaches the host.
    /// </summary>
    /// <summary>Test hook: replaces the Win32 scope page for <c>UserChoice</c> installs.</summary>
    internal static ScopePrompt? ScopePromptOverride { get; set; }

    internal static Func<InstallerServices> RealServicesFactory { get; set; } =
        () => new InstallerServices(PlatformServicesFactory.Create(), RealFileSystem.Instance);

    public Task<int> RunAsync(string[] args, CancellationToken ct = default) => RunAsync(args, services: null, ct);

    /// <summary>
    /// Runs with the given services instead of the real ones (the test harness passes its
    /// fakes). Null means <see cref="RealServicesFactory"/>.
    /// </summary>
    internal async Task<int> RunAsync(string[] args, InstallerServices? services, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);

        // First, before any UI or COM work: DLLs load from System32 only.
        Core.DllSearchHardening.Apply();

        // Build-time manifest emission: the MSBuild two-stage publish invokes
        // the freshly-built installer with `--emit-manifest <path>` to freeze
        // the builder config into a JSON manifest. Handled before any other
        // dispatch so a malformed mode flag elsewhere in args doesn't mask
        // the build-time flow.
        if (TryExtractEmitManifestPath(args, out var manifestPath))
        {
            var manifest = _config.ToManifest() with { IconPath = await MaterializeIconAsync(_config.Icon, manifestPath!, ct) };
            await WriteManifestJsonAsync(manifestPath!, manifest, ct);
            return (int)InstellaExitCode.Success;
        }

        // Preview mode: --preview short-circuits the normal dispatch
        // so the simulated UI + step pipeline takes over before the real
        // runners touch the host. Opt-in is required (`.EnablePreview()` on
        // the fluent builder); a bare --preview without opt-in exits 40 with a
        // clear usage message so production installers don't ship a hidden
        // preview path by accident.
        if (PreviewCliArgsParser.ContainsPreviewFlag(args))
        {
            if (!_config.PreviewEnabled)
            {
                Console.Error.WriteLine(
                    "--preview received, but .EnablePreview() was not called on the installer builder. "
                    + "Add .EnablePreview() before .Build() to opt in.");
                return (int)InstellaExitCode.UsageInvalidArgs;
            }

            var parseResult = PreviewCliArgsParser.Parse(args);
            if (!parseResult.IsSuccess)
            {
                Console.Error.WriteLine(parseResult.Error);
                return (int)InstellaExitCode.UsageInvalidArgs;
            }

            InstellaLogInitializer.Initialize(
                _config.Logging, cliArgs: args, appId: _config.AppId, modeName: "preview");
            var previewLog = new LogsmithLoggerAdapter();
            try
            {
                var runner = new Runners.PreviewModeRunner(_config, previewLog);
                var exit = await runner.RunAsync(parseResult.Args!.Value, ct);
                return (int)exit;
            }
            finally
            {
                await InstellaLogInitializer.FlushAsync(TimeSpan.FromSeconds(2));
                await InstellaLogInitializer.ShutdownAsync(TimeSpan.FromSeconds(2));
            }
        }

        // Injected services (the test harness) answer "is this the installed stub?" themselves.
        var dispatch = ModeDispatcher.Resolve(args, services is null ? null
            : ModeDispatcher.HasSiblingManifest(services.StubDirectoryOrDefault(), services.FileSystem));
        // A GUI installer has no console, so failures are also shown in a message box
        // (never when --silent, and never for injected services: a test must not open a window).
        var silent = dispatch.IsSilent || args.Contains("--silent", StringComparer.OrdinalIgnoreCase);
        var messages = services is null
            ? new UserMessages(silent)
            : services.Messages ?? new UserMessages(silent: true);
        var title = $"{_config.AppName} Setup";
        if (dispatch.Kind == DispatchKind.Help || VersionSelection.ListOnly(args))
            Core.ConsoleAttach.ToParentConsole();   // a GUI-subsystem exe started from a terminal
        if (dispatch.Kind == DispatchKind.Help)
        {
            WriteHelpToConsole();
            return (int)InstellaExitCode.Success;
        }
        if (dispatch.Kind == DispatchKind.Invalid)
        {
            messages.Error(title, UsageMessage(dispatch.Problem));
            Console.Error.WriteLine();
            WriteHelpToConsole();
            return (int)InstellaExitCode.UsageInvalidArgs;
        }

        // Parse the command line exactly once; steps read it through ctx.Cli.
        // Update and Recover are started by the app's SDK, which may be newer than this stub (a
        // stub is never replaced): they ignore options they don't know instead of failing
        // (docs/compatibility.md, "Updater command line"). Every other mode is strict.
        var lenient = dispatch.Mode is InstallerMode.Update or InstallerMode.Recover;
        IReadOnlyList<string> ignoredOptions = [];
        try
        {
            var cli = CliArgParser.Parse(args, _config.DeclaredCliFlags, strict: !lenient);
            ignoredOptions = cli.UnknownFlags;
            dispatch = dispatch with { Cli = cli };
        }
        catch (CliParseException ex) when (!lenient)
        {
            messages.Error(title, UsageMessage(ex.Message));
            Console.Error.WriteLine();
            WriteHelpToConsole();
            return (int)InstellaExitCode.UsageInvalidArgs;
        }
        catch (CliParseException ex)
        {
            // Maintenance modes never read author-declared flags, so nothing is lost.
            ignoredOptions = [ex.Message];
            dispatch = dispatch with { Cli = CliArgs.Empty };
        }

        try
        {
            services ??= RealServicesFactory();
        }
        catch (PlatformNotSupportedException ex)
        {
            messages.Error(title, UnsupportedMessage(ex.Message));
            return (int)InstellaExitCode.UnsupportedPlatform;
        }
        var platform = services.Platform;
        var fileSystem = services.FileSystem;

        InstellaLogInitializer.Initialize(
            _config.Logging,
            cliArgs: args,
            appId: _config.AppId,
            modeName: dispatch.Mode.ToString().ToLowerInvariant());

        var log = new LogsmithLoggerAdapter();

        if (ignoredOptions.Count > 0)
            log.Info($"ignoring options this updater does not know ({string.Join(", ", ignoredOptions)}); they may come from a newer SDK");

        if (!OperatingSystem.IsWindows())
            log.Warn(ExperimentalPlatformBanner);

        if (_config.AllowInsecureServer && Instella.Core.Wire.ServerUrlPolicy.IsInsecure(_config.ServerUrl))
            log.Warn($"server URL '{_config.ServerUrl}' uses plain http (AllowInsecureServer): downloads are not protected in transit"
                     + (_config.DownloadToken is null ? "." : ", and the download token is sent unencrypted."));
        if (_config.AllowUnsignedUpdates && !string.IsNullOrEmpty(_config.ServerUrl))
            log.Warn("AllowUnsignedUpdates is set: releases from the server are not verified against a publisher key.");

        // A footer that exists but fails verification means the file was modified or
        // corrupted: stop before any UI or network access. Only a file with no footer at all
        // (a lite installer) downloads its payload.
        if (dispatch.Mode is InstallerMode.FirstInstall or InstallerMode.Upgrade or InstallerMode.Repair)
        {
            try
            {
                Core.EmbeddedResources.EnsurePayloadIntact();
            }
            catch (Instella.Core.Internal.FooterIntegrityException ex)
            {
                log.Error($"install: {ex.Message}");
                messages.Error(title, UserMessages.WithLog("This installer file is damaged. Download it again."));
                await InstellaLogInitializer.FlushAsync(TimeSpan.FromSeconds(2));
                await InstellaLogInitializer.ShutdownAsync(TimeSpan.FromSeconds(2));
                return (int)InstellaExitCode.InstallIntegrityFailed;
            }
        }

        // Another version than this installer's own: chosen with the WithVersionSelection() flags,
        // or offered by WithNewerVersionPrompt(). Only the first process decides; the elevated
        // relaunch and the handed-over installer (--no-newer-check) do not ask again.
        var installing = dispatch.Mode is InstallerMode.FirstInstall or InstallerMode.Upgrade && !dispatch.ElevatedChild;
        var selecting = VersionSelection.Requested(args) && (installing || VersionSelection.ListOnly(args));
        if (selecting || (_config.OfferNewerVersion && installing && !dispatch.IsSilent && !args.Contains(InstallerHandoff.NoNewerCheckFlag)))
        {
            // The compiled-in download token opens a private package's versions and installers.
            using var http = services.CreateServerClient(_config.ServerUrl, _config.DownloadToken, "Instella-Installer/1.0");
            var handedOver = selecting
                ? await new VersionSelection(_config, log).RunAsync(http, args, dispatch.IsSilent, ct)
                : await new NewerVersionOffer(_config, log,
                    // Injected services (the harness) never see the real prompt.
                    prompt: services.HostFactory is null ? null : (_, _, _) => NewerVersionChoice.InstallThis,
                    showError: text => messages.Error("Download failed", text))
                {
                    InstalledVersion = token => InstalledVersionProbe.FindAsync(_config, dispatch, platform, fileSystem, token),
                }.RunAsync(http, args, ct);
            if (handedOver is { } handoffExit)
            {
                await InstellaLogInitializer.FlushAsync(TimeSpan.FromSeconds(2));
                await InstellaLogInitializer.ShutdownAsync(TimeSpan.FromSeconds(2));
                return handoffExit;
            }
        }

        try
        {
            // Scope and elevation: installs resolve their scope and may relaunch
            // elevated; operations on a machine-wide installation relaunch elevated too.
            var elevation = services.ElevationOrDefault;
            if (dispatch.Mode is InstallerMode.FirstInstall or InstallerMode.Upgrade or InstallerMode.Repair)
            {
                // A re-install targets the existing installation (its folder and scope).
                var (targeted, targetError) = await ExistingInstallTargeting.ApplyAsync(_config, dispatch, platform, fileSystem, ct);
                if (targetError is not null)
                {
                    log.Error($"install: {targetError}");
                    messages.Error(title, UsageMessage(targetError));
                    return (int)InstellaExitCode.UsageInvalidArgs;
                }
                dispatch = targeted;
                if (dispatch.Existing is { } existing)
                    log.Info($"install: {_config.AppName} {existing.Version} is installed in '{existing.Path}' ({existing.Source}); installing over it");

                var prompt = services.ScopePrompt ?? ScopePromptOverride
                    ?? (services.HostFactory is { } hosts
                        ? ElevationGate.DefaultScopePrompt(hosts, _config.AppVersion)
                        : OperatingSystem.IsWindows()
                            ? ElevationGate.DefaultScopePrompt(InteractiveInstallRunner.CreateDefaultWin32Host, _config.AppVersion)
                            : (_, _) => "user");
                var (resolved, stop) = await ElevationGate.ResolveInstallScopeAsync(_config, args, dispatch, elevation, prompt, log, ct, messages);
                if (stop is { } stopCode) return (int)stopCode;
                dispatch = resolved!.Value;
            }
            else if (dispatch.Mode is InstallerMode.Uninstall or InstallerMode.Recover or InstallerMode.Update
                     or InstallerMode.Manage)
            {
                // Target binding: the stub only ever elevates for the installation it
                // belongs to, so a process that cannot write a machine installation cannot use
                // the publisher's signed stub to act elevated on a folder it controls.
                var stubDir = services.StubDirectoryOrDefault();
                string? target;
                if (dispatch.Mode == InstallerMode.Update)
                {
                    var appPath = UpdaterArgs.Parse(args)?.AppPath;
                    if (appPath is not null && !IsStubFolder(appPath, stubDir))
                    {
                        var message = $"this updater only updates the installation it belongs to ({stubDir})";
                        log.Error($"update: --app-path '{appPath}': {message}");
                        messages.Error(title, UsageMessage(message));
                        return (int)InstellaExitCode.UsageInvalidArgs;
                    }
                    target = appPath;
                }
                else
                {
                    target = dispatch.InstallPath ?? stubDir;
                }
                var foreign = target is not null && !IsStubFolder(target, stubDir);
                if (foreign && dispatch.ElevatedChild)
                {
                    // The stub never elevates for a foreign target, so it did not start this child.
                    log.Error($"'{target}' is not this installer's folder ({stubDir}); an elevated relaunch is only made for that folder");
                    messages.Error(title, ElevationGate.NeedsAdminMessage(_config.AppName));
                    return (int)InstellaExitCode.InsufficientPrivileges;
                }

                // Manage elevates per action (ManageModeRunner), with the same rule.
                if (dispatch.Mode != InstallerMode.Manage)
                {
                    var relaunched = await ElevationGate.EnsureElevatedForInstallationAsync(
                        target, args, dispatch, fileSystem, elevation,
                        // The app starts updates and recovery, so a user is there to answer UAC.
                        promptEvenWhenSilent: dispatch.Mode is InstallerMode.Update or InstallerMode.Recover, log, ct, foreign,
                        messages, title);
                    if (relaunched is { } relaunchCode) return (int)relaunchCode;
                }
            }

            var exit = dispatch.Mode switch
            {
                InstallerMode.FirstInstall or InstallerMode.Upgrade or InstallerMode.Repair =>
                    await new InstallModeRunner(_config, log, platform, fileSystem) { Services = services }.RunAsync(dispatch, ct),
                InstallerMode.Update =>
                    await new UpdateModeRunner(_config, log, platform, fileSystem) { Services = services, Messages = messages }.RunAsync(args, ct),
                InstallerMode.Uninstall =>
                    await new UninstallModeRunner(_config, log, platform, fileSystem)
                    {
                        Messages = messages, ProcessFinder = services.ProcessFinder,
                    }.RunAsync(dispatch, ct),
                InstallerMode.Manage =>
                    await new ManageModeRunner(_config, log, platform, fileSystem,
                        services.ManagerUi ?? ManageModeRunner.DefaultManagerUiLauncher, uninstallHandoff: null)
                    {
                        Elevation = elevation, Messages = messages, HttpHandler = services.HttpHandler,
                        StubDirectory = services.StubDirectory,
                    }.RunAsync(dispatch, ct),
                InstallerMode.Cleanup =>
                    await new CleanupModeRunner(log).RunAsync(dispatch, args, ct),
                InstallerMode.Recover =>
                    await new RecoverModeRunner(log, fileSystem) { Messages = messages, AppName = _config.AppName }.RunAsync(dispatch, ct),
                _ => InstellaExitCode.UsageUnknownMode,
            };
            return (int)exit;
        }
        catch (PlatformNotSupportedException ex)
        {
            // An OS or process architecture Instella has no support for (for example an
            // architecture outside x64/x86/arm64/arm32).
            log.Error($"{ex.Message}");
            messages.Error(title, UnsupportedMessage(ex.Message));
            return (int)InstellaExitCode.UnsupportedPlatform;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Nothing may end the installer with an unhandled exception and no word.
            log.Error($"unexpected error: {ex}");
            messages.Error(title, UserMessages.WithLog($"Something went wrong: {ex.Message}"));
            return (int)(dispatch.Mode switch
            {
                InstallerMode.Update => InstellaExitCode.UpdateGeneralFailure,
                InstallerMode.Uninstall => InstellaExitCode.UninstallGeneralFailure,
                _ => InstellaExitCode.InstallGeneralFailure,
            });
        }
        finally
        {
            await InstellaLogInitializer.FlushAsync(TimeSpan.FromSeconds(2));
            await InstellaLogInitializer.ShutdownAsync(TimeSpan.FromSeconds(2));
        }
    }

    private static string UsageMessage(string? problem) => $"{problem}\n\nRun with --help for the options.";

    private string UnsupportedMessage(string reason) =>
        $"{_config.AppName} can't be installed on this system: {reason.TrimEnd('.')}.";

    /// <summary>Whether <paramref name="target"/> is the folder the running stub lives in.</summary>
    private static bool IsStubFolder(string target, string? stubDir) =>
        stubDir is not null
        && InstallPaths.TryNormalize(target, requireRooted: false, out var t, out _)
        && InstallPaths.TryNormalize(stubDir, requireRooted: false, out var s, out _)
        && InstallPaths.SameFolder(t, s);

    /// <summary>
    /// Scan <paramref name="args"/> for <c>--emit-manifest &lt;path&gt;</c>.
    /// Accepted forms: <c>--emit-manifest path</c> (two tokens),
    /// <c>--emit-manifest=path</c> (single token). Returns <see langword="false"/>
    /// when the flag is absent.
    /// </summary>
    internal static bool TryExtractEmitManifestPath(string[] args, out string? path)
    {
        path = null;
        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg is null) continue;

            if (string.Equals(arg, "--emit-manifest", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length)
                {
                    path = args[i + 1];
                    return !string.IsNullOrWhiteSpace(path);
                }
                return false;
            }

            if (arg.StartsWith("--emit-manifest=", StringComparison.OrdinalIgnoreCase))
            {
                path = arg["--emit-manifest=".Length..];
                return !string.IsNullOrWhiteSpace(path);
            }
        }
        return false;
    }

    /// <summary>
    /// The icon as a file the build task can embed: a file source becomes an
    /// absolute path; resource and byte sources are written next to the manifest.
    /// </summary>
    internal static async Task<string?> MaterializeIconAsync(UI.Widgets.ImageSource? icon, string manifestPath, CancellationToken ct)
    {
        byte[]? bytes = icon switch
        {
            UI.Widgets.FileImageSource file => null,
            UI.Widgets.BytesImageSource b => b.Bytes,
            UI.Widgets.ResourceImageSource r => ReadResource(r),
            _ => null,
        };
        if (icon is UI.Widgets.FileImageSource f) return Path.GetFullPath(f.Path);
        if (bytes is null) return null;

        var path = Path.GetFullPath(manifestPath) + ".app.ico";
        await File.WriteAllBytesAsync(path, bytes, ct);
        return path;

        static byte[] ReadResource(UI.Widgets.ResourceImageSource r)
        {
            using var stream = r.Assembly.GetManifestResourceStream(r.Name)
                ?? throw new InvalidOperationException($"WithIcon: embedded resource '{r.Name}' not found in {r.Assembly.GetName().Name}.");
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }
    }

    internal static async Task WriteManifestJsonAsync(
        string path, InstellaManifest manifest, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(
            stream, manifest, ManifestJsonContext.Default.InstellaManifest, ct);
    }

    private void WriteHelpToConsole()
    {
        var help = $"""
            {_config.AppName} v{_config.AppVersion} installer

            Usage:
              {Path.GetFileName(Environment.ProcessPath ?? "installer.exe")} [mode] [options]

            Modes (mutually exclusive; default: --install if no local manifest, --manage otherwise):
              --install           Run a fresh install (default when no local manifest is present).
              --update            Perform an in-place update (started by the app through Instella.Sdk).
                                  Requires --app-path, --app-exe, --from-version, --to-version.
              --uninstall         Remove the installation located by --path (or the installer's own directory).
              --manage            Show installed-app status (default when a local manifest is present).
              --cleanup           Started by uninstall to delete what it could not (guarded by a tombstone).
              --recover           Finish or undo an interrupted update in --path (or this program's folder).

            Common options:
              --path <dir>        Install / uninstall target directory.
              --silent            Headless mode; fail if inputs incomplete.
              --allow-downgrade   Let this installer replace a newer installed version.
              --force             Install into a non-empty folder that holds no installation.
              --force-close       Close programs using the app's files without asking.
              --no-newer-check    Install this version without offering a newer one.
              --scope user|machine  Install for the current user (default) or all users (needs administrator).
              --log-level <name>  Trace|Debug|Info|Warn|Error|Critical.
              --help              Show this message.
            """;
        Console.WriteLine(help);

        if (_config.AllowVersionSelection)
        {
            Console.WriteLine();
            Console.WriteLine("Version selection:");
            Console.WriteLine("  --list-versions     List the versions that can be installed (--channel <name> for another channel).");
            Console.WriteLine("  --app-version <v>   Install version <v>, or 'latest' (works with --silent).");
            Console.WriteLine("  --choose-version    Choose the version to install from a list.");
        }

        if (_config.DeclaredCliFlags.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"{_config.AppName} options:");
            foreach (var flag in _config.DeclaredCliFlags)
            {
                var name = "--" + flag.Name.TrimStart('-');
                var usage = flag.ValueType == typeof(bool) ? name : $"{name} <{flag.ValueType.Name.ToLowerInvariant()}>";
                var mapped = flag.MapsTo is null ? "" : $" (answers {flag.MapsTo})";
                Console.WriteLine($"  {usage,-20}{flag.Help}{mapped}");
            }
        }
    }
}
