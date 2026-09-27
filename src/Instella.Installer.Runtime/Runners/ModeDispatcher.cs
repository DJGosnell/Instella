using System;
using System.Diagnostics;
using System.IO;
using Instella.Core.Installation;

namespace Instella.Installer.Runtime.Runners;

/// <summary>
/// Resolves the runtime <see cref="InstallerMode"/> for a given CLI invocation
/// and locates an installed-manifest path for the modes that need it. This is
/// the one place that turns <c>string[] args</c> into a dispatch decision —
/// all mode runners downstream treat the mode as an input, not something to
/// infer themselves.
/// </summary>
/// <remarks>
/// <para>Rules:</para>
/// <list type="bullet">
/// <item><c>--help</c> / <c>-h</c> / <c>-?</c> → <see cref="DispatchKind.Help"/>.</item>
/// <item><c>--update</c> → <see cref="InstallerMode.Update"/>.</item>
/// <item><c>--uninstall</c> → <see cref="InstallerMode.Uninstall"/>.</item>
/// <item><c>--manage</c> → <see cref="InstallerMode.Manage"/>.</item>
/// <item><c>--cleanup</c> → <see cref="InstallerMode.Cleanup"/>.</item>
/// <item><c>--recover</c> → <see cref="InstallerMode.Recover"/>.</item>
/// <item>
///   No mode flag: if a sibling <c>.instella-manifest.json</c> is present
///   (binary staged inside an install dir after a successful first install),
///   default to <see cref="InstallerMode.Manage"/>. Otherwise default to
///   <see cref="InstallerMode.FirstInstall"/>.
/// </item>
/// </list>
/// </remarks>
internal static class ModeDispatcher
{
    public const string ManifestFileName = ".instella-manifest.json";

    /// <summary>
    /// Parse <paramref name="args"/> and resolve the dispatch decision.
    /// <paramref name="siblingManifestProbe"/> overrides the
    /// "sibling manifest next to the running binary" default behavior; tests
    /// pass <c>false</c> to force the no-manifest path.
    /// </summary>
    public static DispatchResult Resolve(string[] args, bool? siblingManifestProbe = null)
    {
        ArgumentNullException.ThrowIfNull(args);

        // Everything after --extra-args belongs to the app the updater restarts: an app
        // argument such as --uninstall must never change this process's mode.
        var end = Array.IndexOf(args, "--extra-args");
        var own = end < 0 ? args : args[..end];

        // Help is unconditional and short-circuits any mode resolution.
        foreach (var a in own)
        {
            if (a is "--help" or "-h" or "-?" or "/?")
                return new DispatchResult(DispatchKind.Help, InstallerMode.FirstInstall, InstallPath: null);
        }

        string? explicitPath = null;
        var isSilent = false;
        var allowDowngrade = false;
        var force = false;
        var forceClose = false;
        var elevatedChild = false;
        string? scope = null;
        InstallerMode? explicitMode = null;

        for (var i = 0; i < own.Length; i++)
        {
            var a = own[i];
            switch (a)
            {
                case "--install":
                    explicitMode = InstallerMode.FirstInstall;
                    break;
                case "--update":
                    explicitMode = InstallerMode.Update;
                    break;
                case "--uninstall":
                    explicitMode = InstallerMode.Uninstall;
                    break;
                case "--manage":
                    explicitMode = InstallerMode.Manage;
                    break;
                case "--cleanup":
                    explicitMode = InstallerMode.Cleanup;
                    break;
                case "--recover":
                    explicitMode = InstallerMode.Recover;
                    break;
                case "--silent":
                    isSilent = true;
                    break;
                case "--allow-downgrade":
                    allowDowngrade = true;
                    break;
                case "--force":
                    force = true;
                    break;
                case "--force-close":
                    forceClose = true;
                    break;
                case "--elevated-child":
                    elevatedChild = true;
                    break;
                case "--scope":
                    if (i + 1 < own.Length) scope = own[++i];
                    break;
                case "--path":
                    if (i + 1 < own.Length) explicitPath = own[++i];
                    break;
                default:
                    if (a.StartsWith("--path=", StringComparison.Ordinal))
                        explicitPath = a["--path=".Length..];
                    else if (a.StartsWith("--scope=", StringComparison.Ordinal))
                        scope = a["--scope=".Length..];
                    break;
            }
        }

        // --path is used as a folder everywhere (manifest, uninstall string, target binding), so
        // it has one spelling: rooted against the current directory, no trailing separator.
        // A trailing separator would escape the closing quote on a command line ("D:\Apps\QN\").
        if (explicitPath is not null)
        {
            if (!InstallPaths.TryNormalize(explicitPath, requireRooted: false, out var normalized, out var problem))
                return new DispatchResult(DispatchKind.Invalid, explicitMode ?? InstallerMode.FirstInstall, null,
                    Problem: $"--path: {problem}");
            explicitPath = normalized;
        }

        if (explicitMode is { } m)
            return new DispatchResult(DispatchKind.Mode, m, explicitPath, IsSilent: isSilent,
                AllowDowngrade: allowDowngrade, Force: force, ForceClose: forceClose,
                ScopeArgument: scope, ElevatedChild: elevatedChild);

        // No explicit mode: probe for a sibling installed manifest. The
        // caller supplies the probe result in tests; in production it's
        // computed against the running binary's directory.
        var hasSibling = siblingManifestProbe ?? ProbeSiblingManifest();
        var defaultMode = hasSibling ? InstallerMode.Manage : InstallerMode.FirstInstall;
        return new DispatchResult(DispatchKind.Mode, defaultMode, explicitPath, IsSilent: isSilent,
            AllowDowngrade: allowDowngrade, Force: force, ForceClose: forceClose,
            ScopeArgument: scope, ElevatedChild: elevatedChild);
    }

    private static bool ProbeSiblingManifest()
    {
        try
        {
            var selfPath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(selfPath)) return false;
            var dir = Path.GetDirectoryName(selfPath);
            if (string.IsNullOrEmpty(dir)) return false;
            return File.Exists(Path.Combine(dir, ManifestFileName));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Whether <paramref name="stubDirectory"/> holds an installed manifest, read through
    /// <paramref name="fs"/>: injected services (the test harness) never probe the real disk.
    /// </summary>
    internal static bool HasSiblingManifest(string? stubDirectory, Instella.Core.FileSystem.IFileSystem fs)
    {
        try
        {
            return !string.IsNullOrEmpty(stubDirectory) && fs.Exists(Path.Combine(stubDirectory, ManifestFileName));
        }
        catch
        {
            return false;
        }
    }
}

internal enum DispatchKind
{
    Mode,
    Help,
    /// <summary>The command line cannot be used (<see cref="DispatchResult.Problem"/>): exit 40.</summary>
    Invalid,
}

/// <param name="Kind">Run a mode, or print help.</param>
/// <param name="Mode">The mode to run.</param>
/// <param name="InstallPath"><c>--path</c>, when given.</param>
/// <param name="IsSilent"><c>--silent</c>.</param>
/// <param name="AllowDowngrade"><c>--allow-downgrade</c>: an older installer may replace a newer installation.</param>
/// <param name="Force"><c>--force</c>: install into a non-empty directory that holds no Instella installation.</param>
/// <param name="ForceClose"><c>--force-close</c>: close programs using the app's files without asking.</param>
/// <param name="Cli">Declared flags parsed once by <c>InstellaInstallerImpl.RunAsync</c>; null means none.</param>
/// <param name="ScopeArgument"><c>--scope user|machine</c> as given.</param>
/// <param name="ElevatedChild"><c>--elevated-child</c>: this process is the UAC relaunch; never relaunch again.</param>
/// <param name="Scope">The resolved install scope; null lets the runner use its default.</param>
/// <param name="Problem">Why the command line is <see cref="DispatchKind.Invalid"/>.</param>
/// <param name="Existing">The installation of this app the install targets (upgrade or repair in place).</param>
/// <param name="Installations">Several installations found and none chosen yet: the scope page chooses.</param>
internal readonly record struct DispatchResult(
    DispatchKind Kind,
    InstallerMode Mode,
    string? InstallPath,
    bool IsSilent = false,
    bool AllowDowngrade = false,
    bool Force = false,
    bool ForceClose = false,
    Builders.CliArgs? Cli = null,
    string? ScopeArgument = null,
    bool ElevatedChild = false,
    InstallationScope? Scope = null,
    string? Problem = null,
    ExistingInstallation? Existing = null,
    System.Collections.Generic.IReadOnlyList<ExistingInstallation>? Installations = null)
{
    /// <summary><see cref="Cli"/>, or no flags.</summary>
    public Builders.CliArgs CliOrEmpty => Cli ?? Builders.CliArgs.Empty;
}
