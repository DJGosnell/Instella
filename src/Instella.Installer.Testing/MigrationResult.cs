using System.Globalization;
using Instella.Core.Installation;
using Instella.Core.Platform;
using Instella.Installer.Runtime.Migrations;

namespace Instella.Installer.Testing;

/// <summary>What happened to the migration a <see cref="MigrationHarness"/> ran.</summary>
public enum MigrationOutcome
{
    /// <summary>Its condition held and its body finished.</summary>
    Completed,

    /// <summary>Its condition was false (<see cref="MigrationResult.SkipReason"/> says which part).</summary>
    Skipped,

    /// <summary>The installation had already completed it (<see cref="MigrationHarness.AlreadyCompleted"/>).</summary>
    AlreadyCompleted,

    /// <summary>It threw or an action failed; for an AfterCommit or uninstall migration the install goes on.</summary>
    Failed,

    /// <summary>A BeforeCommit migration that failed, or was undone because a later step failed.</summary>
    RolledBack,
}

/// <summary>
/// A registry value the migration changed: <see cref="Before"/> is null for a value it created,
/// <see cref="After"/> null for one it deleted.
/// </summary>
/// <param name="Hive">The hive.</param>
/// <param name="KeyPath">The key.</param>
/// <param name="Name">The value's name.</param>
/// <param name="Before">The data before the run, or null.</param>
/// <param name="After">The data after the run, or null.</param>
public sealed record MigrationRegistryChange(RegistryHive Hive, string KeyPath, string Name, string? Before, string? After);

/// <summary>The outcome of <see cref="MigrationHarness.RunAsync"/>, and the state it left, for assertions.</summary>
public sealed class MigrationResult
{
    private readonly InMemoryFileSystem _fs;
    private readonly FakePlatformServices _platform;
    private readonly Func<KnownFolder, string, string> _pathOf;
    private readonly RegistryHive _runHive;

    internal MigrationResult(
        MigrationRunRecord record,
        bool recorded,
        IReadOnlyList<ManifestAdoptedItem> adopted,
        IReadOnlyList<MigrationRegistryChange> registryChanges,
        IReadOnlyList<string> programsRun,
        IReadOnlyList<string> logLines,
        InMemoryFileSystem fs,
        FakePlatformServices platform,
        Func<KnownFolder, string, string> pathOf,
        RegistryHive runHive)
    {
        Outcome = record.Outcome switch
        {
            MigrationRunOutcome.Completed => MigrationOutcome.Completed,
            MigrationRunOutcome.AlreadyCompleted => MigrationOutcome.AlreadyCompleted,
            MigrationRunOutcome.Failed => MigrationOutcome.Failed,
            MigrationRunOutcome.RolledBack => MigrationOutcome.RolledBack,
            _ => MigrationOutcome.Skipped,
        };
        SkipReason = Outcome is MigrationOutcome.Skipped or MigrationOutcome.AlreadyCompleted ? record.Reason : null;
        Error = Outcome is MigrationOutcome.Failed or MigrationOutcome.RolledBack ? record.Reason : null;
        RecordedAsCompleted = recorded;
        var done = record.Actions.Where(a => !a.Preview).ToList();
        DeletedFiles = done.Where(a => a.Kind == "delete-file").Select(a => a.Target).ToList();
        StoppedProcesses = done.Where(a => a.Kind == "stop-process").Select(a => a.Target).ToList();
        PlannedActions = record.Actions.Where(a => a.Preview).Select(a => a.ToString()).ToList();
        Actions = record.Actions.Select(a => a.ToString()).ToList();
        AdoptedItems = adopted;
        RegistryChanges = registryChanges;
        ProgramsRun = programsRun;
        LogLines = logLines;
        _fs = fs;
        _platform = platform;
        _pathOf = pathOf;
        _runHive = runHive;
    }

    /// <summary>What happened.</summary>
    public MigrationOutcome Outcome { get; }

    /// <summary>Whether the body ran (completed, failed or rolled back).</summary>
    public bool Ran => Outcome is MigrationOutcome.Completed or MigrationOutcome.Failed or MigrationOutcome.RolledBack;

    /// <summary>Why it was skipped: the part of the condition that was false, or "already completed".</summary>
    public string? SkipReason { get; }

    /// <summary>Why it failed.</summary>
    public string? Error { get; }

    /// <summary>Whether its id would be recorded in the installed manifest (a run-once success outside preview).</summary>
    public bool RecordedAsCompleted { get; }

    /// <summary>Full paths of the files it deleted (kept in this list even if a rollback restored them).</summary>
    public IReadOnlyList<string> DeletedFiles { get; }

    /// <summary>The programs it closed.</summary>
    public IReadOnlyList<string> StoppedProcesses { get; }

    /// <summary>Registry values that differ after the run.</summary>
    public IReadOnlyList<MigrationRegistryChange> RegistryChanges { get; }

    /// <summary>Items it adopted for uninstall.</summary>
    public IReadOnlyList<ManifestAdoptedItem> AdoptedItems { get; }

    /// <summary>The programs it ran, as command lines.</summary>
    public IReadOnlyList<string> ProgramsRun { get; }

    /// <summary>In preview: what the actions would have done, e.g. <c>would delete-file C:\…\ExampleApp.exe</c>.</summary>
    public IReadOnlyList<string> PlannedActions { get; }

    /// <summary>Every action it took (or, in preview, would take), in order.</summary>
    public IReadOnlyList<string> Actions { get; }

    /// <summary>The log, one line per entry: <c>INFO migration[id]: …</c>.</summary>
    public IReadOnlyList<string> LogLines { get; }

    /// <summary>Whether the file exists after the run.</summary>
    public bool FileExists(KnownFolder root, string relative) => _fs.Exists(_pathOf(root, relative));

    /// <summary>Whether the folder exists after the run.</summary>
    public bool FolderExists(KnownFolder root, string relative) => _fs.DirectoryExists(_pathOf(root, relative));

    /// <summary>The Run value's command after the run (scope's Run key), or null.</summary>
    public string? RunValue(string name) => _platform.Registry.Get(_runHive, Instella.Installer.Runtime.Migrations.RunCommand.RunKey, name) as string;

    /// <summary>The full path of <paramref name="relative"/> under <paramref name="root"/> in the harness.</summary>
    public string PathOf(KnownFolder root, string relative) => _pathOf(root, relative);

    internal static IReadOnlyList<MigrationRegistryChange> Diff(IReadOnlyList<RegistryValueRecord> before, IReadOnlyList<RegistryValueRecord> after)
    {
        static string Key(RegistryValueRecord r) => $"{r.Hive}\\{r.KeyPath.ToUpperInvariant()}\\{r.ValueName.ToUpperInvariant()}";
        static string? Text(RegistryValueRecord? r) => r is { } v ? Convert.ToString(v.Value, CultureInfo.InvariantCulture) : null;
        var old = before.ToDictionary(Key);
        var now = after.ToDictionary(Key);
        var changes = new List<MigrationRegistryChange>();
        foreach (var key in old.Keys.Union(now.Keys).Order(StringComparer.Ordinal))
        {
            old.TryGetValue(key, out var b);
            now.TryGetValue(key, out var a);
            RegistryValueRecord? was = old.ContainsKey(key) ? b : null;
            RegistryValueRecord? isNow = now.ContainsKey(key) ? a : null;
            if (Text(was) == Text(isNow)) continue;
            var any = (was ?? isNow)!.Value;
            changes.Add(new MigrationRegistryChange(any.Hive, any.KeyPath, any.ValueName, Text(was), Text(isNow)));
        }
        return changes;
    }
}
