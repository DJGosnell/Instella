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
using Instella.Core.Logging;
using Instella.Core.Platform;
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Runtime.Core.Transactions;

namespace Instella.Installer.Runtime.Core.Services;

/// <summary>
/// The services an operation stopped, kept beside its transaction journal as
/// <c>.instella/txn/{id}/services-running.json</c> so that recovery after a crash (or a failed
/// rollback) can start them again. The journal format is unchanged: older stubs delete the
/// folder, file included, as before.
/// </summary>
internal static class ServicesRunningFile
{
    public const string FileName = "services-running.json";

    /// <summary>Writes <paramref name="names"/> into <paramref name="txn"/>'s folder; nothing when empty.</summary>
    public static async Task WriteAsync(IFileSystem fs, InstallTransaction txn, IReadOnlyList<string> names, CancellationToken ct)
    {
        if (names.Count == 0) return;
        var path = Path.Combine(txn.Root, TransactionJournal.TxnDirectory.Replace('/', Path.DirectorySeparatorChar), txn.TxnId, FileName);
        var json = JsonSerializer.SerializeToUtf8Bytes(new ServicesRunning { Services = [.. names] }, ServicesRunningJsonContext.Default.ServicesRunning);
        var written = await fs.WriteAllBytesAsync(path, json, ct);
        if (!written.Success)
            throw new IOException($"Could not record the stopped services in '{path}': {written.Error?.Message}");
    }

    /// <summary>
    /// Every service listed by a side file under <paramref name="installRoot"/>'s transactions, in
    /// the order they were stopped. An unreadable file is logged and skipped.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ReadAllAsync(IFileSystem fs, string installRoot, IInstellaLogger? log, CancellationToken ct)
    {
        var txnRoot = Path.Combine(Path.GetFullPath(installRoot), TransactionJournal.TxnDirectory.Replace('/', Path.DirectorySeparatorChar));
        if (!fs.DirectoryExists(txnRoot)) return [];

        var names = new List<string>();
        foreach (var file in fs.EnumerateFiles(txnRoot, FileName, recursive: true))
        {
            // Only {txn}/services-running.json, never one inside a staged payload.
            var txnDir = Path.GetDirectoryName(file);
            if (txnDir is null || !string.Equals(Path.GetDirectoryName(txnDir), txnRoot, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var read = await fs.ReadAllBytesAsync(file, ct);
                if (!read.Success) throw new IOException(read.Error?.Message);
                var listed = JsonSerializer.Deserialize(read.Value!, ServicesRunningJsonContext.Default.ServicesRunning)?.Services ?? [];
                foreach (var name in listed)
                    if (!string.IsNullOrWhiteSpace(name) && !names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log?.Warn($"recover: ignoring the unreadable list of stopped services '{file}': {ex.Message}");
            }
        }
        return names;
    }
}

/// <summary>
/// <see cref="InstallTransaction.RecoverAsync"/>, then the services the interrupted operation had
/// stopped are started again on the now consistent files.
/// </summary>
internal static class ServiceRecovery
{
    /// <summary>
    /// Reads the stopped-services side files (recovery deletes them with their folders), recovers,
    /// then starts each listed service the installed manifest records that exists and is not
    /// running, dependencies first. A start failure is only a warning: the files are consistent.
    /// </summary>
    /// <returns>True when an interrupted commit was rolled back.</returns>
    /// <exception cref="UnsupportedJournalException">As <see cref="InstallTransaction.RecoverAsync"/>.</exception>
    public static async Task<bool> RecoverAsync(IFileSystem fs, string installRoot, IReadOnlyList<ServiceSpec>? declared,
        IServiceManager? manager, IInstellaLogger log, CancellationToken ct, IReadOnlyList<TimeSpan>? retryDelays = null)
    {
        var listed = await ServicesRunningFile.ReadAllAsync(fs, installRoot, log, ct);
        var rolledBack = await InstallTransaction.RecoverAsync(fs, installRoot, ct, log, retryDelays);
        if (listed.Count == 0) return rolledBack;

        try
        {
            var manifest = await new InstallManifestWriter(fs).ReadAsync(installRoot, ct);
            if (InstallationServices.For(manifest, declared, manager, log) is not { } services) return rolledBack;
            // StartAsync takes the stop order (dependents first) and starts it backwards.
            var stopOrder = (await services.InStartOrderAsync(ct))
                .Where(n => listed.Contains(n, StringComparer.OrdinalIgnoreCase))
                .Reverse().ToList();
            foreach (var (_, error) in await services.StartAsync(stopOrder, ct))
                log.Warn($"recover: {error}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.Warn($"recover: could not start the stopped services again: {ex.Message}");
        }
        return rolledBack;
    }
}

internal sealed class ServicesRunning
{
    public List<string> Services { get; set; } = [];
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ServicesRunning))]
internal partial class ServicesRunningJsonContext : JsonSerializerContext;
