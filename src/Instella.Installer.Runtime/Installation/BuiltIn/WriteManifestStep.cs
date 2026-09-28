using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Installation;
using Instella.Core.Platform;
using Instella.Installer.Runtime.Core;

namespace Instella.Installer.Runtime.Installation.BuiltIn;

/// <summary>
/// Builds <c>.instella-manifest.json</c> and stages it as the install transaction's last
/// operation, so the installed manifest names the new version only once every other file
/// is in place. Runs in <see cref="InstallStage.Finalize"/> just before
/// <c>commit-transaction</c>. Without a transaction (a step run in isolation) it writes the
/// file directly and tracks it on the ledger.
/// </summary>
internal sealed class WriteManifestStep : IInstallStepExecution
{
    private const string ManifestFileName = ".instella-manifest.json";

    public string Name => "write-manifest";
    public InstallStage Stage => InstallStage.Finalize;
    public int Weight => 1;

    public async Task<StepResult> ExecuteAsync(InstallContext context, IStepProgress progress, CancellationToken cancellationToken)
    {
        var executableName = Path.GetRelativePath(context.InstallPath, ExecutableResolver.Resolve(context)).Replace('\\', '/');

        var manifest = new InstalledManifest
        {
            AppName = context.Manifest.AppName,
            AppId = context.Manifest.AppId,
            Version = context.Manifest.Version,
            InstallDirectory = context.InstallPath,
            ExecutableName = executableName,
            InstalledAt = DateTime.UtcNow,
            // Outcomes, not requests: a platform call that failed leaves nothing to undo.
            HasDesktopShortcut = context.CreatedDesktopShortcut,
            HasStartMenuShortcut = context.CreatedStartMenuShortcut,
            AddedToPath = context.AddedToPath,
            HasAutoStart = context.ConfiguredAutoStart,
            HasUninstallEntry = context.UninstallEntryRegistered,
            InstalledPerUser = context.Scope == InstallationScope.PerUser,
            FileAssociations = context.RegisteredAssociations.Count > 0 ? context.RegisteredAssociations.ToList() : null,
            Platform = context.Platform.Platform,
            // The installer is published for the same RID as its payload, so its own
            // process architecture is the architecture being installed.
            Architecture = ArchitectureExtensions.Current,
            ServerUrl = context.Manifest.ServerUrl,
            Channel = context.Manifest.Channel,
            TrustedKeys = TrustedKeysAfterInstall(context),
            AllowUnsignedUpdates = context.Manifest.AllowUnsignedUpdates,
            AllowInsecureServer = context.Manifest.AllowInsecureServer,
            DownloadToken = context.Manifest.DownloadToken,
            Files = context.ExtractedFiles,
            // v3 additions: carry the registry writes, declared CLI flags,
            // and logging snapshot into the manifest so uninstall and manage
            // flows have first-class access to them without re-reading the
            // builder config.
            Registry = MergeRegistry(context),
            TrackedItems = MergeTrackedItems(context),
            CompletedMigrations = MergeCompletedMigrations(context),
            AdoptedItems = MergeAdoptedItems(context),
            DeclaredCliFlags = context.ManifestCliFlags,
            Logging = context.ManifestLogging,
        };

        if (context.Transaction is { } txn)
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(manifest, InstalledManifestJsonContext.Default.InstalledManifest);
            await txn.StageOwnedFileAsync(InstellaOwnedPaths.InstalledManifest, new MemoryStream(json), executable: false, cancellationToken);
        }
        else
        {
            await new InstallManifestWriter(context.FileSystem).WriteAsync(context.InstallPath, manifest, cancellationToken);
            context.TrackFile(Path.Combine(context.InstallPath, ManifestFileName));
        }
        progress.Report(1.0);
        return StepResult.Ok;
    }

    /// <summary>
    /// The keys to trust after this run. An update may have rotated the installation's keys
    /// since this installer was built, so a repair or downgrade with an older
    /// installer keeps the recorded list rather than reinstating a key the publisher revoked.
    /// Only an installer for a newer version, or one over an installation that trusts no keys,
    /// installs its compiled-in keys.
    /// </summary>
    internal static IReadOnlyList<Instella.Core.Trust.PublisherKey>? TrustedKeysAfterInstall(InstallContext context)
    {
        if (context.ExistingInstallation is { TrustedKeys: { Count: > 0 } recorded } existing
            && Instella.Core.Utilities.AppVersions.Compare(context.Manifest.Version, existing.Version) <= 0)
            return recorded;
        return context.Manifest.PublisherKeys;
    }

    /// <summary>
    /// Registry values this install wrote, plus those the previous version recorded: an
    /// upgrade that no longer writes a value must still let uninstall find the old one.
    /// </summary>
    private static List<ManifestRegistryEntry>? MergeRegistry(InstallContext context)
    {
        var merged = context.ManifestRegistryEntries.ToList();
        foreach (var old in context.ExistingInstallation?.Registry ?? [])
        {
            if (!merged.Any(e => e.Hive == old.Hive && e.PerUser == old.PerUser
                    && string.Equals(e.KeyPath, old.KeyPath, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(e.ValueName, old.ValueName, StringComparison.OrdinalIgnoreCase)))
                merged.Add(old);
        }
        return merged.Count > 0 ? merged : null;
    }

    /// <summary>What custom steps tracked, plus what the previous version's did.</summary>
    private static List<ManifestTrackedItem>? MergeTrackedItems(InstallContext context)
    {
        var merged = (context.ExistingInstallation?.TrackedItems ?? []).ToList();
        foreach (var item in context.Ledger.UserTrackedItems())
            if (!merged.Contains(item)) merged.Add(item);
        return merged.Count > 0 ? merged : null;
    }

    /// <summary>
    /// Ids of the run-once migrations completed for this installation: the previous version's
    /// and this run's, sorted ordinally. Upgrades and repairs never drop one.
    /// </summary>
    internal static List<string>? MergeCompletedMigrations(InstallContext context)
    {
        var ids = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var id in context.ExistingInstallation?.CompletedMigrations ?? []) ids.Add(id);
        foreach (var id in context.Migrations.Completed) ids.Add(id);
        return ids.Count > 0 ? ids.ToList() : null;
    }

    /// <summary>
    /// What uninstall removes although Instella did not create it: what the previous version
    /// recorded (kept even when this version no longer has the migration that adopted it), the
    /// <c>WithAppManagedAutoStart</c> values, and what this run's migrations adopted. The first
    /// entry for a (kind, name, scope) wins, so an item keeps its original source.
    /// </summary>
    internal static List<ManifestAdoptedItem>? MergeAdoptedItems(InstallContext context)
    {
        var perUser = context.Scope == InstallationScope.PerUser;
        var merged = new List<ManifestAdoptedItem>();
        void Add(ManifestAdoptedItem item)
        {
            if (!merged.Any(m => m.Kind == item.Kind && m.PerUser == item.PerUser
                                 && string.Equals(m.Name, item.Name, StringComparison.OrdinalIgnoreCase)))
                merged.Add(item);
        }
        foreach (var item in context.ExistingInstallation?.AdoptedItems ?? []) Add(item);
        foreach (var name in context.AppManagedRunValues)
            Add(new ManifestAdoptedItem(ManifestAdoptedItem.RunValue, name, perUser, ManifestAdoptedItem.AppManaged));
        foreach (var item in context.Migrations.Adopted) Add(item);
        return merged.Count > 0 ? merged : null;
    }

    /// <summary>
    /// Custom Finalize steps and AfterCommit migrations run after the commit, so what they track,
    /// complete and adopt reaches the installed manifest here: it is rewritten atomically (temp
    /// file + rename) when it changed.
    /// </summary>
    internal static async Task AmendManifestAsync(InstallContext context, CancellationToken ct)
    {
        var writer = new InstallManifestWriter(context.FileSystem);
        var current = await writer.ReadAsync(context.InstallPath, ct);
        if (current is null) return;
        var items = MergeTrackedItems(context);
        var completed = MergeCompletedMigrations(context);
        var adopted = MergeAdoptedItems(context);
        if ((current.TrackedItems ?? []).SequenceEqual(items ?? [])
            && (current.CompletedMigrations ?? []).SequenceEqual(completed ?? [])
            && (current.AdoptedItems ?? []).SequenceEqual(adopted ?? []))
            return;

        var amended = current with { TrackedItems = items, CompletedMigrations = completed, AdoptedItems = adopted };
        var json = JsonSerializer.SerializeToUtf8Bytes(amended, InstalledManifestJsonContext.Default.InstalledManifest);
        var tmp = Path.Combine(context.InstallPath, ManifestFileName + ".tmp");
        await context.FileSystem.WriteAllBytesAsync(tmp, json, ct);
        await context.FileSystem.MoveFileAsync(tmp, Path.Combine(context.InstallPath, ManifestFileName), overwrite: true, ct);
    }
}
