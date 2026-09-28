using Instella.Installer.Runtime.Migrations;

namespace Instella.Installer.Testing;

/// <summary>
/// A program <see cref="InstellaTestHarness.RunFullAsync"/> would have started: the app's upgrade
/// program (<c>instella-upgrade.json</c>) or a program an install migration runs.
/// </summary>
/// <param name="Path">The program's full path, inside the harness file system.</param>
/// <param name="Arguments">The arguments, exactly as they would be passed.</param>
/// <param name="WorkingDirectory">The working directory.</param>
public sealed record ProgramRun(string Path, IReadOnlyList<string> Arguments, string WorkingDirectory);

/// <summary>
/// What a fake program does in a harness run (<see cref="InstellaTestHarness.WhenProgramRuns"/>):
/// print lines and exit with a code, run past its time limit, or fail to start.
/// </summary>
public sealed class ProgramOutcome
{
    private ProgramOutcome() { }

    /// <summary>The exit code (when it neither times out nor fails to start).</summary>
    public int ExitCode { get; private init; }

    /// <summary>Lines printed on stdout, in order; <c>##instella progress</c> lines drive the progress.</summary>
    public IReadOnlyList<string> Output { get; private init; } = [];

    /// <summary>Lines printed on stderr, after <see cref="Output"/>.</summary>
    public IReadOnlyList<string> ErrorOutput { get; private init; } = [];

    /// <summary>Whether it runs past its time limit (and is ended).</summary>
    public bool TimesOut { get; private init; }

    /// <summary>Why it cannot be started, or null when it starts.</summary>
    public string? StartError { get; private init; }

    /// <summary>Prints <paramref name="output"/> on stdout, then exits with <paramref name="exitCode"/>.</summary>
    public static ProgramOutcome Exit(int exitCode, params string[] output)
    {
        ArgumentNullException.ThrowIfNull(output);
        return new ProgramOutcome { ExitCode = exitCode, Output = output };
    }

    /// <summary>Runs past its time limit; the installer ends it, and that is a failure.</summary>
    public static ProgramOutcome TimeOut() => new() { TimesOut = true };

    /// <summary>Cannot be started, for the reason given (missing, access denied, …).</summary>
    public static ProgramOutcome CannotStart(string error)
    {
        ArgumentException.ThrowIfNullOrEmpty(error);
        return new ProgramOutcome { StartError = error };
    }

    /// <summary>This outcome, also printing <paramref name="lines"/> on stderr.</summary>
    public ProgramOutcome WithErrorOutput(params string[] lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        return new ProgramOutcome
        {
            ExitCode = ExitCode, Output = Output, ErrorOutput = lines, TimesOut = TimesOut, StartError = StartError,
        };
    }
}

/// <summary>The harness's program runner: records every run and answers with the configured behaviour.</summary>
internal sealed class HarnessProgramRunner : IProgramRunner
{
    private readonly List<ProgramRun> _runs = [];
    private Func<ProgramRun, ProgramOutcome> _behaviour = _ => ProgramOutcome.Exit(0);

    public IReadOnlyList<ProgramRun> Runs { get { lock (_runs) return [.. _runs]; } }

    public void SetBehaviour(Func<ProgramRun, ProgramOutcome> behaviour) => _behaviour = behaviour;

    public Task<int> RunAsync(string exePath, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        var outcome = Run(new ProgramRun(exePath, [.. arguments], System.IO.Path.GetDirectoryName(exePath) ?? ""));
        if (outcome.StartError is { } error) throw new InvalidOperationException($"could not start '{exePath}': {error}");
        if (outcome.TimesOut) throw new TimeoutException($"'{System.IO.Path.GetFileName(exePath)}' did not finish in time; it was ended");
        return Task.FromResult(outcome.ExitCode);
    }

    public Task<ProgramExit> RunCapturedAsync(ProgramStart start, Action<ProgramStream, string> onLine, TimeSpan timeout, CancellationToken ct)
    {
        var outcome = Run(new ProgramRun(start.ExePath, [.. start.Arguments], start.WorkingDirectory));
        if (outcome.StartError is { } error) throw new ProgramStartException(start.ExePath, error);
        foreach (var line in outcome.Output) onLine(ProgramStream.Output, line);
        foreach (var line in outcome.ErrorOutput) onLine(ProgramStream.Error, line);
        if (outcome.TimesOut) throw new TimeoutException($"'{System.IO.Path.GetFileName(start.ExePath)}' did not finish in time; it was ended");
        return Task.FromResult(new ProgramExit(outcome.ExitCode, []));
    }

    private ProgramOutcome Run(ProgramRun run)
    {
        lock (_runs) _runs.Add(run);
        return _behaviour(run) ?? throw new InvalidOperationException("WhenProgramRuns returned null");
    }
}
