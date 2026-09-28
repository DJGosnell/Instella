using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.FileSystem;
using Instella.Core.Installation;
using Instella.Core.Platform;
using Instella.Installer.Runtime.Core.Processes;

namespace Instella.Installer.Runtime.Migrations;

/// <summary>
/// When a migration applies. Build one from the condition methods of <see cref="InstallMigration"/>
/// and combine them with <c>&amp;</c>, <c>|</c> and <c>!</c>. A condition is data: it is evaluated
/// against the machine only when the migration's turn comes, and a false one logs which part was false.
/// </summary>
public abstract class Condition
{
    private protected Condition()
    {
    }

    /// <summary>Both must hold; the right side is not evaluated when the left is false.</summary>
    public static Condition operator &(Condition left, Condition right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return new AndCondition(left, right);
    }

    /// <summary>Either must hold; the right side is not evaluated when the left is true.</summary>
    public static Condition operator |(Condition left, Condition right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return new OrCondition(left, right);
    }

    /// <summary>The operand must not hold.</summary>
    public static Condition operator !(Condition operand)
    {
        ArgumentNullException.ThrowIfNull(operand);
        return new NotCondition(operand);
    }

    /// <summary>
    /// A custom condition. It runs outside the safety rules, so it should only read. A predicate
    /// that throws skips the migration (with a warning) whatever <c>&amp;</c>, <c>|</c> or <c>!</c>
    /// surround it: a check that never completed is never read as true.
    /// </summary>
    /// <param name="predicate">Evaluated when the migration's turn comes.</param>
    /// <param name="description">Shown in logs; defaults to <c>custom condition</c>.</param>
    public static Condition From(Func<MigrationContext, bool> predicate, string? description = null)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return new DelegateCondition(predicate, string.IsNullOrWhiteSpace(description) ? "custom condition" : description);
    }

    /// <summary>Always true, for migrations that run whenever their timing comes (typically uninstall migrations).</summary>
    public static Condition Always { get; } = new AlwaysCondition();

    /// <summary>Evaluates the condition. False results carry the reason for the skip log.</summary>
    internal abstract ValueTask<ConditionOutcome> EvaluateAsync(MigrationContext context, CancellationToken ct);

    /// <summary>The modes this condition can be true in (for the Build() check); everything unless it tests the mode.</summary>
    internal virtual ModeSet PossibleModes => ModeSet.All;

    /// <summary>The modes <c>!this</c> can be true in.</summary>
    internal virtual ModeSet PossibleModesWhenNegated => ModeSet.All;
}

/// <summary>A part of a condition could not be evaluated; the migration is skipped, never run.</summary>
internal sealed class ConditionEvaluationException(string condition, Exception inner)
    : Exception($"{condition} threw {inner.GetType().Name}: {inner.Message}", inner);

/// <summary>A condition's value; <see cref="Reason"/> says why it is false (or, under <c>!</c>, why it was true).</summary>
internal readonly record struct ConditionOutcome(bool Value, string? Reason)
{
    public static ConditionOutcome True(string description) => new(true, description);
    public static ConditionOutcome False(string reason) => new(false, reason);
}

internal sealed class AlwaysCondition : Condition
{
    internal override ValueTask<ConditionOutcome> EvaluateAsync(MigrationContext context, CancellationToken ct) =>
        ValueTask.FromResult(ConditionOutcome.True("Always"));

    public override string ToString() => "Always";
}

internal sealed class AndCondition(Condition left, Condition right) : Condition
{
    internal override async ValueTask<ConditionOutcome> EvaluateAsync(MigrationContext context, CancellationToken ct)
    {
        var l = await left.EvaluateAsync(context, ct);
        if (!l.Value) return l;
        var r = await right.EvaluateAsync(context, ct);
        return r.Value ? ConditionOutcome.True(ToString()) : r;
    }

    internal override ModeSet PossibleModes => left.PossibleModes & right.PossibleModes;

    public override string ToString() => $"{Wrap(left)} & {Wrap(right)}";

    private static string Wrap(Condition c) => c is OrCondition ? $"({c})" : c.ToString()!;
}

internal sealed class OrCondition(Condition left, Condition right) : Condition
{
    internal override async ValueTask<ConditionOutcome> EvaluateAsync(MigrationContext context, CancellationToken ct)
    {
        var l = await left.EvaluateAsync(context, ct);
        if (l.Value) return l;
        var r = await right.EvaluateAsync(context, ct);
        return r.Value ? r : ConditionOutcome.False($"neither ({l.Reason}) nor ({r.Reason})");
    }

    internal override ModeSet PossibleModes => left.PossibleModes | right.PossibleModes;

    public override string ToString() => $"{left} | {right}";
}

internal sealed class NotCondition(Condition operand) : Condition
{
    internal override async ValueTask<ConditionOutcome> EvaluateAsync(MigrationContext context, CancellationToken ct)
    {
        var inner = await operand.EvaluateAsync(context, ct);
        return inner.Value
            ? ConditionOutcome.False($"{operand} is true")
            : ConditionOutcome.True(ToString());
    }

    internal override ModeSet PossibleModes => operand.PossibleModesWhenNegated;

    public override string ToString() => operand is AndCondition or OrCondition ? $"!({operand})" : $"!{operand}";
}

internal sealed class ModeCondition(ModeSet modes, string name) : Condition
{
    internal override ValueTask<ConditionOutcome> EvaluateAsync(MigrationContext context, CancellationToken ct) =>
        ValueTask.FromResult((modes & ModeSets.Of(context.Mode)) != 0
            ? ConditionOutcome.True(ToString())
            : ConditionOutcome.False($"{name} is false (the mode is {context.Mode})"));

    internal override ModeSet PossibleModes => modes;

    internal override ModeSet PossibleModesWhenNegated => ModeSet.All & ~modes;

    public override string ToString() => name;
}

internal sealed class VersionRangeCondition(VersionRange range) : Condition
{
    internal override ValueTask<ConditionOutcome> EvaluateAsync(MigrationContext context, CancellationToken ct)
    {
        if (context.Mode != InstallerMode.Upgrade || context.PreviousVersion is not { } previous)
            return ValueTask.FromResult(ConditionOutcome.False($"{this} is false (not an upgrade)"));
        return ValueTask.FromResult(range.Contains(previous)
            ? ConditionOutcome.True(ToString())
            : ConditionOutcome.False($"{this} is false (upgrading from {previous})"));
    }

    internal override ModeSet PossibleModes => ModeSet.Upgrade;

    public override string ToString() => $"UpgradingFrom({range})";
}

internal enum PathTest
{
    FileExists,
    FolderExists,
    InstellaInstallation,
}

internal sealed class PathCondition(KnownFolder root, string relative, PathTest test) : Condition
{
    internal override ValueTask<ConditionOutcome> EvaluateAsync(MigrationContext context, CancellationToken ct)
    {
        var resolved = MigrationFolderGuard.Resolve(root, relative, context, forAction: false);
        if (resolved.Path is not { } path)
            return ValueTask.FromResult(ConditionOutcome.False($"{this} is false: {resolved.Refusal}"));
        var fs = context.FileSystem;
        var value = test switch
        {
            PathTest.FileExists => fs.Exists(path),
            PathTest.FolderExists => fs.DirectoryExists(path),
            _ => fs.Exists(Path.Combine(path, InstellaOwnedPaths.InstalledManifest)),
        };
        return ValueTask.FromResult(value ? ConditionOutcome.True(ToString()) : ConditionOutcome.False($"{this} is false"));
    }

    public override string ToString() => test switch
    {
        PathTest.FileExists => $"FileExists({root}/{relative})",
        PathTest.FolderExists => $"FolderExists({root}/{relative})",
        _ => $"InstellaInstallationAt({root}/{relative})",
    };
}

internal sealed class RunValueCondition(string name, MigrationFolder? pointsInto) : Condition
{
    internal override async ValueTask<ConditionOutcome> EvaluateAsync(MigrationContext context, CancellationToken ct)
    {
        var command = RunValues.AsCommand(await RunValues.ReadAsync(context.PlatformServices, name, context.PerUser, ct));
        if (command is null)
            return ConditionOutcome.False($"{this} is false (no Run value '{name}')");
        if (pointsInto is null) return ConditionOutcome.True(ToString());

        var folder = MigrationFolderGuard.Resolve(pointsInto, context, forAction: false);
        if (folder.Path is not { } path)
            return ConditionOutcome.False($"{this} is false: {folder.Refusal}");
        return RunValues.PointsInto(command, path)
            ? ConditionOutcome.True(ToString())
            : ConditionOutcome.False($"{this} is false (it runs '{command}')");
    }

    public override string ToString() => pointsInto is null ? $"RunValueExists({name})" : $"RunValuePointsInto({name}, {pointsInto})";
}

internal sealed class RegistryValueCondition(RegistryHive hive, string keyPath, string name) : Condition
{
    internal override async ValueTask<ConditionOutcome> EvaluateAsync(MigrationContext context, CancellationToken ct)
    {
        if (hive == RegistryHive.CurrentUser && !context.PerUser)
            return ConditionOutcome.False($"{this} is false: HKCU belongs to the account running a machine-wide install, which may not be the user's");
        var effective = hive == RegistryHive.AutoFromScope ? RunValues.Hive(context.PerUser) : hive;
        var value = await context.PlatformServices.ReadRegistryValueAsync(effective, keyPath, name, context.PerUser, ct);
        return value is not null ? ConditionOutcome.True(ToString()) : ConditionOutcome.False($"{this} is false");
    }

    public override string ToString() => $"RegistryValueExists({hive}\\{keyPath}\\{name})";
}

internal sealed class ProcessCondition(MigrationFolder folder) : Condition
{
    internal override ValueTask<ConditionOutcome> EvaluateAsync(MigrationContext context, CancellationToken ct)
    {
        var resolved = MigrationFolderGuard.Resolve(folder, context, forAction: false);
        if (resolved.Path is not { } path)
            return ValueTask.FromResult(ConditionOutcome.False($"{this} is false: {resolved.Refusal}"));
        var gate = new RunningAppGate(context.PlatformServices, context.Log, context.Runtime.ProcessFinder, context.FileSystem);
        var running = gate.FindBlockers(path, executableName: null).Where(p => p.CanClose).ToList();
        return ValueTask.FromResult(running.Count > 0
            ? ConditionOutcome.True($"{this} ({string.Join(", ", running)})")
            : ConditionOutcome.False($"{this} is false"));
    }

    public override string ToString() => $"ProcessRunningIn({folder})";
}

internal sealed class PlatformCondition(TargetPlatform platform) : Condition
{
    internal override ValueTask<ConditionOutcome> EvaluateAsync(MigrationContext context, CancellationToken ct) =>
        ValueTask.FromResult(context.Platform == platform
            ? ConditionOutcome.True(ToString())
            : ConditionOutcome.False($"{this} is false (running on {context.Platform})"));

    public override string ToString() => $"Is{platform}()";
}

internal sealed class ScopeCondition(InstallationScope scope) : Condition
{
    internal override ValueTask<ConditionOutcome> EvaluateAsync(MigrationContext context, CancellationToken ct) =>
        ValueTask.FromResult(context.Scope == scope
            ? ConditionOutcome.True(ToString())
            : ConditionOutcome.False($"{this} is false (the install is {context.Scope})"));

    public override string ToString() => scope == InstallationScope.PerUser ? "IsPerUserInstall()" : "IsMachineInstall()";
}

/// <remarks>
/// A predicate that throws is not "false": under <c>!</c> that would turn a check that never ran
/// into "true" and let the migration act. The exception propagates, and the migration is skipped
/// (<see cref="MigrationExecution"/>) whatever operators surround the condition.
/// </remarks>
internal sealed class DelegateCondition(Func<MigrationContext, bool> predicate, string description) : Condition
{
    internal override ValueTask<ConditionOutcome> EvaluateAsync(MigrationContext context, CancellationToken ct)
    {
        try
        {
            return ValueTask.FromResult(predicate(context)
                ? ConditionOutcome.True(description)
                : ConditionOutcome.False($"{description} is false"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ConditionEvaluationException(description, ex);
        }
    }

    public override string ToString() => description;
}
