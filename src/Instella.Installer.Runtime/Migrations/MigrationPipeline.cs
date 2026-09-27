using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Installation;
using Instella.Core.Logging;
using Instella.Core.Platform;
using Instella.Installer.Runtime.Installation;

namespace Instella.Installer.Runtime.Migrations;

/// <summary>Where migrations enter the install pipeline, and (for uninstall) the order they run in.</summary>
internal static class MigrationPipeline
{
    /// <summary>The built-in step the <see cref="MigrationTiming.BeforeCommit"/> migrations run just before.</summary>
    public const string BeforeCommitAnchor = "write-manifest";

    /// <summary>
    /// Adds one <see cref="MigrationStep"/> per install migration to <paramref name="ordered"/>
    /// (built-in and user steps, already ordered): <see cref="MigrationTiming.BeforeCommit"/>
    /// ones immediately before <c>write-manifest</c> (after every Register-stage step, so their
    /// completion is staged with the manifest), <see cref="MigrationTiming.AfterCommit"/> ones at
    /// the very end, after every custom Finalize step, so nothing that could fail the install runs
    /// after them. <paramref name="migrations"/> is already sorted (timing, order, id).
    /// </summary>
    public static IReadOnlyList<IInstallStepExecution> Insert(
        IReadOnlyList<IInstallStepExecution> ordered, IReadOnlyList<InstallMigration> migrations)
    {
        var before = migrations.Where(m => m.Timing == MigrationTiming.BeforeCommit).Select(m => new MigrationStep(m)).ToList();
        var after = migrations.Where(m => m.Timing == MigrationTiming.AfterCommit).Select(m => new MigrationStep(m)).ToList();
        if (before.Count == 0 && after.Count == 0) return ordered;

        var list = new List<IInstallStepExecution>(ordered.Count + before.Count + after.Count);
        var anchor = -1;
        for (var i = 0; i < ordered.Count; i++)
            if (ordered[i].Name == BeforeCommitAnchor) { anchor = i; break; }

        if (anchor < 0)
        {
            list.AddRange(ordered);
            list.AddRange(before);
        }
        else
        {
            list.AddRange(ordered.Take(anchor));
            list.AddRange(before);
            list.AddRange(ordered.Skip(anchor));
        }
        list.AddRange(after);
        return list;
    }

    /// <summary>The uninstall migrations, in order (<see cref="InstallMigration.Order"/>, then id).</summary>
    public static IReadOnlyList<InstallMigration> ForUninstall(IReadOnlyList<InstallMigration> migrations) =>
        migrations.Where(m => m.Timing == MigrationTiming.Uninstall).ToList();

    /// <summary>
    /// Uninstall: removes the Run values the installation adopted (<see cref="InstalledManifest.AdoptedItems"/>)
    /// or that the app manages (<paramref name="appManaged"/>, from the running installer's
    /// configuration, for manifests written before it was recorded), but only those that still
    /// start a program inside <paramref name="installPath"/>. A value pointing elsewhere belongs to
    /// another copy of the app and is left alone. Never throws for a registry failure.
    /// </summary>
    public static async Task RemoveAdoptedItemsAsync(InstalledManifest manifest, string installPath, IReadOnlyList<string> appManaged,
        IPlatformServices platform, IInstellaLogger log, CancellationToken ct)
    {
        var items = new List<ManifestAdoptedItem>(manifest.AdoptedItems ?? []);
        foreach (var name in appManaged)
        {
            if (!items.Any(i => i.Kind == ManifestAdoptedItem.RunValue && i.PerUser == manifest.InstalledPerUser
                                && string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)))
                items.Add(new ManifestAdoptedItem(ManifestAdoptedItem.RunValue, name, manifest.InstalledPerUser, ManifestAdoptedItem.AppManaged));
        }

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            if (item.Kind != ManifestAdoptedItem.RunValue)
            {
                log.Info($"uninstall: adopted item '{item.Name}' has kind '{item.Kind}', which this installer does not know; skipped");
                continue;
            }

            var command = RunValues.AsCommand(await RunValues.ReadAsync(platform, item.Name, item.PerUser, ct));
            if (command is null) continue;
            if (!RunValues.PointsInto(command, installPath))
            {
                log.Info($"uninstall: Run value '{item.Name}' left in place: it runs '{command}', outside '{installPath}'");
                continue;
            }

            var deleted = await platform.DeleteRegistryValueAsync(RunValues.Hive(item.PerUser), RunCommand.RunKey, item.Name, item.PerUser, ct);
            if (deleted.Success)
                log.Info($"uninstall: removed Run value '{item.Name}' ({item.Source})");
            else
                log.Warn($"uninstall: could not remove Run value '{item.Name}': {deleted.Error}");
        }
    }
}
