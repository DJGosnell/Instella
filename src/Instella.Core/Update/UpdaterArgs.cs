using System.Globalization;
using Instella.Core.Utilities;

namespace Instella.Core.Update;

/// <summary>
/// The updater's command line, owned in one place for both sides: the SDK builds
/// it with <see cref="ToArgumentList"/> and the stub reads it with <see cref="Parse"/>, so the
/// SDK can never emit something the updater does not parse.
/// </summary>
/// <remarks>
/// The server URL and package id are deliberately absent: the updater reads both from the
/// installed manifest, because anything on a command line is attacker-influenced input to a
/// process that is about to replace executables.
/// </remarks>
internal sealed record UpdaterArgs
{
    /// <summary>Installation directory (the one holding the installed manifest).</summary>
    public required string AppPath { get; init; }

    /// <summary>Main executable, relative to <see cref="AppPath"/>.</summary>
    public required string AppExecutable { get; init; }

    /// <summary>Version the update was offered for; a mismatch with the installed manifest means a stale launch.</summary>
    public required Version FromVersion { get; init; }

    /// <summary>Version to update to (equals the installed version for a repair).</summary>
    public required Version ToVersion { get; init; }

    /// <summary>Release channel.</summary>
    public string Channel { get; init; } = "stable";

    /// <summary>Use the patch archive when one exists.</summary>
    public bool UsePatch { get; init; }

    /// <summary>SHA-256 of the patch archive from <c>check-update</c>; a corruption check, not a trust anchor.</summary>
    public string? PatchSha256 { get; init; }

    /// <summary>Kill the app when it does not close within <see cref="GracefulTimeout"/>.</summary>
    public bool AllowForceClose { get; init; }

    /// <summary>How long to wait for the app to close gracefully.</summary>
    public TimeSpan GracefulTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Restart the app afterwards (always written explicitly).</summary>
    public bool Restart { get; init; } = true;

    /// <summary>
    /// How long the update window counts down before restarting the app (<c>--restart-countdown</c>,
    /// in whole seconds; absent means 5). Zero closes the window and restarts at once.
    /// </summary>
    public TimeSpan RestartCountdown { get; init; } = DefaultRestartCountdown;

    /// <summary>The countdown when <c>--restart-countdown</c> is absent.</summary>
    public static readonly TimeSpan DefaultRestartCountdown = TimeSpan.FromSeconds(5);

    /// <summary>No UI.</summary>
    public bool Silent { get; init; }

    /// <summary>Re-verify the installed version instead of updating.</summary>
    public bool Repair { get; init; }

    /// <summary>The app process that launched the updater; it is waited on before any file is touched.</summary>
    public int? ParentPid { get; init; }

    /// <summary>Arguments for the restarted app. Everything after <c>--extra-args</c>, so it stays last.</summary>
    public IReadOnlyList<string> ExtraArgs { get; init; } = [];

    /// <summary>The argument list <see cref="Parse"/> reads back into an equal instance.</summary>
    public IReadOnlyList<string> ToArgumentList()
    {
        var a = new List<string>
        {
            "--update",
            "--app-path", AppPath,
            "--app-exe", AppExecutable,
            "--from-version", FromVersion.ToString(),
            "--to-version", ToVersion.ToString(),
            "--channel", Channel,
            "--graceful-timeout", ((int)GracefulTimeout.TotalSeconds).ToString(CultureInfo.InvariantCulture),
            Restart ? "--restart" : "--no-restart",
            "--restart-countdown", ((int)RestartCountdown.TotalSeconds).ToString(CultureInfo.InvariantCulture),
        };
        if (UsePatch) a.Add("--use-patch");
        if (PatchSha256 is { } sha) a.AddRange(["--patch-sha256", sha]);
        if (AllowForceClose) a.Add("--allow-force-close");
        if (Silent) a.Add("--silent");
        if (Repair) a.Add("--repair");
        if (ParentPid is { } pid) a.AddRange(["--parent-pid", pid.ToString(CultureInfo.InvariantCulture)]);
        if (ExtraArgs.Count > 0)
        {
            a.Add("--extra-args");
            a.AddRange(ExtraArgs);
        }
        return a;
    }

    /// <summary>
    /// Reads an argument list written by <see cref="ToArgumentList"/> (a leading
    /// <c>--update</c> is optional). Returns null when a required value is missing or malformed.
    /// Values are taken positionally, so a path that starts with "--" still round-trips.
    /// </summary>
    public static UpdaterArgs? Parse(IReadOnlyList<string> args)
    {
        string? appPath = null, appExe = null, channel = null, patchSha = null;
        Version? from = null, to = null;
        int timeout = 30;
        int countdown = (int)DefaultRestartCountdown.TotalSeconds;
        int? parentPid = null;
        bool usePatch = false, forceClose = false, restart = true, silent = false, repair = false;
        var extra = new List<string>();

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg == "--extra-args")
            {
                for (i++; i < args.Count; i++) extra.Add(args[i]);
                break;
            }

            string? Next() => i + 1 < args.Count ? args[++i] : null;
            switch (arg)
            {
                case "--update": break;
                case "--app-path": appPath = Next(); break;
                case "--app-exe": appExe = Next(); break;
                case "--from-version": if (!AppVersions.TryParse(Next(), out from)) return null; break;
                case "--to-version": if (!AppVersions.TryParse(Next(), out to)) return null; break;
                case "--channel": channel = Next(); break;
                case "--patch-sha256": patchSha = Next(); break;
                case "--graceful-timeout":
                    if (!int.TryParse(Next(), NumberStyles.Integer, CultureInfo.InvariantCulture, out timeout)) return null;
                    break;
                case "--restart-countdown":
                    // Lenient: a bad value keeps the default rather than failing the update.
                    if (!int.TryParse(Next(), NumberStyles.Integer, CultureInfo.InvariantCulture, out countdown) || countdown < 0)
                        countdown = (int)DefaultRestartCountdown.TotalSeconds;
                    break;
                case "--parent-pid":
                    if (!int.TryParse(Next(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid)) return null;
                    parentPid = pid;
                    break;
                case "--use-patch": usePatch = true; break;
                case "--allow-force-close": forceClose = true; break;
                case "--restart": restart = true; break;
                case "--no-restart": restart = false; break;
                case "--silent": silent = true; break;
                case "--repair": repair = true; break;
            }
        }

        if (appPath is null || appExe is null || from is null || to is null)
            return null;

        return new UpdaterArgs
        {
            AppPath = appPath,
            AppExecutable = appExe,
            FromVersion = from,
            ToVersion = to,
            Channel = channel ?? "stable",
            UsePatch = usePatch,
            PatchSha256 = patchSha,
            AllowForceClose = forceClose,
            GracefulTimeout = TimeSpan.FromSeconds(timeout),
            Restart = restart,
            RestartCountdown = TimeSpan.FromSeconds(countdown),
            Silent = silent,
            Repair = repair,
            ParentPid = parentPid,
            ExtraArgs = extra,
        };
    }

    /// <summary>Record equality with <see cref="ExtraArgs"/> compared by content.</summary>
    public bool Equals(UpdaterArgs? other) =>
        other is not null
        && AppPath == other.AppPath && AppExecutable == other.AppExecutable
        && AppVersions.Equal(FromVersion, other.FromVersion) && AppVersions.Equal(ToVersion, other.ToVersion) && Channel == other.Channel
        && UsePatch == other.UsePatch && PatchSha256 == other.PatchSha256 && AllowForceClose == other.AllowForceClose
        && GracefulTimeout == other.GracefulTimeout && Restart == other.Restart && Silent == other.Silent
        && RestartCountdown == other.RestartCountdown
        && Repair == other.Repair && ParentPid == other.ParentPid && ExtraArgs.SequenceEqual(other.ExtraArgs);

    public override int GetHashCode() => HashCode.Combine(AppPath, AppVersions.Normalize(FromVersion), AppVersions.Normalize(ToVersion), ParentPid);
}
