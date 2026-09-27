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
/// run's seams, records what it did, reports instead of acting in preview, and — for
/// <see cref="MigrationTiming.BeforeCommit"/> migrations — journals how to undo it.
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
        var gate = new RunningAppGate(ctx.PlatformServices, ctx.Log, rt.ProcessFinder, ctx.FileSystem, rt.ProcessCloser);

        if (rt.IsPreview)
        {
            foreach (var p in gate.FindBlockers(path, executableName: null).Where(p => p.CanClose))
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
        var data = await RunValues.ReadAsync(ctx.PlatformServices, name, ctx.PerUser, ct);
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

        await WriteRunValueAsync(ctx, name, InstellaRegistryValueKind.String, target, action, ct);
        Record(run, record);
        Journal(run, $"restore Run value '{name}'", t => WriteRunValueAsync(ctx, name, data!.Kind, data.Value, action, t));
        return true;
    }

    public static async Task<bool> DeleteRunValueAsync(MigrationRun run, string name, MigrationFolder pointingInto, CancellationToken ct)
    {
        const string action = "DeleteRunValue";
        ArgumentNullException.ThrowIfNull(pointingInto);
        var path = ResolveForAction(run, pointingInto, action);
        var ctx = run.Context;
        var data = await RunValues.ReadAsync(ctx.PlatformServices, name, ctx.PerUser, ct);
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

        var deleted = await ctx.PlatformServices.DeleteRegistryValueAsync(
            RunValues.Hive(ctx.PerUser), RunCommand.RunKey, name, ctx.PerUser, ct);
        if (!deleted.Success)
            throw new MigrationActionException(action, $"could not delete Run value '{name}': {deleted.Error}");
        Record(run, record);
        Journal(run, $"restore Run value '{name}'", t => WriteRunValueAsync(ctx, name, data!.Kind, data.Value, action, t));
        return true;
    }

    public static async Task<bool> AdoptRunValueAsync(MigrationRun run, string name, CancellationToken ct)
    {
        var ctx = run.Context;
        var data = await RunValues.ReadAsync(ctx.PlatformServices, name, ctx.PerUser, ct);
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
            Journal(run, $"forget adopted Run value '{name}'", _ =>
            {
                adopted.RemoveAll(a => SameItem(a, item));
                return Task.CompletedTask;
            });
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
        var count = 0;
        foreach (var name in names)
        {
            ct.ThrowIfCancellationRequested();
            var file = SafePath.Combine(path, name);
            if (!fs.Exists(file)) continue;
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
        if (!fs.DirectoryExists(path)) return false;
        // In preview the files an earlier action would have deleted are still there.
        var pendingDeletes = run.Actions.Where(a => a.Preview && a.Kind == "delete-file").Select(a => a.Target)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var left = fs.EnumerateFiles(path, "*", recursive: true).Where(f => !pendingDeletes.Contains(f)).ToList();
        if (left.Count > 0)
        {
            ctx.Log.Info($"{folder} still holds {left.Count} file(s); left in place");
            return false;
        }

        var record = new MigrationAction("delete-folder", folder.ToString(), null, ctx.IsPreview);
        if (!ctx.IsPreview)
        {
            var deleted = await fs.DeleteDirectoryAsync(path, recursive: false, ct);
            if (!deleted.Success)
            {
                ctx.Log.Info($"{folder} could not be removed ({deleted.Error?.Message ?? "not empty"}); left in place");
                return false;
            }
            Journal(run, $"recreate {folder}", async t => await fs.CreateDirectoryAsync(path, t));
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
        if (!ctx.FileSystem.Exists(exePath))
            throw new MigrationActionException(action, $"'{exePath}' does not exist");
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
    public static async Task UndoAsync(MigrationRun run, CancellationToken ct)
    {
        for (var i = run.UndoJournal.Count - 1; i >= 0; i--)
        {
            var (what, undo) = run.UndoJournal[i];
            try
            {
                await undo(ct);
                run.Context.Log.Info($"rollback: {what}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                run.Context.Log.Warn($"rollback: could not {what}: {ex.Message}");
            }
        }
        run.UndoJournal.Clear();
        await DeleteUndoFolderAsync(run.Context, run.Migration.Id, ct);
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

    private static void Journal(MigrationRun run, string what, Func<CancellationToken, Task> undo)
    {
        if (run.Journaled) run.UndoJournal.Add((what, undo));
    }

    private static bool SameItem(ManifestAdoptedItem a, ManifestAdoptedItem b) =>
        a.Kind == b.Kind && a.PerUser == b.PerUser && string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);

    private static async Task WriteRunValueAsync(MigrationContext ctx, string name, InstellaRegistryValueKind kind, object value, string action, CancellationToken ct)
    {
        var written = await ctx.PlatformServices.WriteRegistryValueAsync(
            RunValues.Hive(ctx.PerUser), RunCommand.RunKey, name, kind, value, ctx.PerUser, ct);
        if (!written.Success)
            throw new MigrationActionException(action, $"could not write Run value '{name}': {written.Error}");
    }

    /// <summary>
    /// Moves <paramref name="file"/> into the run's undo folder instead of deleting it, so a
    /// failed install can put it back. The folder is deleted once the install completes.
    /// </summary>
    private static async Task MoveToUndoAsync(MigrationRun run, string file, string action, CancellationToken ct)
    {
        var ctx = run.Context;
        var fs = ctx.FileSystem;
        var dir = Path.Combine(ctx.Runtime.UndoDirectory, run.Migration.Id);
        var backup = Path.Combine(dir, run.UndoJournal.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-" + Path.GetFileName(file));
        await fs.CreateDirectoryAsync(dir, ct);
        var moved = await fs.MoveFileAsync(file, backup, overwrite: true, ct);
        if (!moved.Success)
        {
            // Another volume, or a file that cannot be moved: copy it, then delete it.
            var copied = await fs.CopyFileAsync(file, backup, overwrite: true, ct);
            var deleted = copied.Success ? await fs.DeleteFileAsync(file, ct) : copied;
            if (!deleted.Success)
                throw new MigrationActionException(action, $"could not delete '{file}': {deleted.Error?.Message ?? moved.Error?.Message ?? "unknown error"}");
        }

        if (!ctx.Runtime.UndoCleanupRegistered)
        {
            ctx.Runtime.UndoCleanupRegistered = true;
            var undoDirectory = ctx.Runtime.UndoDirectory;
            ctx.Install.CompletionActions.Add(async () =>
            {
                if (fs.DirectoryExists(undoDirectory))
                    await fs.DeleteDirectoryAsync(undoDirectory, recursive: true, CancellationToken.None);
            });
        }
        Journal(run, $"restore '{file}'", async t =>
        {
            await fs.CreateDirectoryAsync(Path.GetDirectoryName(file)!, t);
            var back = await fs.MoveFileAsync(backup, file, overwrite: true, t);
            if (!back.Success) throw new IOException(back.Error?.Message ?? "move failed");
        });
    }

    private static async Task DeleteUndoFolderAsync(MigrationContext ctx, string id, CancellationToken ct)
    {
        var dir = Path.Combine(ctx.Runtime.UndoDirectory, id);
        if (ctx.FileSystem.DirectoryExists(dir))
            await ctx.FileSystem.DeleteDirectoryAsync(dir, recursive: true, ct);
    }
}
