using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Instella.Installer.Runtime.Migrations;

/// <summary>The migration actions, with the safety rules each enforces.</summary>
internal static class MigrationActions
{
    private static NotSupportedException Pending() => new("migration actions arrive with the migration engine");

    public static Task StopProcessesInAsync(MigrationRun run, MigrationFolder folder, CancellationToken ct) => throw Pending();

    public static Task<bool> RepointRunValueAsync(MigrationRun run, string name, MigrationFolder from, CancellationToken ct) => throw Pending();

    public static Task<bool> DeleteRunValueAsync(MigrationRun run, string name, MigrationFolder pointingInto, CancellationToken ct) => throw Pending();

    public static Task<bool> AdoptRunValueAsync(MigrationRun run, string name, CancellationToken ct) => throw Pending();

    public static Task<int> DeleteFilesAsync(MigrationRun run, MigrationFolder folder, IReadOnlyList<string> fileNames, CancellationToken ct) => throw Pending();

    public static Task<bool> DeleteFolderIfEmptyAsync(MigrationRun run, MigrationFolder folder, CancellationToken ct) => throw Pending();

    public static Task<int> RunProgramAsync(MigrationRun run, string exePath, IReadOnlyList<string> arguments, IReadOnlyList<int> successExitCodes, CancellationToken ct) => throw Pending();
}
