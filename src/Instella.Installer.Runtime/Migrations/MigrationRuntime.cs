using System;
using System.Collections.Generic;
using System.IO;
using Instella.Core.Installation;
using Instella.Installer.Runtime.Core.Processes;

namespace Instella.Installer.Runtime.Migrations;

/// <summary>
/// Per-run migration state, held on <c>InstallContext.Migrations</c>: the seams every condition
/// and action reaches the machine through (so each can be faked), and what the run produced.
/// </summary>
internal sealed class MigrationRuntime
{
    /// <summary>Resolves <see cref="KnownFolder"/>s.</summary>
    public KnownFolderResolver Folders { get; init; } = KnownFolderResolver.Host;

    /// <summary>Finds processes holding files in a folder (Restart Manager on Windows).</summary>
    public ILockingProcessFinder ProcessFinder { get; init; } = DefaultLockingProcessFinder.Instance;

    /// <summary>Closes a process; null asks it to close through the platform, then ends it.</summary>
    public IProcessCloser? ProcessCloser { get; init; }

    /// <summary>Asks the user about programs to close; null (silent) closes them only with <see cref="ForceClose"/>.</summary>
    public AppRunningPrompt? Prompt { get; init; }

    /// <summary><c>--force-close</c>: close programs without asking.</summary>
    public bool ForceClose { get; init; }

    /// <summary>Starts programs for <c>RunProgramAsync</c>.</summary>
    public IProgramRunner Programs { get; init; } = ProcessProgramRunner.Instance;

    /// <summary>Actions report what they would do and change nothing.</summary>
    public bool IsPreview { get; init; }

    /// <summary>Where <see cref="MigrationTiming.BeforeCommit"/> deletions keep files until the install completes.</summary>
    public string UndoDirectory { get; init; } =
        Path.Combine(Path.GetTempPath(), "Instella", "migration-undo", Guid.NewGuid().ToString("N"));

    /// <summary>Ids of the run-once migrations that succeeded in this run.</summary>
    public List<string> Completed { get; } = [];

    /// <summary>Items adopted in this run.</summary>
    public List<ManifestAdoptedItem> Adopted { get; } = [];

    /// <summary>One record per migration considered, in the order they ran.</summary>
    public List<MigrationRunRecord> Records { get; } = [];
}

/// <summary>What happened to one migration in one run.</summary>
internal enum MigrationRunOutcome
{
    Completed,
    Skipped,
    AlreadyCompleted,
    Failed,
    RolledBack,
}

/// <summary>The outcome of one migration, with the reason (skip or error) and the actions it took.</summary>
internal sealed record MigrationRunRecord(string Id, MigrationRunOutcome Outcome, string? Reason, IReadOnlyList<MigrationAction> Actions);

/// <summary>
/// One thing an action did (or, in preview, would do). <see cref="Kind"/> is one of
/// <c>stop-process</c>, <c>delete-file</c>, <c>delete-folder</c>, <c>run-value-repoint</c>,
/// <c>run-value-delete</c>, <c>run-value-adopt</c>, <c>run-program</c>.
/// </summary>
internal sealed record MigrationAction(string Kind, string Target, string? Detail = null, bool Preview = false)
{
    public override string ToString() =>
        (Preview ? "would " : "") + Kind + " " + Target + (Detail is null ? "" : $" ({Detail})");
}
