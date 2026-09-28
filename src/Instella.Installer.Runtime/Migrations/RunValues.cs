using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Platform;
using Instella.Installer.Runtime.Core;

namespace Instella.Installer.Runtime.Migrations;

/// <summary>The scope's Run key: HKCU for a per-user install, HKLM for a machine-wide one.</summary>
internal static class RunValues
{
    public static RegistryHive Hive(bool perUser) => perUser ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;

    /// <summary>The value (null when it does not exist; always null off Windows), or why it could not be read.</summary>
    public static Task<RegistryReadResult> ReadAsync(IPlatformServices platform, string name, bool perUser, CancellationToken ct) =>
        platform.TryReadRegistryValueAsync(Hive(perUser), RunCommand.RunKey, name, perUser, ct);

    /// <summary>The value as a command line, or null when it is missing or not a string.</summary>
    public static string? AsCommand(RegistryValueData? data) =>
        data?.Value as string;

    /// <summary>Whether <paramref name="command"/> starts an executable inside <paramref name="folder"/>.</summary>
    public static bool PointsInto(string? command, string folder) =>
        RunCommand.TryGetExecutable(command, out var exe, out _) && PathGuards.IsSameOrInside(exe, folder);
}
