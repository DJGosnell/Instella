using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Installation;
using Instella.Core.Logging;
using Instella.Core.Trust;
using Instella.Core.Utilities;
using Instella.Core.Wire;
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Runtime.UI.Widgets;

namespace Instella.Installer.Runtime.Runners;

/// <summary>Picks a version from <paramref name="versions"/> (newest first); null means cancel.</summary>
internal delegate Version? VersionPicker(string appName, Version current, IReadOnlyList<AvailableVersion> versions);

/// <summary>
/// <c>WithVersionSelection()</c>: <c>--list-versions</c> prints the versions that have an online
/// installer for this platform; <c>--app-version &lt;v|latest&gt;</c> installs that version;
/// <c>--choose-version</c> asks which one. <c>--channel</c> picks the channel (default: the
/// installer's <c>WithChannel</c>); deprecated versions are never offered. Any version other than
/// this installer's own is installed by handing over to its own installer
/// (<see cref="InstallerHandoff"/>), verified against this installer's publisher keys.
/// </summary>
internal sealed class VersionSelection
{
    public const string ListFlag = "--list-versions";
    public const string AppVersionFlag = "--app-version";
    public const string ChooseFlag = "--choose-version";
    private const string ChannelFlag = "--channel";

    private readonly FrozenConfig _config;
    private readonly IInstellaLogger _log;
    private readonly TextWriter _output;
    private readonly VersionPicker _picker;

    public VersionSelection(FrozenConfig config, IInstellaLogger log, TextWriter? output = null, VersionPicker? picker = null)
    {
        _config = config;
        _log = log;
        _output = output ?? Console.Out;
        _picker = picker ?? DefaultPicker(InteractiveInstallRunner.CreateDefaultWin32Host);
    }

    /// <summary>Test hook: runs the downloaded installer; null starts it and waits.</summary>
    internal Func<string, IReadOnlyList<string>, CancellationToken, Task<int>>? Launcher { get; init; }

    /// <summary>Test hook: reads Authenticode signatures; null uses WinVerifyTrust on Windows.</summary>
    internal Instella.Core.Platform.Windows.IAuthenticodeReader? Authenticode { get; init; }

    /// <summary>Test hook: the running installer's path, for its own signature.</summary>
    internal string? SelfPath { get; init; }

    /// <summary>True when <paramref name="args"/> use any of the version-selection flags.</summary>
    public static bool Requested(IReadOnlyList<string> args) =>
        args.Any(a => a is ListFlag or ChooseFlag || a == AppVersionFlag || a.StartsWith(AppVersionFlag + "=", StringComparison.Ordinal));

    /// <summary>True when <paramref name="args"/> only list versions (no install happens).</summary>
    public static bool ListOnly(IReadOnlyList<string> args) => args.Contains(ListFlag);

    /// <summary>
    /// Handles the flags. Returns the exit code to end with (a listing, an error, a cancel, or the
    /// handed-over installer's), or null when this installer's own version was chosen and the
    /// install should go on here.
    /// </summary>
    public async Task<int?> RunAsync(HttpClient http, IReadOnlyList<string> args, bool silent, CancellationToken ct)
    {
        if (!_config.AllowVersionSelection)
            return Fail(InstellaExitCode.UsageInvalidArgs,
                $"{ListFlag}, {AppVersionFlag} and {ChooseFlag} are not enabled in this installer (InstallerBuilder.WithVersionSelection()).");

        var channel = _config.Channel;
        if (ValueOf(args, ChannelFlag) is { } requestedChannel && !ChannelNames.TryNormalize(requestedChannel, out channel))
            return Fail(InstellaExitCode.UsageInvalidArgs, $"{ChannelFlag} '{requestedChannel}': {ChannelNames.Rule}.");
        IReadOnlyList<AvailableVersion> versions;
        try
        {
            versions = await InstallerHandoff.ListAsync(_config, http, channel, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or NotSupportedException
                                   || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return Fail(InstellaExitCode.UpdateServerUnreachable, $"could not list the versions on {_config.ServerUrl}: {ex.Message}");
        }

        if (ListOnly(args))
        {
            WriteList(versions, channel);
            return (int)InstellaExitCode.Success;
        }

        Version target;
        var requested = ValueOf(args, AppVersionFlag);
        if (requested is not null)
        {
            if (requested.Equals("latest", StringComparison.OrdinalIgnoreCase))
            {
                if (versions.Count == 0 || AppVersions.Compare(versions[0].Version, _config.AppVersion) <= 0)
                {
                    _log.Info($"version selection: {_config.AppVersion} is the latest; installing it");
                    return null;
                }
                target = versions[0].Version;
            }
            else if (AppVersions.TryParse(requested, out var parsed))
            {
                target = parsed;
                if (!AppVersions.Equal(target, _config.AppVersion) && versions.All(v => !AppVersions.Equal(v.Version, target)))
                    return Fail(InstellaExitCode.UsageInvalidArgs,
                        $"version {target} has no installer for this platform on the '{channel}' channel (see {ListFlag}).");
            }
            else
            {
                return Fail(InstellaExitCode.UsageInvalidArgs, $"{AppVersionFlag} expects a version or 'latest', got '{requested}'.");
            }
        }
        else
        {
            if (silent)
                return Fail(InstellaExitCode.UsageInvalidArgs, $"{ChooseFlag} needs the interactive installer; use {AppVersionFlag} with --silent.");
            var offered = versions.Any(v => v.Version == _config.AppVersion)
                ? versions
                : versions.Append(new AvailableVersion(_config.AppVersion, null, default, 0)).OrderByDescending(v => v.Version).ToList();
            var picked = _picker(_config.AppName, _config.AppVersion, offered);
            if (picked is null)
            {
                _log.Info("version selection: user cancelled");
                return (int)InstellaExitCode.UserCancelled;
            }
            target = picked;
        }

        if (target == _config.AppVersion)
        {
            _log.Info($"version selection: installing this installer's version {target}");
            return null;
        }

        _log.Info($"version selection: handing over to the {target} installer");
        VerifiedInstallerFile file;
        try
        {
            var verified = await InstallerHandoff.VerifyAsync(_config, http, target, channel, ct);
            file = await InstallerHandoff.DownloadAsync(_config, http, verified, ct, Authenticode, SelfPath, _log);
        }
        catch (Exception ex) when (ex is HttpRequestException or UpdateTrustException or IOException or InstallerSignerMismatchException)
        {
            return Fail(InstellaExitCode.InstallIntegrityFailed, $"the installer for {target} could not be used: {ex.Message}");
        }
        // The handle that keeps writers out is released only after the installer has run.
        await using (file)
            return await (Launcher ?? InstallerHandoff.RunAsync)(file.Path, WithoutSelectionFlags(args), ct);
    }

    /// <summary><paramref name="args"/> without the selection flags (and their values), for the handed-over installer.</summary>
    internal static List<string> WithoutSelectionFlags(IReadOnlyList<string> args)
    {
        var result = new List<string>(args.Count);
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a is ListFlag or ChooseFlag || a.StartsWith(AppVersionFlag + "=", StringComparison.Ordinal)
                || a.StartsWith(ChannelFlag + "=", StringComparison.Ordinal)) continue;
            if (a is AppVersionFlag or ChannelFlag)
            {
                if (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) i++;
                continue;
            }
            result.Add(a);
        }
        return result;
    }

    private void WriteList(IReadOnlyList<AvailableVersion> versions, string channel)
    {
        _output.WriteLine($"{_config.AppName} versions on the '{channel}' channel with an installer for this platform:");
        if (versions.Count == 0) _output.WriteLine("  (none)");
        for (var i = 0; i < versions.Count; i++)
        {
            var v = versions[i];
            var notes = new List<string>();
            if (i == 0) notes.Add("latest");
            if (v.Version == _config.AppVersion) notes.Add("this installer");
            var suffix = notes.Count > 0 ? $"  ({string.Join(", ", notes)})" : "";
            _output.WriteLine($"  {v.Version,-12} {v.ReleasedAt:yyyy-MM-dd}{suffix}");
            if (!string.IsNullOrWhiteSpace(v.Changelog))
                foreach (var line in v.Changelog!.Trim().Split('\n'))
                    _output.WriteLine("      " + line.TrimEnd('\r'));
        }
        _output.WriteLine();
        _output.WriteLine($"Install one with {AppVersionFlag} <version> (or 'latest').");
    }

    private int Fail(InstellaExitCode code, string message)
    {
        _log.Error($"version selection: {message}");
        Console.Error.WriteLine($"error: {message}");
        return (int)code;
    }

    private static string? ValueOf(IReadOnlyList<string> args, string name)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == name && i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) return args[i + 1];
            if (args[i].StartsWith(name + "=", StringComparison.Ordinal)) return args[i][(name.Length + 1)..];
        }
        return null;
    }

    /// <summary>The Windows picker: a one-page window with a drop-down of versions and their changelogs.</summary>
    internal static VersionPicker DefaultPicker(InteractiveHostFactory hostFactory) => (appName, current, versions) =>
    {
        if (!OperatingSystem.IsWindows()) return null;
        var labels = versions.Select(v => Label(v, current, v == versions[0])).ToList();
        var notes = new StringBuilder();
        foreach (var v in versions.Where(v => !string.IsNullOrWhiteSpace(v.Changelog)))
            notes.Append(v.Version).Append(v.ReleasedAt == default ? "" : $" ({v.ReleasedAt:yyyy-MM-dd})").Append("\r\n")
                .Append(v.Changelog!.Trim().Replace("\r\n", "\n").Replace("\n", "\r\n")).Append("\r\n\r\n");

        var page = InteractivePages.BuildPage("instella-version", p =>
        {
            p.Heading($"Choose the {appName} version to install");
            p.Widget(new Dropdown("Version:", labels, Default: labels[0]) { Id = "version" });
            if (notes.Length > 0) p.ScrollableText(notes.ToString().TrimEnd());
            p.ContinueWhen(s => !string.IsNullOrEmpty(s.Text("version")));
        }) with { ContinueLabel = "&Next >" };
        var state = new PageState();
        PreviewModeRunner.SeedWidgetDefaults(page, state);
        using var host = hostFactory(appName, [page], [state]);
        if (host.Run() != InteractiveHostOutcome.Completed) return null;
        var index = labels.IndexOf(state.Text("version") ?? "");
        return index >= 0 ? versions[index].Version : null;
    };

    private static string Label(AvailableVersion v, Version current, bool newest)
    {
        var notes = new List<string>();
        if (newest) notes.Add("latest");
        if (v.Version == current) notes.Add("this installer");
        return notes.Count > 0 ? $"{v.Version} ({string.Join(", ", notes)})" : v.Version.ToString();
    }
}
