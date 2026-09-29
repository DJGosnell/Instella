using System.Globalization;
using Instella.Core.Utilities;

namespace Instella.Core.Installation;

/// <summary>
/// The launch contract between Instella and an app upgrade program: the arguments it is started
/// with, the progress lines it may print, and the exit codes it returns. Frozen per
/// <see cref="CurrentVersion"/> (docs/compatibility.md). The Runtime builds the arguments and reads
/// the output; the SDK parses the arguments and writes the output.
/// </summary>
internal static class AppUpgradeContract
{
    /// <summary>The highest contract version this build speaks.</summary>
    public const int CurrentVersion = 1;

    /// <summary>The declaration, at the root of the app's files.</summary>
    public const string DeclarationFileName = "instella-upgrade.json";

    /// <summary>First argument of an upgrade run (install, repair, downgrade, update).</summary>
    public const string UpgradeSwitch = "--instella-upgrade";

    /// <summary>First argument of an uninstall run.</summary>
    public const string UninstallSwitch = "--instella-uninstall";

    /// <summary>The contract version the arguments follow.</summary>
    public const string ContractArg = "--contract";
    /// <summary>The operation (<see cref="ModeName"/>).</summary>
    public const string ModeArg = "--mode";
    /// <summary>The installed version before the operation, or <see cref="NoVersion"/>.</summary>
    public const string FromArg = "--from";
    /// <summary>The version after the operation, or <see cref="NoVersion"/>.</summary>
    public const string ToArg = "--to";
    /// <summary><c>user</c> or <c>machine</c>.</summary>
    public const string ScopeArg = "--scope";
    /// <summary>The install folder.</summary>
    public const string InstallPathArg = "--install-path";
    /// <summary>The application id.</summary>
    public const string AppIdArg = "--app-id";

    /// <summary>The value of <see cref="FromArg"/> / <see cref="ToArg"/> when there is no version.</summary>
    public const string NoVersion = "none";

    /// <summary>Scope values.</summary>
    public const string UserScope = "user", MachineScope = "machine";

    /// <summary>Prefix of an instruction line on stdout.</summary>
    public const string InstructionPrefix = "##instella";

    /// <summary>The progress instruction: <c>##instella progress &lt;0-100&gt; [text]</c>.</summary>
    public const string ProgressVerb = "progress";

    /// <summary>Set in the program's environment to the contract version.</summary>
    public const string EnvironmentVariable = "INSTELLA_UPGRADE_CONTRACT";

    /// <summary>Success.</summary>
    public const int ExitSuccess = 0;
    /// <summary>The upgrade failed (and left the data usable by the old version).</summary>
    public const int ExitFailed = 1;
    /// <summary>The app refused the change, for example a downgrade it does not support.</summary>
    public const int ExitRefused = 2;
    /// <summary>The program did not understand how it was started (not a contract call, or a newer contract).</summary>
    public const int ExitNotUnderstood = 3;

    /// <summary>The time limit when the declaration sets none.</summary>
    public const int DefaultTimeoutMinutes = 30;

    /// <summary>The highest time limit a declaration may set (one day).</summary>
    public const int MaxTimeoutMinutes = 1440;

    /// <summary>The <c>--mode</c> value of <paramref name="mode"/>.</summary>
    public static string ModeName(AppUpgradeLaunchMode mode) => mode switch
    {
        AppUpgradeLaunchMode.FirstInstall => "first-install",
        AppUpgradeLaunchMode.Upgrade => "upgrade",
        AppUpgradeLaunchMode.Repair => "repair",
        AppUpgradeLaunchMode.Downgrade => "downgrade",
        AppUpgradeLaunchMode.Update => "update",
        AppUpgradeLaunchMode.Uninstall => "uninstall",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };

    /// <summary>Parses a <c>--mode</c> value (exact, lower case).</summary>
    public static bool TryParseMode(string? text, out AppUpgradeLaunchMode mode)
    {
        foreach (var candidate in Enum.GetValues<AppUpgradeLaunchMode>())
        {
            if (string.Equals(ModeName(candidate), text, StringComparison.Ordinal))
            {
                mode = candidate;
                return true;
            }
        }
        mode = default;
        return false;
    }

    /// <summary>
    /// The command line for <paramref name="launch"/>: the switch, then <c>--contract</c>,
    /// <c>--mode</c>, <c>--from</c>, <c>--to</c>, <c>--scope</c>, <c>--install-path</c>,
    /// <c>--app-id</c>, in that order, then <paramref name="extra"/> (the declaration's arguments).
    /// </summary>
    public static IReadOnlyList<string> BuildArguments(AppUpgradeLaunch launch, IReadOnlyList<string> extra)
    {
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentNullException.ThrowIfNull(extra);
        var args = new List<string>(15 + extra.Count)
        {
            launch.Mode == AppUpgradeLaunchMode.Uninstall ? UninstallSwitch : UpgradeSwitch,
            ContractArg, launch.ContractVersion.ToString(CultureInfo.InvariantCulture),
            ModeArg, ModeName(launch.Mode),
            FromArg, VersionText(launch.From),
            ToArg, VersionText(launch.To),
            ScopeArg, launch.Scope == InstallationScope.SystemWide ? MachineScope : UserScope,
            InstallPathArg, launch.InstallPath,
            AppIdArg, launch.AppId,
        };
        args.AddRange(extra);
        return args;
    }

    /// <summary>The canonical text of <paramref name="version"/>, or <see cref="NoVersion"/>.</summary>
    public static string VersionText(Version? version) =>
        version is null ? NoVersion : AppVersions.ToCanonicalString(version);

    /// <summary>
    /// Reads one stdout line. Returns true for a well-formed progress instruction. Returns false
    /// with a null <paramref name="problem"/> for an ordinary line, and false with a problem for an
    /// instruction line that is malformed or unknown: it is logged, never fatal. The percent is
    /// clamped to 0-100, and a decimal is rounded.
    /// </summary>
    public static bool TryParseInstruction(string? line, out AppUpgradeProgressLine progress, out string? problem)
    {
        progress = default;
        problem = null;
        if (line is null || !line.StartsWith(InstructionPrefix, StringComparison.Ordinal))
            return false;

        var rest = line.AsSpan(InstructionPrefix.Length);
        if (rest.Length > 0 && rest[0] != ' ')
            return false;                       // "##instellax": an ordinary line
        rest = rest.TrimStart(' ');
        if (rest.IsEmpty)
        {
            problem = "an instruction line without an instruction";
            return false;
        }

        var verbEnd = rest.IndexOf(' ');
        var verb = verbEnd < 0 ? rest : rest[..verbEnd];
        if (!verb.SequenceEqual(ProgressVerb))
        {
            problem = $"unknown instruction '{verb.ToString()}'";
            return false;
        }

        rest = verbEnd < 0 ? ReadOnlySpan<char>.Empty : rest[(verbEnd + 1)..].TrimStart(' ');
        var numberEnd = rest.IndexOf(' ');
        var number = numberEnd < 0 ? rest : rest[..numberEnd];
        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || double.IsNaN(value) || double.IsInfinity(value))
        {
            problem = $"progress needs a percentage from 0 to 100, not '{number.ToString()}'";
            return false;
        }

        var text = numberEnd < 0 ? null : rest[(numberEnd + 1)..].Trim().ToString();
        var percent = (int)Math.Round(Math.Clamp(value, 0, 100), MidpointRounding.AwayFromZero);
        progress = new AppUpgradeProgressLine(percent, string.IsNullOrEmpty(text) ? null : text);
        return true;
    }

    /// <summary>The progress instruction line for <paramref name="percent"/> and <paramref name="text"/> (line breaks become spaces).</summary>
    public static string FormatProgress(int percent, string? text)
    {
        var p = Math.Clamp(percent, 0, 100).ToString(CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(text)) return $"{InstructionPrefix} {ProgressVerb} {p}";
        var oneLine = text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return $"{InstructionPrefix} {ProgressVerb} {p} {oneLine}";
    }
}

/// <summary>The operation an upgrade program is started for (<c>--mode</c>).</summary>
internal enum AppUpgradeLaunchMode
{
    /// <summary><c>first-install</c>: nothing was installed before.</summary>
    FirstInstall,
    /// <summary><c>upgrade</c>: an installer replaced an older version.</summary>
    Upgrade,
    /// <summary><c>repair</c>: the same version was reinstalled (installer or updater).</summary>
    Repair,
    /// <summary><c>downgrade</c>: an installer replaced a newer version (<c>--allow-downgrade</c>).</summary>
    Downgrade,
    /// <summary><c>update</c>: the in-app updater applied a newer release.</summary>
    Update,
    /// <summary><c>uninstall</c>: the app is being removed (<c>--instella-uninstall</c>).</summary>
    Uninstall,
}

/// <summary>Everything the contract passes to one run of an upgrade program.</summary>
/// <param name="Mode">The operation.</param>
/// <param name="From">The version installed before it; null on a first install.</param>
/// <param name="To">The version after it; null on uninstall.</param>
/// <param name="Scope">Per-user or machine-wide.</param>
/// <param name="InstallPath">The install folder (also the working directory).</param>
/// <param name="AppId">The application id.</param>
/// <param name="ContractVersion">The contract version the declaration asked for.</param>
internal sealed record AppUpgradeLaunch(
    AppUpgradeLaunchMode Mode, Version? From, Version? To, InstallationScope Scope, string InstallPath, string AppId,
    int ContractVersion = AppUpgradeContract.CurrentVersion);

/// <summary>One parsed <c>##instella progress</c> line.</summary>
/// <param name="Percent">0 to 100.</param>
/// <param name="Text">The status text, if any.</param>
internal readonly record struct AppUpgradeProgressLine(int Percent, string? Text);
