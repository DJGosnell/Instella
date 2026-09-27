using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.FileSystem;
using Instella.Core.Installation;
using Instella.Installer.Runtime.Core;
using Instella.Installer.Runtime.Core.Transactions;

namespace Instella.Installer.Runtime.Installation.BuiltIn;

/// <summary>
/// Stages <see cref="InstallContext.PayloadArchive"/> into a new
/// <see cref="InstallTransaction"/> rooted at <see cref="InstallContext.InstallPath"/>:
/// nothing live is written here. Files the previous installation had but the payload no
/// longer contains become delete operations. The built-in <c>commit-transaction</c> step
/// swaps everything into place at the start of <see cref="InstallStage.Finalize"/>.
/// </summary>
internal sealed class ExtractPayloadStep : IInstallStepExecution
{
    public string Name => "extract-payload";
    public InstallStage Stage => InstallStage.Extract;
    public int Weight => 5;

    public async Task<StepResult> ExecuteAsync(InstallContext context, IStepProgress progress, CancellationToken cancellationToken)
    {
        if (context.PayloadArchive is null)
            return StepResult.Fail("no payload archive provided");

        // A first install creates the install directory; rollback removes it again once the
        // transaction has put everything back (the ledger unwinds after step rollbacks).
        if (!context.FileSystem.DirectoryExists(context.InstallPath))
        {
            await context.FileSystem.CreateDirectoryAsync(context.InstallPath, cancellationToken);
            context.TrackDirectory(context.InstallPath, recursive: false);
        }

        var existing = context.ExistingInstallation;
        var txn = await InstallTransaction.BeginAsync(
            context.FileSystem, context.InstallPath, KindOf(context.Mode), existing?.Version, context.AppVersion,
            cancellationToken, context.Log);
        context.Transaction = txn;

        List<InstalledFile> files;
        try
        {
            files = new List<InstalledFile>();
            using var zip = new ZipArchive(context.PayloadArchive, ZipArchiveMode.Read, leaveOpen: true);
            var entries = zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
            var processed = 0;
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // The signed stub rides in the payload; StageUninstallerStubStep stages it.
                if (SafePath.TryNormalizeRelative(entry.FullName, out var carried, out _)
                    && string.Equals(carried, InstellaOwnedPaths.PayloadStub, StringComparison.OrdinalIgnoreCase))
                {
                    processed++;
                    continue;
                }

                // Entry names come from the payload, so they are untrusted: the transaction
                // refuses anything unsafe or Instella-owned.
                await using (var entryStream = entry.Open())
                {
                    // The build embeds the app icon at an Instella-owned path.
                    var hash = SafePath.TryNormalizeRelative(entry.FullName, out var rel, out _)
                               && string.Equals(rel, InstellaOwnedPaths.AppIcon, StringComparison.OrdinalIgnoreCase)
                        ? await txn.StageOwnedFileAsync(rel, entryStream, executable: false, cancellationToken)
                        : await txn.StageFileAsync(entry.FullName, entryStream, expectedSha256: null,
                            expectedSize: entry.Length, IsExecutable(entry), cancellationToken);
                    SafePath.TryNormalizeRelative(entry.FullName, out var relative, out _);
                    files.Add(new InstalledFile(relative, hash, entry.Length));
                }

                processed++;
                progress.Report(processed / (double)entries.Count, $"Extracting {entry.Name}");
            }

            // Files the previous version installed that this payload no longer contains.
            if (existing is not null)
            {
                var incoming = files.Select(f => f.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var old in existing.Files)
                {
                    if (SafePath.TryNormalizeRelative(old.RelativePath, out var rel, out _)
                        && !incoming.Contains(rel) && !InstellaOwnedPaths.IsOwned(rel))
                        txn.Delete(rel);
                }
            }
        }
        catch
        {
            // The executor only rolls back completed steps; clean up our own staging.
            await RollbackAsync(context, CancellationToken.None);
            throw;
        }

        context.ExtractedFiles = files;
        progress.Report(1.0);
        return StepResult.Ok;
    }

    /// <summary>
    /// Undoes a commit that happened (idempotent: it inspects the file system) and removes
    /// the transaction folder. Runs after <c>commit-transaction</c>'s own rollback.
    /// </summary>
    public async Task RollbackAsync(InstallContext context, CancellationToken cancellationToken)
    {
        if (context.Transaction is not { } txn) return;
        await txn.RollbackAsync();
        await txn.CompleteAsync();
    }

    private static TxnKind KindOf(InstallerMode mode) => mode switch
    {
        InstallerMode.Upgrade => TxnKind.Upgrade,
        InstallerMode.Repair => TxnKind.Repair,
        _ => TxnKind.FirstInstall,
    };

    /// <summary><c>0755</c>: owner rwx, group and others r-x.</summary>
    internal const UnixFileMode ExecutableMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    /// <summary>True when any execute bit is set in the entry's recorded Unix mode.</summary>
    internal static bool IsExecutable(ZipArchiveEntry entry)
    {
        var unixMode = (entry.ExternalAttributes >> 16) & 0xFFFF;
        return (unixMode & 0b001_001_001) != 0;
    }
}
