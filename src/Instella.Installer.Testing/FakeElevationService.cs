using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Instella.Installer.Testing;

/// <summary>
/// Elevation for <see cref="InstellaTestHarness.RunFullAsync"/>: never shows a UAC prompt and
/// never starts a process. It records each elevated relaunch the installer asks for and answers
/// it with <see cref="RelaunchExitCode"/>, as if the elevated copy had run and exited.
/// </summary>
public sealed class FakeElevationService : Instella.Installer.Runtime.Core.Elevation.IElevationService
{
    private readonly List<IReadOnlyList<string>> _requests = [];

    /// <summary>Creates the fake.</summary>
    /// <param name="isElevated">Whether the installer runs as administrator.</param>
    /// <param name="relaunchExitCode">The elevated copy's exit code; null means the prompt was declined.</param>
    public FakeElevationService(bool isElevated = false, int? relaunchExitCode = 0)
    {
        IsElevated = isElevated;
        RelaunchExitCode = relaunchExitCode;
    }

    /// <summary>Whether the installer runs as administrator.</summary>
    public bool IsElevated { get; }

    /// <summary>What a relaunch returns: the elevated copy's exit code, or null for a declined prompt.</summary>
    public int? RelaunchExitCode { get; }

    /// <summary>The arguments of each elevated relaunch the installer asked for, in order.</summary>
    public IReadOnlyList<IReadOnlyList<string>> RelaunchRequests => _requests;

    /// <summary>Records the request and returns <see cref="RelaunchExitCode"/>.</summary>
    public Task<int?> RelaunchElevatedAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        lock (_requests) _requests.Add([.. args]);
        return Task.FromResult(RelaunchExitCode);
    }
}
