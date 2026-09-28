using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.FileSystem;
using Instella.Core.Installation;
using Instella.Core.Platform;
using Instella.Installer.Runtime.Core.Processes;
using Instella.Installer.Runtime.Installation.BuiltIn;

namespace Instella.Installer.Runtime.Migrations;

/// <summary>
/// The migration actions. Each resolves its folders through <see cref="MigrationFolderGuard"/>
/// (refusing with <see cref="MigrationActionException"/>), reaches the machine only through the
/// run's seams, records what it did, reports instead of acting in preview, treats anything it
/// cannot read as a failure (never as "absent"), and — for <see cref="MigrationTiming.BeforeCommit"/>
/// migrations — journals how to undo each change on disk before making it (<see cref="MigrationUndo"/>).
/// </summary>
internal static class MigrationActions
{
    /// <summary>The longest <c>RunProgramAsync</c> waits.</summary>
    internal static readonly TimeSpan ProgramTimeout = TimeSpan.FromMinutes(10);

    public static async Task StopProcessesInAsync(MigrationRun run, MigrationFolder folder, CancellationToken ct)
    {
        const string action = "StopProcessesIn";
        ArgumentNullException.ThrowIfNull(folder);
        var path = ResolveForAction(run, folder, action);
        var ctx = run.Context;
        var rt = ctx.Runtime;
        // Links are not followed: only programs of this folder, never of whatever a link points to.
        var gate = new RunningAppGate(ctx.PlatformServices, ctx.Log, rt.ProcessFinder, ctx.FileSystem, rt.ProcessCloser) { SkipLinks = true };

        var running = gate.FindBlockers(path, executableName: null, out var error).Where(p => p.CanClose).ToList();
        if (error is not null)
            throw new MigrationActionException(action, $"the programs using {folder} could not be found ({error})");
        if (rt.IsPreview)
        {
            foreach (var p in running)
                Record(run, new MigrationAction("stop-process", p.ToString(), folder.ToString(), Preview: true));
            return;
        }

        var closed = await gate.EnsureClosedAsync(ctx.AppName, path, executableName: null, rt.ForceClose, rt.Prompt, ct);
        foreach (var p in gate.Closed)
            Record(run, new MigrationAction("stop-process", p.ToString(), folder.ToString()));
        if (!closed)
            throw new MigrationActionException(action, $"programs in {folder} are still running" +
                (rt.ForceClose || rt.Prompt is not null ? "" : "; a silent install closes them only with --force-close"));
    }

    public static async Task<bool> RepointRunValueAsync(MigrationRun run, string name, MigrationFolder from, CancellationToken ct)
    {
        const string action = "RepointRunValue";
        ArgumentNullException.ThrowIfNull(from);
        var path = ResolveForAction(run, from, action);
        var ctx = run.Context;
        var data = await ReadRunValueAsync(ctx, name, action, ct);
        var command = RunValues.AsCommand(data);
        if (command is null || !RunCommand.TryGetExecutable(command, out var exe, out var arguments)
            || !Core.PathGuards.IsSameOrInside(exe, path))
        {
            ctx.Log.Info(command is null
                ? $"Run value '{name}' does not exist; nothing to repoint"
                : $"Run value '{name}' runs '{command}', not a program in {from}; left unchanged");
            return false;
        }

        var target = RunCommand.Format(ExecutableResolver.Resolve(ctx.Install), arguments);
        var record = new MigrationAction("run-value-repoint", name, $"{command} -> {target}", ctx.IsPreview);
        if (ctx.IsPreview)
        {
            Record(run, record);
            return true;
        }

        var journaled = await JournalAsync(run, $"restore Run value '{name}'", RunValueEntry(ctx, name, data!), action, ct);
        var written = await ctx.PlatformServices.WriteRegistryValueAsync(
            RunValues.Hive(ctx.PerUser), RunCommand.RunKey, name, InstellaRegistryValueKind.String, target, ctx.PerUser, ct);
        if (!written.Success)
        {
            if (journaled) await MigrationUndo.DropLastAsync(run, ct);
            throw new MigrationActionException(action, $"could not write Run value '{name}': {written.Error}");
        }
        Record(run, record);
        return true;
    }

    public static async Task<bool> DeleteRunValueAsync(MigrationRun run, string name, MigrationFolder pointingInto, CancellationToken ct)
    {
        const string action = "DeleteRunValue";
        ArgumentNullException.ThrowIfNull(pointingInto);
        var path = ResolveForAction(run, pointingInto, action);
        var ctx = run.Context;
        var data = await ReadRunValueAsync(ctx, name, action, ct);
        var command = RunValues.AsCommand(data);
        if (command is null || !RunValues.PointsInto(command, path))
        {
            ctx.Log.Info(command is null
                ? $"Run value '{name}' does not exist; nothing to delete"
                : $"Run value '{name}' runs '{command}', not a program in {pointingInto}; left in place");
            return false;
        }

        var record = new MigrationAction("run-value-delete", name, command, ctx.IsPreview);
        if (ctx.IsPreview)
        {
            Record(run, record);
            return true;
        }

        var journaled = await JournalAsync(run, $"restore Run value '{name}'", RunValueEntry(ctx, name, data!), action, ct);
        var deleted = await ctx.PlatformServices.DeleteRegistryValueAsync(
            RunValues.Hive(ctx.PerUser), RunCommand.RunKey, name, ctx.PerUser, ct);
        if (!deleted.Success)
        {
            if (journaled) await MigrationUndo.DropLastAsync(run, ct);
            throw new MigrationActionException(action, $"could not delete Run value '{name}': {deleted.Error}");
        }
        Record(run, record);
        return true;
    }

    public static async Task<bool> AdoptRunValueAsync(MigrationRun run, string name, CancellationToken ct)
    {
        const string action = "AdoptRunValue";
        var ctx = run.Context;
        var data = await ReadRunValueAsync(ctx, name, action, ct);
        if (data is null)
        {
            ctx.Log.Info($"Run value '{name}' does not exist; nothing to adopt");
            return false;
        }

        var item = new ManifestAdoptedItem(ManifestAdoptedItem.RunValue, name, ctx.PerUser, run.Migration.Id);
        Record(run, new MigrationAction("run-value-adopt", name, null, ctx.IsPreview));
        if (ctx.IsPreview) return true;

        var adopted = ctx.Runtime.Adopted;
        if (!adopted.Any(a => SameItem(a, item)))
        {
            adopted.Add(item);
            // Nothing on the machine changed, so nothing is written to the journal.
            if (run.Journaled)
                run.UndoJournal.Add(new UndoStep($"forget adopted Run value '{name}'", null, _ =>
                {
                    adopted.RemoveAll(a => SameItem(a, item));
                    return Task.CompletedTask;
                }));
        }
        return true;
    }

    public static async Task<int> DeleteFilesAsync(MigrationRun run, MigrationFolder folder, IReadOnlyList<string> fileNames, CancellationToken ct)
    {
        const string action = "DeleteFiles";
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(fileNames);
        // Every name is checked before anything is deleted.
        var names = new List<string>(fileNames.Count);
        foreach (var name in fileNames)
        {
            try
            {
                names.Add(MigrationPaths.NormalizeRelative(name, nameof(fileNames)));
            }
            catch (ArgumentException ex)
            {
                throw new MigrationActionException(action, ex.Message);
            }
        }

        var path = ResolveForAction(run, folder, action);
        var ctx = run.Context;
        var fs = ctx.FileSystem;
        foreach (var name in names)
        {
            // A link on the way (sub/ being a junction) would lead outside the folder.
            if (MigrationFolderGuard.LinkBetween(fs, path, SafePath.Combine(path, name)) is { } link)
                throw new MigrationActionException(action, $"refused: '{name}' goes through the link '{link}', which may lead outside {folder}");
        }

        var count = 0;
        foreach (var name in names)
        {
            ct.ThrowIfCancellationRequested();
            var file = SafePath.Combine(path, name);
            switch (fs.GetEntryState(file))
            {
                case FileSystemEntryState.Missing:
                    continue;
                case FileSystemEntryState.Denied:
                    throw new MigrationActionException(action, $"could not delete '{file}': it cannot be read (access denied)");
                case FileSystemEntryState.Directory:
                    throw new MigrationActionException(action, $"refused: '{file}' is a folder; DeleteFilesAsync deletes files only");
            }
            var record = new MigrationAction("delete-file", file, null, ctx.IsPreview);
            if (!ctx.IsPreview)
            {
                if (run.Journaled)
                    await MoveToUndoAsync(run, file, action, ct);
                else if ((await fs.DeleteFileAsync(file, ct)) is { Success: false } failed)
                    throw new MigrationActionException(action, $"could not delete '{file}': {failed.Error?.Message ?? "unknown error"}");
            }
            Record(run, record);
            count++;
        }
        return count;
    }

    public static async Task<bool> DeleteFolderIfEmptyAsync(MigrationRun run, MigrationFolder folder, CancellationToken ct)
    {
        const string action = "DeleteFolderIfEmpty";
        ArgumentNullException.ThrowIfNull(folder);
        var path = ResolveForAction(run, folder, action);
        var ctx = run.Context;
        var fs = ctx.FileSystem;
        switch (fs.GetEntryState(path))
        {
            case FileSystemEntryState.Missing:
            case FileSystemEntryState.File:
                return false;
            case FileSystemEntryState.Denied:
                throw new MigrationActionException(action, $"could not check {folder}: it cannot be read (access denied)");
        }
        // In preview the files an earlier action would have deleted are still there.
        var pendingDeletes = run.Actions.Where(a => a.Preview && a.Kind == "delete-file").Select(a => a.Target)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<string> left;
        try
        {
            left = fs.EnumerateFiles(path, "*", recursive: true).Where(f => !pendingDeletes.Contains(f)).ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            throw new MigrationActionException(action, $"could not list {folder}: {ex.Message}");
        }
        if (left.Count > 0)
        {
            ctx.Log.Info($"{folder} still holds {left.Count} file(s); left in place");
            return false;
        }

        var record = new MigrationAction("delete-folder", folder.ToString(), null, ctx.IsPreview);
        if (!ctx.IsPreview)
        {
            var journaled = await JournalAsync(run, $"recreate {folder}", new UndoEntry(UndoEntry.Folder, Path: path), action, ct);
            var deleted = await fs.DeleteDirectoryAsync(path, recursive: false, ct);
            if (!deleted.Success)
            {
                if (journaled) await MigrationUndo.DropLastAsync(run, ct);
                ctx.Log.Info($"{folder} could not be removed ({deleted.Error?.Message ?? "not empty"}); left in place");
                return false;
            }
        }
        Record(run, record);
        return true;
    }

    public static async Task<int> RunProgramAsync(MigrationRun run, string exePath, IReadOnlyList<string> arguments, IReadOnlyList<int> successExitCodes, CancellationToken ct)
    {
        const string action = "RunProgram";
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(successExitCodes);
        var ctx = run.Context;
        if (!Path.IsPathFullyQualified(exePath))
            throw new MigrationActionException(action, $"'{exePath}' is not a full path");
        switch (ctx.FileSystem.GetEntryState(exePath))
        {
            case FileSystemEntryState.Denied:
                throw new MigrationActionException(action, $"'{exePath}' cannot be read (access denied)");
            case not FileSystemEntryState.File:
                throw new MigrationActionException(action, $"'{exePath}' does not exist");
        }
        IReadOnlyList<int> success = successExitCodes.Count == 0 ? [0] : successExitCodes;
        var commandLine = arguments.Count == 0 ? exePath : $"{exePath} {string.Join(' ', arguments)}";

        if (ctx.IsPreview)
        {
            Record(run, new MigrationAction("run-program", commandLine, null, Preview: true));
            return success[0];
        }

        int code;
        try
        {
            code = await ctx.Runtime.Programs.RunAsync(exePath, arguments, ProgramTimeout, ct);
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new MigrationActionException(action, ex.Message);
        }
        Record(run, new MigrationAction("run-program", commandLine, $"exit code {code}"));
        if (!success.Contains(code))
            throw new MigrationActionException(action, $"'{Path.GetFileName(exePath)}' exited with {code}; success is {string.Join(" or ", success)}");
        return code;
    }

    /// <summary>Replays <paramref name="run"/>'s undo journal, newest first; a failing entry is a warning.</summary>
    /// <remarks>
    /// The undo folder holds the only copy of every file the migration deleted, so it is removed
    /// only when every entry was undone; otherwise the failed entries stay in <c>undo.json</c>, the
    /// copies stay, the log says where, and the next installer run tries again
    /// (<see cref="MigrationUndo.RecoverAsync"/>).
    /// </remarks>
    public static async Task UndoAsync(MigrationRun run, CancellationToken ct)
    {
        var ctx = run.Context;
        var failed = new List<UndoStep>();
        for (var i = run.UndoJournal.Count - 1; i >= 0; i--)
        {
            var step = run.UndoJournal[i];
            try
            {
                if (step.Entry is { } entry)
                    await MigrationUndo.ApplyAsync(ctx.FileSystem, ctx.PlatformServices, entry, ct);
                else if (step.InMemory is { } undo)
                    await undo(ct);
                ctx.Log.Info($"rollback: {step.What}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed.Insert(0, step);
                ctx.Log.Warn($"rollback: could not {step.What}: {ex.Message}");
            }
        }
        run.UndoJournal.Clear();
        run.UndoJournal.AddRange(failed.Where(s => s.Entry is not null));

        if (run.UndoJournal.Count == 0)
        {
            await DeleteUndoFolderAsync(run, ct);
            return;
        }
        ctx.Runtime.UndoCopiesKept = true;
        try
        {
            await MigrationUndo.PersistAsync(run, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ctx.Log.Warn($"rollback: could not update the undo journal: {ex.Message}");
        }
        ctx.Log.Warn(
            $"rollback: {failed.Count} change(s) could not be undone; the files the migration had deleted are kept in " +
            $"'{MigrationUndo.FolderFor(run)}', and the next installer run tries again");
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static string ResolveForAction(MigrationRun run, MigrationFolder folder, string action)
    {
        var resolved = MigrationFolderGuard.Resolve(folder, run.Context, forAction: true);
        return resolved.Path ?? throw new MigrationActionException(action, $"refused: {resolved.Refusal}");
    }

    private static void Record(MigrationRun run, MigrationAction record)
    {
        run.Actions.Add(record);
        run.Context.Log.Info(record.Preview ? $"preview: {record}" : record.ToString());
    }

    /// <summary>For a BeforeCommit migration, journals <paramref name="entry"/> on disk before the change. Returns whether it did.</summary>
    private static async Task<bool> JournalAsync(MigrationRun run, string what, UndoEntry entry, string action, CancellationToken ct)
    {
        if (!run.Journaled) return false;
        await MigrationUndo.AppendAsync(run, what, entry, action, ct);
        return true;
    }

    private static UndoEntry RunValueEntry(MigrationContext ctx, string name, RegistryValueData data) =>
        new(UndoEntry.RunValue, Name: name, PerUser: ctx.PerUser, ValueKind: data.Kind, Value: (string)data.Value);

    /// <summary>The Run value, or null when it does not exist; a value that cannot be read fails the action.</summary>
    private static async Task<RegistryValueData?> ReadRunValueAsync(MigrationContext ctx, string name, string action, CancellationToken ct)
    {
        var read = await RunValues.ReadAsync(ctx.PlatformServices, name, ctx.PerUser, ct);
        if (read.Failed)
            throw new MigrationActionException(action, $"could not read Run value '{name}': {read.Error}");
        return read.Value;
    }

    private static bool SameItem(ManifestAdoptedItem a, ManifestAdoptedItem b) =>
        a.Kind == b.Kind && a.PerUser == b.PerUser && string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Moves <paramref name="file"/> into the migration's undo folder instead of deleting it, so a
    /// failed (or interrupted) install can put it back; the move is journaled first.
    /// </summary>
    private static async Task MoveToUndoAsync(MigrationRun run, string file, string action, CancellationToken ct)
    {
        var fs = run.Context.FileSystem;
        var backup = Path.Combine(MigrationUndo.FolderFor(run),
            run.UndoJournal.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-" + Path.GetFileName(file));
        await MigrationUndo.AppendAsync(run, $"restore '{file}'", new UndoEntry(UndoEntry.File, Path: file, Backup: backup), action, ct);

        var moved = await fs.MoveFileAsync(file, backup, overwrite: true, ct);
        if (moved.Success) return;
        // Another volume, or a file that cannot be moved: copy it, then delete it.
        var copied = await fs.CopyFileAsync(file, backup, overwrite: true, ct);
        var deleted = copied.Success ? await fs.DeleteFileAsync(file, ct) : copied;
        if (deleted.Success) return;
        if (copied.Success) await fs.DeleteFileAsync(backup, ct);
        await MigrationUndo.DropLastAsync(run, ct);
        throw new MigrationActionException(action, $"could not delete '{file}': {deleted.Error?.Message ?? moved.Error?.Message ?? "unknown error"}");
    }

    private static async Task DeleteUndoFolderAsync(MigrationRun run, CancellationToken ct)
    {
        var fs = run.Context.FileSystem;
        var dir = MigrationUndo.FolderFor(run);
        if (fs.DirectoryExists(dir))
            await fs.DeleteDirectoryAsync(dir, recursive: true, ct);
        var runDir = run.Context.Runtime.UndoDirectory;
        if (fs.DirectoryExists(runDir) && !fs.EnumerateFiles(runDir, "*", recursive: true).Any())
            await fs.DeleteDirectoryAsync(runDir, recursive: true, ct);
    }
}
