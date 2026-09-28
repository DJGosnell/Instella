using System;
using System.Collections.Generic;
using System.Linq;
using Instella.Core.Installation;
using Instella.Installer.Runtime.Installation;
using Instella.Installer.Runtime.Installation.Builders;
using BuiltIn = Instella.Installer.Runtime.Installation.BuiltIn;

namespace Instella.Installer.Runtime.Builders;

/// <summary>
/// Topological sort that interleaves built-in steps with user-contributed
/// <see cref="StepSpec"/> entries, honoring the <c>Before</c> / <c>After</c>
/// ordering hints recorded on each spec. Cycles throw.
/// </summary>
/// <remarks>
/// The algorithm runs in two stages. First, user specs are slotted by
/// <see cref="IInstallStep.Stage"/> into the corresponding bucket of the
/// built-in list. Then within each bucket a Kahn-style topological sort is
/// applied using the spec's <c>BeforeStep</c> / <c>AfterStep</c> hints; stage
/// hints produce edges between bucket boundaries. Built-in ordering is
/// treated as a hard constraint (e.g., <c>stage-uninstaller-stub</c> must
/// precede <c>register-uninstall-entry</c>) and encoded as implicit
/// after-anchors on the follower.
/// </remarks>
internal static class StepOrdering
{
    /// <summary>
    /// Merge built-in steps with user <see cref="StepSpec"/>s in a single
    /// linear order suitable for <see cref="StepExecutor"/>. Built-ins are
    /// defined by <see cref="OfflineInstallRunner.BuildDefaultSteps"/>. Install migrations are
    /// then placed by <see cref="Migrations.MigrationPipeline.Insert"/>.
    /// </summary>
    public static IReadOnlyList<IInstallStepExecution> BuildOrderedSteps(
        IReadOnlyList<IInstallStepExecution> builtIn,
        IReadOnlyList<StepSpec> userSteps,
        IReadOnlyList<Migrations.InstallMigration> migrations) =>
        Migrations.MigrationPipeline.Insert(BuildOrderedSteps(builtIn, userSteps), migrations);

    /// <summary>Built-in and user steps only (no migrations).</summary>
    public static IReadOnlyList<IInstallStepExecution> BuildOrderedSteps(
        IReadOnlyList<IInstallStepExecution> builtIn,
        IReadOnlyList<StepSpec> userSteps)
    {
        ArgumentNullException.ThrowIfNull(builtIn);
        ArgumentNullException.ThrowIfNull(userSteps);

        if (userSteps.Count == 0)
            return builtIn;

        var nodes = new List<IInstallStepExecution>(builtIn.Count + userSteps.Count);
        nodes.AddRange(builtIn);
        nodes.AddRange(userSteps);

        // Group by stage and sort within stage via topological sort that
        // respects the hints recorded on user specs.
        var perStage = new Dictionary<InstallStage, List<IInstallStepExecution>>();
        foreach (var s in nodes)
        {
            if (!perStage.TryGetValue(s.Stage, out var list))
                perStage[s.Stage] = list = new List<IInstallStepExecution>();
            list.Add(s);
        }

        var ordered = new List<IInstallStepExecution>(nodes.Count);
        foreach (InstallStage stage in Enum.GetValues<InstallStage>())
        {
            if (!perStage.TryGetValue(stage, out var list))
                continue;
            ordered.AddRange(TopologicalSort(list, nodes));
        }
        return ordered;
    }

    private static List<IInstallStepExecution> TopologicalSort(
        List<IInstallStepExecution> stageNodes,
        List<IInstallStepExecution> allNodes)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < stageNodes.Count; i++)
            index[stageNodes[i].Name] = i;

        // Adjacency list: edges[i] = list of j such that i must run before j.
        var edges = new List<HashSet<int>>(stageNodes.Count);
        for (var i = 0; i < stageNodes.Count; i++)
            edges.Add(new HashSet<int>());

        // Preserve the authoring order of built-in steps as a weak constraint
        // — edges i -> i+1 among consecutive built-ins only. User specs have
        // no intrinsic position, so we skip weak edges into or out of them;
        // their <c>Before</c> / <c>After</c> hints are the sole source of
        // constraints and free the sort to position them at the earliest
        // feasible slot.
        for (var i = 0; i < stageNodes.Count - 1; i++)
        {
            if (stageNodes[i] is StepSpec || stageNodes[i + 1] is StepSpec)
                continue;
            edges[i].Add(i + 1);
        }

        // The install transaction commits at the start of Finalize. User Finalize steps see
        // the committed files, and a point-of-no-return step must never run before the
        // commit: rollback stops at it, so the installed manifest has to be committed
        // by then. Any other user step can still opt in to running earlier with an explicit
        // Before("write-manifest") / Before("commit-transaction").
        if (index.TryGetValue(BuiltIn.CommitTransactionStep.StepName, out var commit))
        {
            for (var i = 0; i < stageNodes.Count; i++)
            {
                if (stageNodes[i] is not StepSpec spec) continue;
                var explicitBefore = spec.OrderingHints.Any(h => h.Kind == StepOrderingHintKind.BeforeStep
                    && h.StepName is "write-manifest" or BuiltIn.CommitTransactionStep.StepName);
                if (spec.IsPointOfNoReturn || !explicitBefore)
                    edges[commit].Add(i);
            }
        }

        // The app's upgrade program runs right after the commit, before every user Finalize step:
        // its failure must be able to roll the commit back, so no point of no return may come
        // first, and custom Finalize steps should see the upgraded data. A step that is not a point
        // of no return can still opt in to running earlier with Before("app-upgrade").
        if (index.TryGetValue(BuiltIn.AppUpgradeStep.StepName, out var upgrade))
        {
            for (var i = 0; i < stageNodes.Count; i++)
            {
                if (stageNodes[i] is not StepSpec spec) continue;
                var explicitBefore = spec.OrderingHints.Any(h => h.Kind == StepOrderingHintKind.BeforeStep
                    && h.StepName is "write-manifest" or BuiltIn.CommitTransactionStep.StepName or BuiltIn.AppUpgradeStep.StepName);
                if (spec.IsPointOfNoReturn || !explicitBefore)
                    edges[upgrade].Add(i);
            }
        }

        // Layer explicit user hints on top. Because user specs do not
        // participate in the weak sequential edges, there is no reverse edge
        // to remove here — conflicting user hints (a.After(b) + b.After(a))
        // resolve as real cycles that Kahn's algorithm rejects below.
        for (var i = 0; i < stageNodes.Count; i++)
        {
            if (stageNodes[i] is not StepSpec spec) continue;
            foreach (var hint in spec.OrderingHints)
            {
                switch (hint.Kind)
                {
                    case StepOrderingHintKind.BeforeStep:
                        if (hint.StepName is { } bn && index.TryGetValue(bn, out var bj))
                            edges[i].Add(bj);
                        break;
                    case StepOrderingHintKind.AfterStep:
                        if (hint.StepName is { } an && index.TryGetValue(an, out var aj))
                            edges[aj].Add(i);
                        break;
                    case StepOrderingHintKind.BeforeStage:
                    case StepOrderingHintKind.AfterStage:
                        // Stage hints are cross-stage; they've already been
                        // used to slot this spec into its bucket. No intra-
                        // stage effect.
                        break;
                }
            }
        }

        // Kahn's algorithm.
        var indegree = new int[stageNodes.Count];
        foreach (var edgeSet in edges)
            foreach (var target in edgeSet)
                indegree[target]++;

        var ready = new Queue<int>();
        for (var i = 0; i < indegree.Length; i++)
            if (indegree[i] == 0)
                ready.Enqueue(i);

        var sorted = new List<IInstallStepExecution>(stageNodes.Count);
        while (ready.Count > 0)
        {
            var n = ready.Dequeue();
            sorted.Add(stageNodes[n]);
            foreach (var m in edges[n])
            {
                if (--indegree[m] == 0)
                    ready.Enqueue(m);
            }
        }

        if (sorted.Count != stageNodes.Count)
            throw new InvalidOperationException(
                $"step ordering cycle detected among steps in stage {stageNodes[0].Stage}");

        return sorted;
    }
}
