using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Installation;
using Instella.Core.Logging;
using Instella.Core.Platform;

namespace Instella.Installer.Runtime.Migrations;

/// <summary>
/// Installer code that runs once, under a condition, to deal with what came before this version:
/// a copy of the app installed without Instella, settings an older version kept elsewhere, a
/// leftover an older version created. Derive one class per migration and register it with
/// <c>InstallerBuilder.AddMigration&lt;T&gt;()</c>.
/// </summary>
/// <remarks>
/// <para><see cref="When"/> returns a <see cref="Condition"/> built from the protected condition
/// methods; <see cref="ExecuteAsync"/> uses the protected actions, which enforce the safety
/// rules (known folders only; never the install folder, a protected folder, or another Instella
/// installation; named files only; processes only from the folder). Direct use of
/// <see cref="MigrationContext.FileSystem"/> or <see cref="MigrationContext.PlatformServices"/>
/// bypasses those rules.</para>
/// <para>A run-once migration that succeeds is recorded in the installed manifest by its
/// <see cref="Id"/> and never runs again for that installation, so an id is permanent once shipped.
/// Migrations run only when an installer runs (install, upgrade, repair, uninstall), never during
/// an in-app update; an installation that updated past a migration runs it the next time an
/// installer that contains it runs.</para>
/// </remarks>
public abstract class InstallMigration
{
    private MigrationRun? _run;

    /// <summary>
    /// The permanent id, recorded once the migration succeeds: 1–64 characters, lowercase letters,
    /// digits, <c>.</c>, <c>_</c> and <c>-</c>, starting and ending with a letter or digit. Never
    /// reuse or rename an id that has shipped.
    /// </summary>
    public abstract string Id { get; }

    /// <summary>The text the wizard's Progress page shows while it runs. Defaults to <see cref="Id"/>.</summary>
    public virtual string DisplayName => Id;

    /// <summary>When it runs. Defaults to <see cref="MigrationTiming.AfterCommit"/> (best effort, after everything else).</summary>
    public virtual MigrationTiming Timing => MigrationTiming.AfterCommit;

    /// <summary>Order among migrations of the same <see cref="Timing"/>, ascending; ties are broken by <see cref="Id"/>. Defaults to 0.</summary>
    public virtual int Order => 0;

    /// <summary>
    /// Whether a success is recorded so the migration never runs again for the installation.
    /// Defaults to true, and must be false for <see cref="MigrationTiming.Uninstall"/>.
    /// </summary>
    public virtual bool RunOnce => Timing != MigrationTiming.Uninstall;

    /// <summary>
    /// The condition under which it runs. Only compose conditions here: it is also called by
    /// <c>Build()</c>, where <see cref="Context"/> is not available.
    /// </summary>
    protected abstract Condition When();

    /// <summary>The migration's work. Throwing fails the migration.</summary>
    protected abstract Task ExecuteAsync(CancellationToken ct);

    /// <summary>
    /// <see cref="MigrationTiming.BeforeCommit"/> only: undo what <see cref="ExecuteAsync"/> did with
    /// custom code, when it failed or a later step failed the install. The built-in actions undo
    /// themselves afterwards. Must tolerate a partial <see cref="ExecuteAsync"/>.
    /// </summary>
    protected virtual Task RollbackAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>Facts about the current run. Throws outside a run (for example in <see cref="When"/>).</summary>
    protected MigrationContext Context => Bound.Context;

    /// <summary>The log, with every line prefixed <c>migration[&lt;id&gt;]</c>. Throws outside a run.</summary>
    protected IInstellaLogger Log => Bound.Context.Log;

    // ---- Conditions --------------------------------------------------------------------------

    /// <summary>The installer is installing into an empty folder.</summary>
    protected Condition IsFirstInstall() => new ModeCondition(ModeSet.FirstInstall, "IsFirstInstall()");

    /// <summary>The installer is replacing another version (a newer one, or an older one with <c>--allow-downgrade</c>).</summary>
    protected Condition IsUpgrade() => new ModeCondition(ModeSet.Upgrade, "IsUpgrade()");

    /// <summary>A first install or an upgrade.</summary>
    protected Condition IsFirstInstallOrUpgrade() => new ModeCondition(ModeSet.FirstInstall | ModeSet.Upgrade, "IsFirstInstallOrUpgrade()");

    /// <summary>The installer is reinstalling the version already installed.</summary>
    protected Condition IsRepair() => new ModeCondition(ModeSet.Repair, "IsRepair()");

    /// <summary>The app is being uninstalled (<see cref="MigrationTiming.Uninstall"/> migrations).</summary>
    protected Condition IsUninstall() => new ModeCondition(ModeSet.Uninstall, "IsUninstall()");

    /// <summary>
    /// An upgrade from a version in <paramref name="range"/>: comparators separated by spaces, all
    /// of which must hold (<c>&lt;</c>, <c>&lt;=</c>, <c>&gt;</c>, <c>&gt;=</c>, <c>=</c> or a bare
    /// version; <c>*</c> for any), compared canonically. Example: <c>"&gt;=1.0 &lt;2.0"</c>. After
    /// in-app updates the previous version is the updated one; prefer conditions on state.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="range"/> is not valid.</exception>
    protected Condition UpgradingFrom(string range) => new VersionRangeCondition(VersionRange.Parse(range));

    /// <summary>The file <paramref name="relative"/> under <paramref name="root"/> exists.</summary>
    protected Condition FileExists(KnownFolder root, string relative) =>
        new PathCondition(root, MigrationPaths.NormalizeRelative(relative, nameof(relative)), PathTest.FileExists);

    /// <summary>The folder <paramref name="relative"/> under <paramref name="root"/> exists.</summary>
    protected Condition FolderExists(KnownFolder root, string relative) =>
        new PathCondition(root, MigrationPaths.NormalizeRelative(relative, nameof(relative)), PathTest.FolderExists);

    /// <summary>The folder <paramref name="relative"/> under <paramref name="root"/> holds an Instella installation (<c>.instella-manifest.json</c>).</summary>
    protected Condition InstellaInstallationAt(KnownFolder root, string relative) =>
        new PathCondition(root, MigrationPaths.NormalizeRelative(relative, nameof(relative)), PathTest.InstellaInstallation);

    /// <summary>The Run value <paramref name="name"/> exists in the scope's Run key (HKCU per-user, HKLM machine-wide).</summary>
    protected Condition RunValueExists(string name) => new RunValueCondition(RequireName(name), null);

    /// <summary>The Run value <paramref name="name"/> starts a program inside <paramref name="folder"/>.</summary>
    protected Condition RunValuePointsInto(string name, MigrationFolder folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        return new RunValueCondition(RequireName(name), folder);
    }

    /// <summary>
    /// The registry value exists. <see cref="RegistryHive.CurrentUser"/> is false in a machine-wide
    /// install (another account's HKCU); <see cref="RegistryHive.AutoFromScope"/> follows the scope.
    /// </summary>
    protected Condition RegistryValueExists(RegistryHive hive, string keyPath, string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(keyPath);
        ArgumentNullException.ThrowIfNull(name);
        return new RegistryValueCondition(hive, keyPath, name);
    }

    /// <summary>A program (other than Explorer, a service or a critical process) has files in <paramref name="folder"/> open.</summary>
    protected Condition ProcessRunningIn(MigrationFolder folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        return new ProcessCondition(folder);
    }

    /// <summary>The installer runs on Windows.</summary>
    protected Condition IsWindows() => new PlatformCondition(TargetPlatform.Windows);

    /// <summary>A per-user install.</summary>
    protected Condition IsPerUserInstall() => new ScopeCondition(InstallationScope.PerUser);

    /// <summary>A machine-wide install.</summary>
    protected Condition IsMachineInstall() => new ScopeCondition(InstallationScope.SystemWide);

    // ---- Folders and actions -----------------------------------------------------------------

    /// <summary>
    /// A folder to act on: <paramref name="relative"/> under <paramref name="root"/>. The path is
    /// validated now (relative, no <c>..</c>, no wildcards); the safety rules are checked each time
    /// an action uses it.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="relative"/> is not a safe relative path.</exception>
    protected MigrationFolder Folder(KnownFolder root, string relative) => new(root, relative);

    /// <summary>
    /// Closes the programs that have files in <paramref name="folder"/> open: interactive installs
    /// ask first, silent ones close them only with <c>--force-close</c>. Explorer, services and
    /// critical processes are never closed. Fails when a program is still running afterwards.
    /// </summary>
    protected Task StopProcessesInAsync(MigrationFolder folder, CancellationToken ct) =>
        MigrationActions.StopProcessesInAsync(Bound, folder, ct);

    /// <summary>
    /// When the Run value <paramref name="name"/> starts a program in <paramref name="from"/>, points
    /// it at this installation's executable, keeping its arguments. Returns whether it changed.
    /// </summary>
    protected Task<bool> RepointRunValueAsync(string name, MigrationFolder from, CancellationToken ct) =>
        MigrationActions.RepointRunValueAsync(Bound, RequireName(name), from, ct);

    /// <summary>Deletes the Run value <paramref name="name"/> when it starts a program in <paramref name="pointingInto"/>. Returns whether it was deleted.</summary>
    protected Task<bool> DeleteRunValueAsync(string name, MigrationFolder pointingInto, CancellationToken ct) =>
        MigrationActions.DeleteRunValueAsync(Bound, RequireName(name), pointingInto, ct);

    /// <summary>
    /// Records the Run value <paramref name="name"/> in the installed manifest so uninstall removes
    /// it, if it then points into the installation. Returns whether the value exists (and was adopted).
    /// </summary>
    protected Task<bool> AdoptRunValueAsync(string name, CancellationToken ct) =>
        MigrationActions.AdoptRunValueAsync(Bound, RequireName(name), ct);

    /// <summary>
    /// Deletes the named files (relative paths, no wildcards) from <paramref name="folder"/>.
    /// Missing files are skipped. Returns how many were deleted; fails when one cannot be.
    /// </summary>
    protected Task<int> DeleteFilesAsync(MigrationFolder folder, IReadOnlyList<string> fileNames, CancellationToken ct) =>
        MigrationActions.DeleteFilesAsync(Bound, folder, fileNames, ct);

    /// <summary>Deletes <paramref name="folder"/> if it holds nothing. Returns whether it was deleted.</summary>
    protected Task<bool> DeleteFolderIfEmptyAsync(MigrationFolder folder, CancellationToken ct) =>
        MigrationActions.DeleteFolderIfEmptyAsync(Bound, folder, ct);

    /// <summary>
    /// Runs a program (for example an old uninstaller) without a shell and waits up to 10 minutes.
    /// <paramref name="exePath"/> must be a full path to an existing file. Fails when the exit code
    /// is not in <paramref name="successExitCodes"/> (empty means 0). Returns the exit code.
    /// </summary>
    protected Task<int> RunProgramAsync(string exePath, IReadOnlyList<string> arguments, IReadOnlyList<int> successExitCodes, CancellationToken ct) =>
        MigrationActions.RunProgramAsync(Bound, exePath, arguments, successExitCodes, ct);

    // ---- Engine ------------------------------------------------------------------------------

    internal Condition GetCondition() => When() ?? throw new InvalidOperationException($"migration '{Id}': When() returned null");

    internal Task RunBodyAsync(CancellationToken ct) => ExecuteAsync(ct);

    internal Task RunRollbackAsync(CancellationToken ct) => RollbackAsync(ct);

    internal void Bind(MigrationRun run) => _run = run;

    internal void Unbind() => _run = null;

    private MigrationRun Bound => _run ?? throw new InvalidOperationException(
        $"migration '{Id}': Context, Log and the actions are only available while the migration runs (not in When() or a constructor)");

    private static string RequireName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Contains('\\'))
            throw new ArgumentException($"'{name}' is not a value name", nameof(name));
        return name;
    }
}

/// <summary>One migration's run: its context, what its actions did, and how to undo them.</summary>
internal sealed class MigrationRun(InstallMigration migration, MigrationContext context)
{
    public InstallMigration Migration { get; } = migration;

    public MigrationContext Context { get; } = context;

    /// <summary>BeforeCommit migrations keep an undo journal.</summary>
    public bool Journaled => Migration.Timing == MigrationTiming.BeforeCommit;

    public List<MigrationAction> Actions { get; } = [];

    /// <summary>Undo entries, applied newest first.</summary>
    public List<UndoStep> UndoJournal { get; } = [];

    /// <summary>When this migration first journaled a change (written into <c>undo.json</c>).</summary>
    public DateTime? JournalCreatedAt { get; set; }
}
