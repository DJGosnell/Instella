using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Instella.Installer.Runtime.Migrations;

/// <summary>Starts a program and waits for its exit code; the seam behind <c>RunProgramAsync</c>.</summary>
internal interface IProgramRunner
{
    /// <summary>Runs <paramref name="exePath"/> with <paramref name="arguments"/> (no shell) and returns its exit code.</summary>
    /// <exception cref="TimeoutException">It ran longer than <paramref name="timeout"/>; it was ended.</exception>
    Task<int> RunAsync(string exePath, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct);
}

/// <summary>Real processes: no shell, arguments passed as a list, the process tree ended on timeout or cancel.</summary>
internal sealed class ProcessProgramRunner : IProgramRunner
{
    public static readonly ProcessProgramRunner Instance = new();

    public async Task<int> RunAsync(string exePath, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? "",
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"could not start '{exePath}'");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already exited */ }
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException($"'{Path.GetFileName(exePath)}' did not finish within {timeout.TotalMinutes:0} minutes; it was ended");
        }
    }
}
