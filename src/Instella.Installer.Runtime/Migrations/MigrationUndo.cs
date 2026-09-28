using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.FileSystem;
using Instella.Core.Installation;
using Instella.Core.Platform;
using Instella.Core.Utilities;
using Instella.Installer.Runtime.Installation;
using Instella.Installer.Runtime.Runners;

namespace Instella.Installer.Runtime.Migrations;

/// <summary>
/// One change a <see cref="MigrationTiming.BeforeCommit"/> migration made, and how to undo it.
/// <see cref="Kind"/>: <c>file</c> (move <see cref="Backup"/> back to <see cref="Path"/>),
/// <c>folder</c> (recreate <see cref="Path"/>), <c>run-value</c> (write <see cref="Value"/> back
/// to the Run value <see cref="Name"/>).
/// </summary>
internal sealed record UndoEntry(
    string Kind,
    string? Path = null,
    string? Backup = null,
    string? Name = null,
    bool PerUser = true,
    InstellaRegistryValueKind ValueKind = InstellaRegistryValueKind.String,
    string? Value = null)
{
    public const string File = "file";
    public const string Folder = "folder";
    public const string RunValue = "run-value";
}

/// <summary>
/// <c>undo.json</c> in a migration's undo folder: what the migration changed, written before each
/// change, so a crash mid-install can still be undone by the next installer run.
/// </summary>
internal sealed record UndoJournalFile(
    int FormatVersion,
    string MigrationId,
    string InstallPath,
    string TargetVersion,
    DateTime CreatedAt,
    IReadOnlyList<UndoEntry> Entries)
{
    public const int CurrentFormatVersion = 1;
    public const string FileName = "undo.json";
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(UndoJournalFile))]
internal partial class UndoJournalJsonContext : JsonSerializerContext;

/// <summary>A step of a migration's in-memory undo: a persisted <see cref="UndoEntry"/>, or an in-memory-only action.</summary>
internal sealed record UndoStep(string What, UndoEntry? Entry, Func<CancellationToken, Task>? InMemory = null);

/// <summary>
/// The crash-safe undo of <see cref="MigrationTiming.BeforeCommit"/> migrations. Every change is
/// journaled to <c>{undo folder}/{id}/undo.json</c> before it is made (write-ahead); undoing applies
/// the entries newest first; <see cref="RecoverAsync"/> finishes the job after a crash.
/// </summary>
internal static class MigrationUndo
{
    /// <summary>The migration's undo folder: the moved-aside files and <c>undo.json</c>.</summary>
    public static string FolderFor(MigrationRun run) => Path.Combine(run.Context.Runtime.UndoDirectory, run.Migration.Id);

    /// <summary>Journals <paramref name="entry"/> on disk, then in memory. Call before making the change.</summary>
    public static async Task AppendAsync(MigrationRun run, string what, UndoEntry entry, string action, CancellationToken ct)
    {
        run.UndoJournal.Add(new UndoStep(what, entry));
        try
        {
            await PersistAsync(run, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            run.UndoJournal.RemoveAt(run.UndoJournal.Count - 1);
            throw new MigrationActionException(action, $"could not record how to undo it, so nothing was changed: {ex.Message}");
        }
    }

    /// <summary>The change journaled last was not made after all: forget it.</summary>
    public static async Task DropLastAsync(MigrationRun run, CancellationToken ct)
    {
        run.UndoJournal.RemoveAt(run.UndoJournal.Count - 1);
        try
        {
            await PersistAsync(run, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The stale entry is harmless: undoing a change that never happened finds nothing to do.
            run.Context.Log.Warn($"could not update the undo journal: {ex.Message}");
        }
    }

    /// <summary>Writes the persisted entries to <c>undo.json</c> atomically (temp file + rename).</summary>
    public static async Task PersistAsync(MigrationRun run, CancellationToken ct)
    {
        var ctx = run.Context;
        var fs = ctx.FileSystem;
        var dir = FolderFor(run);
        run.JournalCreatedAt ??= DateTime.UtcNow;
        var journal = new UndoJournalFile(
            UndoJournalFile.CurrentFormatVersion, run.Migration.Id, ctx.InstallPath,
            AppVersions.ToCanonicalString(ctx.AppVersion), run.JournalCreatedAt.Value,
            run.UndoJournal.Where(s => s.Entry is not null).Select(s => s.Entry!).ToList());
        var bytes = JsonSerializer.SerializeToUtf8Bytes(journal, UndoJournalJsonContext.Default.UndoJournalFile);
        Check(await fs.CreateDirectoryAsync(dir, ct));
        var path = Path.Combine(dir, UndoJournalFile.FileName);
        Check(await fs.WriteAllBytesAsync(path + ".tmp", bytes, ct));
        Check(await fs.MoveFileAsync(path + ".tmp", path, overwrite: true, ct));
        RegisterCompletion(run);
    }

    /// <summary>Undoes one entry. Throws when it cannot; a missing backup means the change never happened.</summary>
    public static async Task ApplyAsync(IFileSystem fs, IPlatformServices platform, UndoEntry entry, CancellationToken ct)
    {
        switch (entry.Kind)
        {
            case UndoEntry.File:
                if (!fs.Exists(entry.Backup!)) return;   // journaled, then the move never happened
                if (fs.Exists(entry.Path!))
                    throw new IOException($"'{entry.Path}' exists again; the original is kept in '{entry.Backup}'");
                Check(await fs.CreateDirectoryAsync(Path.GetDirectoryName(entry.Path!)!, ct));
                Check(await fs.MoveFileAsync(entry.Backup!, entry.Path!, overwrite: false, ct));
                return;
            case UndoEntry.Folder:
                Check(await fs.CreateDirectoryAsync(entry.Path!, ct));
                return;
            case UndoEntry.RunValue:
                var written = await platform.WriteRegistryValueAsync(RunValues.Hive(entry.PerUser), RunCommand.RunKey, entry.Name!,
                    entry.ValueKind, entry.Value!, entry.PerUser, ct);
                if (!written.Success) throw new IOException(written.Error ?? "the Run value could not be written");
                return;
            default:
                throw new InvalidDataException($"unknown undo entry kind '{entry.Kind}'");
        }
    }

    /// <summary>
    /// After a crash or a killed installer: for every undo journal left for this install folder,
    /// either deletes the copies (the install that ran the migration was committed, so its changes
    /// stand) or undoes the changes (it was not). Runs at the start of the next installer run,
    /// while that run holds the install folder's lock, after the install transaction's own recovery.
    /// Journals of other install folders are left for their own next run. Never throws.
    /// </summary>
    public static async Task RecoverAsync(InstallContext context, CancellationToken ct)
    {
        var fs = context.FileSystem;
        var log = context.Log;
        var root = context.Migrations.UndoRoot;
        List<string> journals;
        try
        {
            if (!fs.DirectoryExists(root)) return;
            journals = fs.EnumerateFiles(root, UndoJournalFile.FileName, recursive: true).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.Warn($"migrations: could not look for interrupted migrations in '{root}': {ex.Message}");
            return;
        }

        InstalledManifest? installed = null;
        var manifestRead = false;
        foreach (var journalPath in journals)
        {
            var dir = Path.GetDirectoryName(journalPath)!;
            if (Core.PathGuards.IsSameOrInside(dir, context.Migrations.UndoDirectory)) continue;   // this run's own
            UndoJournalFile? journal;
            try
            {
                var bytes = await fs.ReadAllBytesAsync(journalPath, ct);
                journal = bytes.Success ? JsonSerializer.Deserialize(bytes.Value!, UndoJournalJsonContext.Default.UndoJournalFile) : null;
            }
            catch (JsonException)
            {
                journal = null;
            }
            if (journal is null || journal.FormatVersion != UndoJournalFile.CurrentFormatVersion)
            {
                log.Warn($"migrations: '{journalPath}' is not an undo journal this installer can read; left in place");
                continue;
            }
            if (!InstallPaths.TryNormalize(journal.InstallPath, requireRooted: false, out var journalInstall, out _)
                || !InstallPaths.TryNormalize(context.InstallPath, requireRooted: false, out var here, out _)
                || !InstallPaths.SameFolder(journalInstall, here))
                continue;

            if (!manifestRead)
            {
                installed = await new InstallManifestWriter(fs).ReadAsync(context.InstallPath, ct);
                manifestRead = true;
            }
            var committed = installed is not null
                && AppVersions.TryParse(journal.TargetVersion, out var target)
                && AppVersions.Equal(installed.Version, target)
                && installed.InstalledAt >= journal.CreatedAt;

            if (committed)
            {
                log.Info($"migration[{journal.MigrationId}]: the install that ran it completed; removing its undo copies");
                await DeleteFolderAsync(fs, dir, log, ct);
                continue;
            }

            log.Warn($"migration[{journal.MigrationId}]: an earlier install stopped before it completed; undoing what the migration changed");
            var remaining = new List<UndoEntry>();
            for (var i = journal.Entries.Count - 1; i >= 0; i--)
            {
                var entry = journal.Entries[i];
                try
                {
                    await ApplyAsync(fs, context.Platform, entry, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    remaining.Insert(0, entry);
                    log.Warn($"migration[{journal.MigrationId}]: could not undo {entry.Kind} '{entry.Path ?? entry.Name}': {ex.Message}");
                }
            }
            if (remaining.Count == 0)
            {
                await DeleteFolderAsync(fs, dir, log, ct);
            }
            else
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(journal with { Entries = remaining }, UndoJournalJsonContext.Default.UndoJournalFile);
                await fs.WriteAllBytesAsync(journalPath, bytes, ct);
                log.Warn($"migration[{journal.MigrationId}]: {remaining.Count} change(s) are still to undo; the copies stay in '{dir}' and the next installer run tries again");
            }
        }
    }

    private static async Task DeleteFolderAsync(IFileSystem fs, string dir, Instella.Core.Logging.IInstellaLogger log, CancellationToken ct)
    {
        if ((await fs.DeleteDirectoryAsync(dir, recursive: true, ct)) is { Success: false } failed)
        {
            log.Warn($"migrations: could not delete '{dir}': {failed.Error?.Message}");
            return;
        }
        // The run's folder goes too once nothing is left in it.
        var parent = Path.GetDirectoryName(dir);
        if (parent is not null && fs.DirectoryExists(parent) && !fs.EnumerateFiles(parent, "*", recursive: true).Any())
            await fs.DeleteDirectoryAsync(parent, recursive: true, ct);
    }

    /// <summary>Once the install is final (or passes a point of no return), the run's undo copies are deleted.</summary>
    private static void RegisterCompletion(MigrationRun run)
    {
        var runtime = run.Context.Runtime;
        if (runtime.UndoCleanupRegistered) return;
        runtime.UndoCleanupRegistered = true;
        var fs = run.Context.FileSystem;
        run.Context.Install.CompletionActions.Add(async () =>
        {
            if (!runtime.UndoCopiesKept && fs.DirectoryExists(runtime.UndoDirectory))
                await fs.DeleteDirectoryAsync(runtime.UndoDirectory, recursive: true, CancellationToken.None);
        });
    }

    private static void Check(FileSystemResult result)
    {
        if (!result.Success) throw new IOException(result.Error?.Message ?? "file system error");
    }
}
