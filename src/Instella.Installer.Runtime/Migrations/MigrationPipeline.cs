using System.Collections.Generic;
using System.Linq;
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
}
