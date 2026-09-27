using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.FileSystem;
using Instella.Core.Installation;
using Instella.Core.Logging;
using Instella.Core.Manifest;
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Runtime.Core;
using Instella.Installer.Runtime.Core.Elevation;
using Instella.Installer.Runtime.UI.Widgets;

namespace Instella.Installer.Runtime.Runners;

/// <summary>Asks the user where to install; returns "user", "machine", or null when cancelled.</summary>
/// <param name="appName">The application name, for the prompt text.</param>
/// <param name="notice">Shown above the choice, e.g. after the UAC prompt was declined.</param>
internal delegate string? ScopePrompt(string appName, string? notice);

/// <summary>
/// Turns scope decisions into "run here", "relaunch elevated" or "exit 51".
/// Nothing a user answered ever crosses the elevation boundary: the scope page is the
/// first thing shown, and the elevated child shows every other page itself.
/// </summary>
internal static class ElevationGate
{
    private const string DeclinedNotice =
        "Administrator rights were not granted, so the installer cannot install for all users. " +
        "Choose \"Install for me only\", or try again and accept the prompt.";

    /// <summary>
    /// Resolves the install scope. Returns the dispatch to run with (its
    /// <see cref="DispatchResult.Scope"/> set), or the exit code to stop with — the elevated
    /// child's own exit code after a relaunch.
    /// </summary>
    public static async Task<(DispatchResult? Dispatch, InstellaExitCode? Exit)> ResolveInstallScopeAsync(
        FrozenConfig config, string[] args, DispatchResult dispatch, IElevationService elevation, ScopePrompt prompt,
        IInstellaLogger log, CancellationToken ct, IUserMessages? messages = null)
    {
        var title = $"{config.AppName} Setup";
        // Installed for the user and for the machine: the scope page says so and chooses.
        var notice = ExistingInstallTargeting.TwoInstallationsNotice(config, dispatch);
        while (true)
        {
            var decision = ScopeResolver.Resolve(config.Elevation, dispatch.IsSilent, dispatch.ScopeArgument, elevation.IsElevated,
                dispatch.Existing?.Scope ?? (dispatch.Installations is { Count: > 1 } ? ScopeFromArgument(dispatch.ScopeArgument) : null));
            if (decision.Action == ScopeAction.Proceed && dispatch.Installations is { Count: > 1 } && dispatch.ScopeArgument is null
                && dispatch.Existing is null && !dispatch.IsSilent)
                decision = new ScopeDecision(ScopeAction.AskUser);
            switch (decision.Action)
            {
                case ScopeAction.Proceed:
                    return (ExistingInstallTargeting.SelectForScope(dispatch with { Scope = decision.Scope }), null);

                case ScopeAction.InvalidScopeArgument:
                    log.Error($"install: --scope must be 'user' or 'machine', not '{dispatch.ScopeArgument}'");
                    messages?.Error(title, $"--scope must be 'user' or 'machine', not '{dispatch.ScopeArgument}'.\n\nRun with --help for the options.");
                    return (null, InstellaExitCode.UsageInvalidArgs);

                case ScopeAction.RefuseNotElevated:
                    log.Error("install: a machine-wide install needs administrator rights; run the installer from an elevated prompt (silent installs never show a UAC prompt)");
                    messages?.Error(title, NeedsAdminMessage(config.AppName));
                    return (null, InstellaExitCode.InsufficientPrivileges);

                case ScopeAction.RelaunchElevated:
                    if (dispatch.ElevatedChild)
                    {
                        log.Error("install: relaunched for elevation but still not elevated");
                        messages?.Error(title, NeedsAdminMessage(config.AppName));
                        return (null, InstellaExitCode.InsufficientPrivileges);
                    }
                    log.Info("install: relaunching elevated for a machine-wide install");
                    var code = await elevation.RelaunchElevatedAsync(ForwardArgs(args, "machine", dispatch.InstallPath), ct);
                    if (code is { } exit)
                        return (null, (InstellaExitCode)exit);
                    if (config.Elevation == ElevationMode.UserChoice)
                    {
                        // Back to the scope page with a notice.
                        notice = DeclinedNotice;
                        dispatch = dispatch with { ScopeArgument = null };
                        continue;
                    }
                    log.Error("install: the administrator prompt was declined");
                    messages?.Error(title, NeedsAdminMessage(config.AppName));
                    return (null, InstellaExitCode.InsufficientPrivileges);

                case ScopeAction.AskUser:
                    var choice = prompt(config.AppName, notice);
                    if (choice is null)
                        return (null, InstellaExitCode.UserCancelled);
                    dispatch = dispatch with { ScopeArgument = choice };
                    continue;
            }
        }
    }

    /// <summary>
    /// Uninstall, update, repair and recover of a machine-wide installation need administrator
    /// rights: when the process lacks them it relaunches itself elevated with the same
    /// arguments and returns the child's exit code. Null means "carry on in this process".
    /// </summary>
    /// <param name="installPath">The installation to act on.</param>
    /// <param name="args">This process's arguments, forwarded to the elevated copy.</param>
    /// <param name="dispatch">The parsed command line.</param>
    /// <param name="fs">File system, to read the installed manifest's scope.</param>
    /// <param name="elevation">Elevation service.</param>
    /// <param name="promptEvenWhenSilent">Updates prompt even when silent (documented; a prompt-free updater is 1.x).</param>
    /// <param name="log">Installer log.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="foreign">
    /// The installation is not the one this stub belongs to: it may be acted on unelevated,
    /// but never triggers a UAC prompt (target binding, docs/security-model.md).
    /// </param>
    /// <param name="messages">Shows why this process stops with exit 51; null only logs (and prints the foreign case).</param>
    /// <param name="appName">Title for those messages.</param>
    public static async Task<InstellaExitCode?> EnsureElevatedForInstallationAsync(
        string? installPath, string[] args, DispatchResult dispatch, IFileSystem fs, IElevationService elevation,
        bool promptEvenWhenSilent, IInstellaLogger log, CancellationToken ct, bool foreign = false,
        IUserMessages? messages = null, string? appName = null)
    {
        var title = appName ?? "Instella";
        if (string.IsNullOrEmpty(installPath) || elevation.IsElevated) return null;
        var manifest = await new InstallManifestWriter(fs).ReadAsync(installPath, ct);
        if (manifest is null || manifest.InstalledPerUser) return null;

        if (foreign)
        {
            log.Error(ForeignMachineInstallMessage(installPath));
            if (messages is null) Console.Error.WriteLine($"error: {ForeignMachineInstallMessage(installPath)}");
            else messages.Error(title, ForeignMachineInstallMessage(installPath));
            return InstellaExitCode.InsufficientPrivileges;
        }

        if (dispatch.ElevatedChild)
        {
            log.Error("relaunched for elevation but still not elevated");
            messages?.Error(title, NeedsAdminMessage(manifest.AppName));
            return InstellaExitCode.InsufficientPrivileges;
        }
        if (dispatch.IsSilent && !promptEvenWhenSilent)
        {
            log.Error($"'{installPath}' is a machine-wide installation; run this from an elevated prompt");
            messages?.Error(title, NeedsAdminMessage(manifest.AppName));
            return InstellaExitCode.InsufficientPrivileges;
        }

        log.Info($"'{installPath}' is a machine-wide installation; relaunching elevated");
        var code = await elevation.RelaunchElevatedAsync(ForwardArgs(args, scope: null, dispatch.InstallPath), ct);
        if (code is { } exit) return (InstellaExitCode)exit;
        log.Error("the administrator prompt was declined");
        messages?.Error(title, NeedsAdminMessage(manifest.AppName));
        return InstellaExitCode.InsufficientPrivileges;
    }

    /// <summary>Exit 51, for the user.</summary>
    internal static string NeedsAdminMessage(string appName) => UserMessages.WithLog(
        $"{appName} needs administrator rights for this, and the permission prompt was declined or is not available.");

    private static InstallationScope? ScopeFromArgument(string? value) => value?.ToLowerInvariant() switch
    {
        "user" => InstallationScope.PerUser,
        "machine" => InstallationScope.SystemWide,
        _ => null,
    };

    /// <summary>What to do about a machine-wide installation that is not this stub's own.</summary>
    internal static string ForeignMachineInstallMessage(string installPath) =>
        $"'{installPath}' is installed for all users. Run the uninstaller in that folder " +
        $"({Path.Combine(installPath, InstellaOwnedPaths.StubFileName)}), or run this one as administrator.";

    /// <summary>
    /// The arguments for the elevated child: the originals without any earlier
    /// <c>--scope</c> / <c>--elevated-child</c>, plus <c>--scope {scope}</c> (when given) and
    /// <c>--elevated-child</c>, which stops a relaunch loop.
    /// </summary>
    /// <remarks>
    /// With <paramref name="installPath"/>, the <c>--path</c> value is replaced by it (the
    /// normalised path), so the child does not resolve a relative path against a different
    /// working directory.
    /// </remarks>
    internal static IReadOnlyList<string> ForwardArgs(IReadOnlyList<string> args, string? scope, string? installPath = null)
    {
        // Only this process's own options are rewritten: everything from --extra-args on
        // belongs to the app and is copied verbatim, after the added options.
        var end = -1;
        for (var i = 0; i < args.Count && end < 0; i++)
            if (args[i] == "--extra-args") end = i;
        var ownCount = end < 0 ? args.Count : end;

        var forwarded = new List<string>(args.Count + 3);
        for (var i = 0; i < ownCount; i++)
        {
            var a = args[i];
            if (a == "--elevated-child" || a.StartsWith("--scope=", StringComparison.Ordinal)) continue;
            if (a == "--scope") { i++; continue; }
            if (installPath is not null && a == "--path" && i + 1 < ownCount)
            {
                forwarded.AddRange(["--path", installPath]);
                i++;
                continue;
            }
            if (installPath is not null && a.StartsWith("--path=", StringComparison.Ordinal))
            {
                forwarded.AddRange(["--path", installPath]);
                continue;
            }
            forwarded.Add(a);
        }
        if (scope is not null) forwarded.AddRange(["--scope", scope]);
        // A folder this process found (the existing installation) goes to the child too, so it
        // does not have to find it again (or find another one).
        if (installPath is not null && !forwarded.Contains("--path")) forwarded.AddRange(["--path", installPath]);
        forwarded.Add("--elevated-child");
        for (var i = ownCount; i < args.Count; i++) forwarded.Add(args[i]);
        return forwarded;
    }

    /// <summary>The built-in scope page, shown first for <see cref="ElevationMode.UserChoice"/>.</summary>
    public static ScopePrompt DefaultScopePrompt(InteractiveHostFactory hostFactory, Version version) => (appName, notice) =>
    {
        var page = InteractivePages.BuildPage("instella-scope", p =>
        {
            p.Heading($"Install {appName}");
            p.Paragraph($"Version {version}");   // the first window, so say which version this is
            if (notice is not null) p.Paragraph(notice);
            p.Widget(new RadioGroup("Install for:",
                [
                    new RadioOption("user", "Install for me only"),
                    new RadioOption("machine", "Install for all users (requires administrator)"),
                ], Default: "user") { Id = "scope" });
            p.ContinueWhen(s => s.Text("scope") is "user" or "machine");
        }) with { ContinueLabel = "&Next >" };
        var state = new PageState();
        PreviewModeRunner.SeedWidgetDefaults(page, state);
        using var host = hostFactory(appName, [page], [state]);
        return host.Run() == InteractiveHostOutcome.Completed ? state.Text("scope") : null;
    };
}
