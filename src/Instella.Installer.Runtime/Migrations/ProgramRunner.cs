using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Instella.Installer.Runtime.Core.Processes;

namespace Instella.Installer.Runtime.Migrations;

/// <summary>
/// Starts a program and waits for its exit code; the seam behind <c>RunProgramAsync</c> and the
/// app's upgrade program, so tests never start real processes.
/// </summary>
internal interface IProgramRunner
{
    /// <summary>Runs <paramref name="exePath"/> with <paramref name="arguments"/> (no shell) and returns its exit code.</summary>
    /// <exception cref="TimeoutException">It ran longer than <paramref name="timeout"/>; it was ended.</exception>
    Task<int> RunAsync(string exePath, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct);

    /// <summary>
    /// Runs <paramref name="start"/> headless with stdin closed, passing every stdout and stderr line
    /// to <paramref name="onLine"/> (one call at a time, in order within each stream).
    /// </summary>
    /// <exception cref="TimeoutException">It ran longer than <paramref name="timeout"/>; its process tree was ended.</exception>
    /// <exception cref="ProgramStartException">It could not be started.</exception>
    Task<ProgramExit> RunCapturedAsync(ProgramStart start, Action<ProgramStream, string> onLine, TimeSpan timeout, CancellationToken ct);
}

/// <summary>What to start for <see cref="IProgramRunner.RunCapturedAsync"/>.</summary>
/// <param name="ExePath">The program's full path.</param>
/// <param name="Arguments">Passed as a list, never through a shell.</param>
/// <param name="WorkingDirectory">The working directory.</param>
/// <param name="Environment">Added to (or replacing values in) the inherited environment.</param>
internal sealed record ProgramStart(
    string ExePath, IReadOnlyList<string> Arguments, string WorkingDirectory, IReadOnlyDictionary<string, string> Environment);

/// <summary>Which output stream a line came from.</summary>
internal enum ProgramStream
{
    /// <summary>stdout.</summary>
    Output,
    /// <summary>stderr.</summary>
    Error,
}

/// <summary>How a captured program ended.</summary>
/// <param name="ExitCode">Its exit code.</param>
/// <param name="Notes">Things the caller should log, for example that the program could not be tied to this process's lifetime.</param>
internal sealed record ProgramExit(int ExitCode, IReadOnlyList<string> Notes);

/// <summary>A program could not be started (missing, not executable, access denied).</summary>
internal sealed class ProgramStartException(string exePath, string reason, Exception? inner = null)
    : Exception($"'{exePath}' could not be started: {reason}", inner)
{
    /// <summary>Why, as the operating system said it.</summary>
    public string Reason { get; } = reason;
}

/// <summary>Real processes: no shell, arguments passed as a list, the process tree ended on timeout or cancel.</summary>
internal sealed class ProcessProgramRunner : IProgramRunner
{
    public static readonly ProcessProgramRunner Instance = new();

    /// <summary>How long to wait for a killed tree to exit, and for its output to drain after exit.</summary>
    internal static TimeSpan DrainTimeout { get; set; } = TimeSpan.FromSeconds(10);

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

    public async Task<ProgramExit> RunCapturedAsync(
        ProgramStart start, Action<ProgramStream, string> onLine, TimeSpan timeout, CancellationToken ct)
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var psi = new ProcessStartInfo(start.ExePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = start.WorkingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = utf8,
            StandardErrorEncoding = utf8,
        };
        foreach (var a in start.Arguments) psi.ArgumentList.Add(a);
        foreach (var (name, value) in start.Environment) psi.Environment[name] = value;

        if (!File.Exists(start.ExePath))
            throw new ProgramStartException(start.ExePath, "the file does not exist");

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new ProgramStartException(start.ExePath, "no process was created");
        }
        catch (Win32Exception ex)
        {
            throw new ProgramStartException(start.ExePath, ex.Message, ex);
        }

        var notes = new List<string>();
        using (process)
        {
            // Tie the program to this process: if the installer or updater dies, recovery rolls the
            // files back, and the program must not keep changing the app's data after that.
            IDisposable? job = null;
            if (OperatingSystem.IsWindows())
            {
                job = ChildProcessJob.TryAssign(process, out var jobProblem);
                if (job is null) notes.Add($"the program is not ended if this process dies ({jobProblem})");
            }
            using var jobLifetime = job;

            try { process.StandardInput.Close(); } catch (IOException) { /* it exited already */ }

            var gate = new object();
            void Deliver(ProgramStream stream, string line) { lock (gate) onLine(stream, line); }
            var stdout = PumpAsync(process.StandardOutput, ProgramStream.Output, Deliver);
            var stderr = PumpAsync(process.StandardError, ProgramStream.Error, Deliver);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already exited */ }
                using (var wait = new CancellationTokenSource(DrainTimeout))
                {
                    try { await process.WaitForExitAsync(wait.Token); } catch (OperationCanceledException) { /* reported below */ }
                }
                await DrainAsync(stdout, stderr);
                if (ct.IsCancellationRequested) throw;
                throw new TimeoutException(
                    $"'{Path.GetFileName(start.ExePath)}' did not finish within {Minutes(timeout)}; it was ended");
            }

            // A grandchild that inherited the pipes can keep them open after the program exits:
            // wait a little for the rest of the output, never forever.
            if (!await DrainAsync(stdout, stderr))
                notes.Add("some output was not read: a process the program started still holds its output");
            return new ProgramExit(process.ExitCode, notes);
        }
    }

    private static string Minutes(TimeSpan t) =>
        t.TotalMinutes >= 1 ? $"{t.TotalMinutes:0} minute(s)" : $"{t.TotalSeconds:0.#} second(s)";

    private static async Task<bool> DrainAsync(Task stdout, Task stderr)
    {
        var both = Task.WhenAll(stdout, stderr);
        return await Task.WhenAny(both, Task.Delay(DrainTimeout)) == both;
    }

    private static async Task PumpAsync(StreamReader reader, ProgramStream stream, Action<ProgramStream, string> deliver)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
                deliver(stream, line);
        }
        catch (IOException)
        {
            // The pipe broke when the tree was ended.
        }
        catch (ObjectDisposedException)
        {
            // The process was disposed while a grandchild still held the pipe.
        }
    }
}
