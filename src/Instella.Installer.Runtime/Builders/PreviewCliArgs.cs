using System;
using System.Collections.Generic;
using Instella.Core.Installation;

namespace Instella.Installer.Runtime.Builders;

/// <summary>
/// One simulated-step delay bucket — selected via <c>--preview-speed</c>.
/// </summary>
internal enum PreviewSpeed
{
    Fast,
    Normal,
    Slow,
}

/// <summary>
/// Parsed preview-mode CLI flags. Produced by
/// <c>PreviewCliArgs.Parse</c>; consumed by the installer's routing
/// shim and <see cref="Installation.SimulatedStepExecutor"/>. All four flags
/// accept both <c>--flag value</c> and <c>--flag=value</c> forms, and their
/// flag names are compared case-insensitively.
/// </summary>
/// <remarks>
/// Parsing is best-effort: a structurally valid value (e.g. a known
/// <c>--preview-mode</c> or <c>--preview-speed</c>) is accepted verbatim; an
/// unknown value produces <see cref="PreviewCliParseResult.Failed"/> with an
/// explanatory message. The caller (<c>PreviewModeRunner</c>) translates that
/// into <see cref="InstellaExitCode.UsageInvalidArgs"/>.
/// </remarks>
internal readonly record struct PreviewCliArgs(
    bool PreviewFlagPresent,
    InstallerMode Mode,
    PreviewSpeed Speed,
    string? FailAtStep)
{
    /// <summary>Default preview configuration — install mode, normal speed, no failure injection.</summary>
    public static PreviewCliArgs Default => new(
        PreviewFlagPresent: false,
        Mode: InstallerMode.FirstInstall,
        Speed: PreviewSpeed.Normal,
        FailAtStep: null);
}

/// <summary>
/// Outcome of <c>PreviewCliArgs.Parse</c>. Either a populated
/// <see cref="Args"/> record or a human-readable <see cref="Error"/>.
/// </summary>
internal readonly record struct PreviewCliParseResult(PreviewCliArgs? Args, string? Error)
{
    public bool IsSuccess => Args is not null;

    public static PreviewCliParseResult Succeeded(PreviewCliArgs args) => new(args, null);

    public static PreviewCliParseResult Failed(string error) => new(null, error);
}

/// <summary>
/// Parser for the four <c>--preview*</c> CLI flags. Keeps the parsing
/// logic off the hot install-routing path so tests can pin parsing behavior
/// without spinning up a mode runner.
/// </summary>
internal static class PreviewCliArgsParser
{
    /// <summary>Parse preview flags out of <paramref name="args"/>.</summary>
    public static PreviewCliParseResult Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var flagPresent = false;
        var mode = InstallerMode.FirstInstall;
        var speed = PreviewSpeed.Normal;
        string? failAt = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg is null) continue;

            if (EqualsFlag(arg, "--preview"))
            {
                flagPresent = true;
                continue;
            }

            if (TryReadValue(args, ref i, "--preview-mode", out var modeRaw))
            {
                if (modeRaw is null)
                    return PreviewCliParseResult.Failed("--preview-mode requires a value (install|upgrade|repair|update|uninstall|manage|cleanup).");
                if (!TryParseMode(modeRaw, out mode))
                    return PreviewCliParseResult.Failed(
                        $"--preview-mode: unknown value '{modeRaw}'. Valid values: install, upgrade, repair, update, uninstall, manage, cleanup.");
                continue;
            }

            if (TryReadValue(args, ref i, "--preview-speed", out var speedRaw))
            {
                if (speedRaw is null)
                    return PreviewCliParseResult.Failed("--preview-speed requires a value (fast|normal|slow).");
                if (!TryParseSpeed(speedRaw, out speed))
                    return PreviewCliParseResult.Failed(
                        $"--preview-speed: unknown value '{speedRaw}'. Valid values: fast, normal, slow.");
                continue;
            }

            if (TryReadValue(args, ref i, "--preview-fail", out var failRaw))
            {
                if (string.IsNullOrWhiteSpace(failRaw))
                    return PreviewCliParseResult.Failed("--preview-fail requires a step name.");
                failAt = failRaw;
                continue;
            }
        }

        return PreviewCliParseResult.Succeeded(new PreviewCliArgs(flagPresent, mode, speed, failAt));
    }

    private static bool EqualsFlag(string arg, string name)
        => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Detect <paramref name="flagName"/> at <c>args[i]</c> in either two-token
    /// (<c>--flag value</c>) or single-token (<c>--flag=value</c>) form. On
    /// success advances <paramref name="i"/> past the consumed value (for the
    /// two-token form) and returns <c>true</c> with the captured value in
    /// <paramref name="value"/> (possibly <c>null</c> when the flag was at the
    /// tail of <paramref name="args"/> with no attached value).
    /// </summary>
    private static bool TryReadValue(string[] args, ref int i, string flagName, out string? value)
    {
        var arg = args[i];
        if (EqualsFlag(arg, flagName))
        {
            value = i + 1 < args.Length ? args[i + 1] : null;
            if (value is not null) i++;
            return true;
        }

        var prefix = flagName + "=";
        if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = arg[prefix.Length..];
            return true;
        }

        value = null;
        return false;
    }

    private static bool TryParseMode(string raw, out InstallerMode mode)
    {
        switch (raw.ToLowerInvariant())
        {
            case "install":
            case "firstinstall":
                mode = InstallerMode.FirstInstall;
                return true;
            case "upgrade":
                mode = InstallerMode.Upgrade;
                return true;
            case "repair":
                mode = InstallerMode.Repair;
                return true;
            case "update":
                mode = InstallerMode.Update;
                return true;
            case "uninstall":
                mode = InstallerMode.Uninstall;
                return true;
            case "manage":
                mode = InstallerMode.Manage;
                return true;
            case "cleanup":
                mode = InstallerMode.Cleanup;
                return true;
            default:
                mode = InstallerMode.FirstInstall;
                return false;
        }
    }

    private static bool TryParseSpeed(string raw, out PreviewSpeed speed)
    {
        switch (raw.ToLowerInvariant())
        {
            case "fast": speed = PreviewSpeed.Fast; return true;
            case "normal": speed = PreviewSpeed.Normal; return true;
            case "slow": speed = PreviewSpeed.Slow; return true;
            default: speed = PreviewSpeed.Normal; return false;
        }
    }

    /// <summary>Delay per simulated step for a given <see cref="PreviewSpeed"/> bucket.</summary>
    public static TimeSpan DelayFor(PreviewSpeed speed) => speed switch
    {
        PreviewSpeed.Fast => TimeSpan.FromMilliseconds(50),
        PreviewSpeed.Normal => TimeSpan.FromMilliseconds(300),
        PreviewSpeed.Slow => TimeSpan.FromSeconds(1),
        _ => TimeSpan.FromMilliseconds(300),
    };

    /// <summary>
    /// True when <paramref name="args"/> contains <c>--preview</c> in any
    /// positional slot. Used by the routing shim to check for opt-in
    /// before full parsing runs.
    /// </summary>
    public static bool ContainsPreviewFlag(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a is not null && EqualsFlag(a, "--preview")) return true;
        }
        return false;
    }
}

/// <summary>Known valid <c>--preview-mode</c> values (string form, lowercase).</summary>
internal static class PreviewModeNames
{
    public static readonly IReadOnlyList<string> All = new[]
    {
        "install", "upgrade", "repair", "update", "uninstall", "manage", "cleanup",
    };
}
