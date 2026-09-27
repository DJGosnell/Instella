using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Installation;

namespace Instella.Installer.Runtime.Installation;

/// <summary>
/// Generic undo logic for every <see cref="LedgerKind"/>. Invoked by
/// <see cref="TrackingLedger.UnwindAsync"/> in reverse-registration order
/// during a failed install. Each kind dispatches to the appropriate
/// platform-services or filesystem call; failures collect into the warnings
/// list rather than propagating, so a flaky rollback never masks the original
/// install error or aborts the rest of the unwind.
/// </summary>
internal static class TrackedRollbackStrategy
{
    public static async Task UnwindAsync(
        TrackingLedger ledger,
        InstallContext context,
        List<string> warnings,
        int fromIndex,
        CancellationToken cancellationToken)
    {
        var entries = ledger.Entries;
        var startInclusive = Math.Max(0, fromIndex);

        for (var i = entries.Count - 1; i >= startInclusive; i--)
        {
            var entry = entries[i];
            try
            {
                await UndoAsync(entry, context, warnings, cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Outer-cancelled rollbacks are still allowed to continue.
            }
            catch (Exception ex)
            {
                warnings.Add($"rollback: {entry.Kind} '{entry.Path}' threw: {ex.Message}");
            }
        }
    }

    private static async Task UndoAsync(
        LedgerEntry entry,
        InstallContext context,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        switch (entry.Kind)
        {
            case LedgerKind.File:
                if (context.FileSystem.Exists(entry.Path))
                {
                    var fileResult = await context.FileSystem.DeleteFileAsync(entry.Path, cancellationToken);
                    if (!fileResult.Success)
                        warnings.Add($"rollback: could not delete file '{entry.Path}': {fileResult.Error?.Message ?? "unknown"}");
                }
                break;

            case LedgerKind.Directory:
                if (context.FileSystem.DirectoryExists(entry.Path))
                {
                    var dirResult = await context.FileSystem.DeleteDirectoryAsync(entry.Path, entry.Recursive, cancellationToken);
                    if (!dirResult.Success)
                        warnings.Add($"rollback: could not delete directory '{entry.Path}': {dirResult.Error?.Message ?? "unknown"}");
                }
                break;

            case LedgerKind.RegistryValue:
                {
                    var perUser = context.Scope == InstallationScope.PerUser;
                    var ok = await context.Platform.DeleteRegistryValueAsync(entry.Hive, entry.Path, entry.ValueName ?? string.Empty, perUser, cancellationToken);
                    if (!ok.Success)
                        warnings.Add($"rollback: could not delete registry value '{entry.ValueName}' under '{entry.Path}': {ok.Error}");
                }
                break;

            case LedgerKind.RegistryKey:
                {
                    var perUser = context.Scope == InstallationScope.PerUser;
                    var ok = await context.Platform.DeleteRegistryKeyAsync(entry.Hive, entry.Path, perUser, cancellationToken);
                    if (!ok.Success)
                        warnings.Add($"rollback: could not delete registry key '{entry.Path}': {ok.Error}");
                }
                break;

            case LedgerKind.RestoreRegistryValue:
                {
                    var perUser = context.Scope == InstallationScope.PerUser;
                    var previous = entry.Previous!;
                    var ok = await context.Platform.WriteRegistryValueAsync(entry.Hive, entry.Path, entry.ValueName ?? string.Empty,
                        previous.Kind, previous.Value, perUser, cancellationToken);
                    if (!ok.Success)
                        warnings.Add($"rollback: could not restore registry value '{entry.ValueName}' under '{entry.Path}': {ok.Error}");
                }
                break;

            case LedgerKind.PathEntry:
                {
                    var perUser = context.Scope == InstallationScope.PerUser;
                    var ok = await context.Platform.RemoveFromPathAsync(entry.Path, perUser, cancellationToken);
                    if (!ok.Success)
                        warnings.Add($"rollback: could not remove PATH entry '{entry.Path}': {ok.Error}");
                }
                break;
        }
    }
}
