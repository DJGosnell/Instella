using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Platform.Windows;

namespace Instella.Installer.Runtime.Core.Elevation;

/// <summary>Detects elevation and relaunches the running installer elevated.</summary>
internal interface IElevationService
{
    /// <summary>True when the process already has administrator rights.</summary>
    bool IsElevated { get; }

    /// <summary>
    /// Relaunches the current executable elevated with <paramref name="args"/>, waits for it,
    /// and returns its exit code, or null when the user declined the UAC prompt.
    /// </summary>
    Task<int?> RelaunchElevatedAsync(IReadOnlyList<string> args, CancellationToken ct);
}

/// <summary>
/// UAC relaunch through the <c>runas</c> verb. The elevated child re-reads its payload from
/// its own .exe, which is safe even in a user-writable folder: Windows denies writes to an
/// image file while it is mapped by a running process, and the footer hash is verified as usual.
/// </summary>
internal sealed class WindowsElevationService : IElevationService
{
    private const int ErrorCancelled = 1223; // the user answered No

    public bool IsElevated => Environment.IsPrivilegedProcess;

    public async Task<int?> RelaunchElevatedAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("cannot determine the installer path"))
        {
            UseShellExecute = true, // required for the "runas" verb
            Verb = "runas",
            Arguments = WindowsCommandLine.Join(args),
            WorkingDirectory = Environment.CurrentDirectory,
        };
        try
        {
            using var child = Process.Start(psi) ?? throw new InvalidOperationException("the elevated installer did not start");
            await child.WaitForExitAsync(ct);
            return child.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return null;
        }
    }
}

/// <summary>Non-Windows hosts: elevation is not implemented (experimental platforms).</summary>
internal sealed class NoElevationService : IElevationService
{
    public bool IsElevated => Environment.IsPrivilegedProcess;

    public Task<int?> RelaunchElevatedAsync(IReadOnlyList<string> args, CancellationToken ct) =>
        Task.FromResult<int?>(null);
}
