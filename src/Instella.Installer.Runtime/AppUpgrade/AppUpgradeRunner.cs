using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.FileSystem;
using Instella.Core.Installation;
using Instella.Core.Logging;
using Instella.Core.Platform;
using Instella.Installer.Runtime.Migrations;

namespace Instella.Installer.Runtime.AppUpgrade;

/// <summary>One run of the app's upgrade program: what to tell it and which files are the app's.</summary>
internal sealed record AppUpgradeRequest
{
    /// <summary>The operation (<c>--mode</c>).</summary>
    public required AppUpgradeLaunchMode Mode { get; init; }
    /// <summary>The installed version before the operation; null on a first install.</summary>
    public Version? From { get; init; }
    /// <summary>The version after it; null on uninstall.</summary>
    public Version? To { get; init; }
    /// <summary>Per-user or machine-wide.</summary>
    public required InstallationScope Scope { get; init; }
    /// <summary>The install folder: holds the declaration and the program, and is the working directory.</summary>
    public required string InstallPath { get; init; }
    /// <summary>The application id.</summary>
    public required string AppId { get; init; }
    /// <summary>The platform whose program name applies (<see cref="AppUpgradeDeclarations.ProgramPathFor"/>).</summary>
    public required TargetPlatform Platform { get; init; }
    /// <summary>The app's files for the version whose program runs (relative, either separator). The program must be one of them.</summary>
    public required IReadOnlyCollection<string> InstalledFiles { get; init; }
    /// <summary>Uninstall only: the installed manifest's hashes. The program runs only when both it and the declaration have the recorded bytes.</summary>
    public IReadOnlyDictionary<string, string>? ExpectedHashes { get; init; }
}

/// <summary>Progress from the program: a fraction (0-1) and the status text, if it gave one.</summary>
internal readonly record struct AppUpgradeProgress(double Fraction, string? Text);

/// <summary>How a run of the app's upgrade program ended.</summary>
internal enum AppUpgradeOutcome
{
    /// <summary>No declaration: nothing to run.</summary>
    NotDeclared,
    /// <summary>Exit code 0.</summary>
    Succeeded,
    /// <summary>Exit code 1, or any code without a reserved meaning.</summary>
    Failed,
    /// <summary>Exit code 2: the app refused the change.</summary>
    Refused,
    /// <summary>Exit code 3: the program did not understand how it was started.</summary>
    NotUnderstood,
    /// <summary>It ran past the declaration's time limit and was ended.</summary>
    TimedOut,
    /// <summary>It could not be started (missing, not executable, access denied).</summary>
    NotStarted,
    /// <summary>The declaration could not be honoured (see the message).</summary>
    InvalidDeclaration,
    /// <summary>Not run, by design: an uninstall the program does not handle, or a program changed since it was installed.</summary>
    Skipped,
}

/// <summary>The outcome, the exit code when there was one, and a message for the log and the user.</summary>
internal sealed record AppUpgradeResult(AppUpgradeOutcome Outcome, int? ExitCode, string Message)
{
    /// <summary>Whether the operation may go on: the program succeeded, or there was nothing to run.</summary>
    public bool Success => Outcome is AppUpgradeOutcome.NotDeclared or AppUpgradeOutcome.Succeeded or AppUpgradeOutcome.Skipped;

    /// <summary>Whether the program actually ran and exited 0.</summary>
    public bool Ran => Outcome == AppUpgradeOutcome.Succeeded;
}

/// <summary>
/// Reads the declaration from the install folder, checks the program is one of the app's files,
/// runs it with the launch contract, turns its progress lines into progress, logs its output
/// (capped), and maps the exit to an <see cref="AppUpgradeResult"/>. Never throws apart from
/// cancellation before the program starts: every failure is a result.
/// </summary>
internal sealed class AppUpgradeRunner
{
    /// <summary>Longest output line written to the log; the rest is cut.</summary>
    internal const int MaxLineLength = 4096;

    /// <summary>Output written to the log per run; later lines are counted, not logged.</summary>
    internal const int MaxLoggedBytes = 1024 * 1024;

    /// <summary>The status text before the program reports any.</summary>
    internal const string DefaultStatus = "Upgrading your data";

    private readonly IFileSystem _fs;
    private readonly IProgramRunner _programs;
    private readonly IInstellaLogger _log;

    public AppUpgradeRunner(IFileSystem fs, IProgramRunner programs, IInstellaLogger log)
    {
        _fs = fs;
        _programs = programs;
        _log = log;
    }

    public async Task<AppUpgradeResult> RunAsync(AppUpgradeRequest request, IProgress<AppUpgradeProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var comparer = request.Platform == TargetPlatform.Windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var read = await ReadDeclarationAsync(request, ct);
        if (read is AppUpgradeDeclarationRead.Absent)
            return Done(AppUpgradeOutcome.NotDeclared, null, "the app declares no upgrade program");

        // On uninstall the declaration decides what runs, and with which arguments: trust it only as installed.
        if (await CheckInstalledHashAsync(request, AppUpgradeContract.DeclarationFileName, comparer, ct) is { } changedDeclaration)
            return changedDeclaration;
        if (read is AppUpgradeDeclarationRead.Refused refused)
            return Done(AppUpgradeOutcome.InvalidDeclaration, null, refused.Reason);

        var (declaration, program) = (AppUpgradeDeclarationRead.Valid)read;
        if (request.Mode == AppUpgradeLaunchMode.Uninstall && !declaration.HandlesUninstall)
            return Done(AppUpgradeOutcome.Skipped, null, "the upgrade program does not handle uninstall");

        if (!request.InstalledFiles.Any(f => comparer.Equals(f.Replace('\\', '/'), program)))
            return Done(AppUpgradeOutcome.InvalidDeclaration, null,
                $"{AppUpgradeContract.DeclarationFileName} names '{program}', which is not one of the app's files");

        var exePath = SafePath.Combine(request.InstallPath, program);
        if (!_fs.Exists(exePath))
            return Done(AppUpgradeOutcome.NotStarted, null, $"the upgrade program '{program}' is missing from the install folder");

        if (await CheckInstalledHashAsync(request, program, comparer, ct) is { } changedProgram)
            return changedProgram;

        var launch = new AppUpgradeLaunch(request.Mode, request.From, request.To, request.Scope, request.InstallPath,
            request.AppId, declaration.ContractVersion);
        var args = AppUpgradeContract.BuildArguments(launch, declaration.Arguments);
        var timeout = TimeoutOverride ?? TimeoutFor(declaration);
        var start = new ProgramStart(exePath, args, request.InstallPath, new Dictionary<string, string>
        {
            [AppUpgradeContract.EnvironmentVariable] = declaration.ContractVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });

        _log.Info($"app-upgrade: running {program} {string.Join(' ', args.Select(Quote))} (time limit {declaration.TimeoutMinutes} min)");
        progress?.Report(new AppUpgradeProgress(0, DefaultStatus));

        var output = new AppUpgradeOutputLog(_log);
        var clock = Stopwatch.StartNew();
        ProgramExit exit;
        try
        {
            exit = await _programs.RunCapturedAsync(start, (stream, line) =>
            {
                output.Write(stream, line);
                if (stream != ProgramStream.Output) return;
                if (AppUpgradeContract.TryParseInstruction(line, out var p, out var problem))
                    progress?.Report(new AppUpgradeProgress(p.Percent / 100.0, p.Text));
                else if (problem is not null)
                    _log.Warn($"app-upgrade: instruction not understood ({problem}): {Cut(line)}");
            }, timeout, CancellationToken.None);
        }
        catch (TimeoutException)
        {
            output.Finish();
            return Done(AppUpgradeOutcome.TimedOut, null,
                $"the upgrade program did not finish within {declaration.TimeoutMinutes} minute(s) and was ended");
        }
        catch (ProgramStartException ex)
        {
            output.Finish();
            return Done(AppUpgradeOutcome.NotStarted, null, $"the upgrade program could not be started: {ex.Reason}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            output.Finish();
            return Done(AppUpgradeOutcome.NotStarted, null, $"the upgrade program could not be run: {ex.Message}");
        }

        output.Finish();
        foreach (var note in exit.Notes) _log.Warn($"app-upgrade: {note}");
        var elapsed = $"{clock.Elapsed.TotalSeconds:0.0} s";
        var detail = output.LastError ?? output.LastOutput;
        return exit.ExitCode switch
        {
            AppUpgradeContract.ExitSuccess => Done(AppUpgradeOutcome.Succeeded, 0, $"the upgrade program succeeded in {elapsed}"),
            AppUpgradeContract.ExitRefused => Done(AppUpgradeOutcome.Refused, 2,
                $"the app refused this change{(detail is null ? "" : $": {detail}")}"),
            AppUpgradeContract.ExitNotUnderstood => Done(AppUpgradeOutcome.NotUnderstood, 3,
                $"the upgrade program did not understand launch contract version {declaration.ContractVersion}"),
            var code => Done(AppUpgradeOutcome.Failed, code,
                $"the upgrade program failed with exit code {code}{(detail is null ? "" : $": {detail}")}"),
        };
    }

    /// <summary>The time limit of <paramref name="declaration"/>.</summary>
    internal static TimeSpan TimeoutFor(AppUpgradeDeclaration declaration) => TimeSpan.FromMinutes(declaration.TimeoutMinutes);

    /// <summary>Test seam: overrides every declaration's time limit (a timeout test cannot wait a minute).</summary>
    internal TimeSpan? TimeoutOverride { get; init; }

    /// <summary>
    /// Uninstall only: null when <paramref name="relative"/> has the bytes the installed manifest
    /// records; otherwise the result that stops the program.
    /// </summary>
    private async Task<AppUpgradeResult?> CheckInstalledHashAsync(AppUpgradeRequest request, string relative, StringComparer comparer, CancellationToken ct)
    {
        if (request.ExpectedHashes is not { } hashes)
            return null;
        var expected = hashes.FirstOrDefault(h => comparer.Equals(h.Key.Replace('\\', '/'), relative)).Value;
        if (expected is null)
            return Done(AppUpgradeOutcome.NotStarted, null, $"the installation has no record of '{relative}'");
        var actual = await _fs.ComputeSha256Async(SafePath.Combine(request.InstallPath, relative), ct);
        return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase) ? null
            : Done(AppUpgradeOutcome.Skipped, null, $"'{relative}' changed since it was installed, so the upgrade program was not run");
    }

    private async Task<AppUpgradeDeclarationRead> ReadDeclarationAsync(AppUpgradeRequest request, CancellationToken ct)
    {
        try
        {
            return await AppUpgradeDeclarations.ReadAsync(_fs, request.InstallPath, request.Platform, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AppUpgradeDeclarationRead.Refused($"{AppUpgradeContract.DeclarationFileName} cannot be read: {ex.Message}");
        }
    }

    private AppUpgradeResult Done(AppUpgradeOutcome outcome, int? exitCode, string message)
    {
        var result = new AppUpgradeResult(outcome, exitCode, message);
        if (outcome == AppUpgradeOutcome.NotDeclared)
            return result;
        if (result.Success)
            _log.Info($"app-upgrade: {message}");
        else
            _log.Warn($"app-upgrade: {message}");
        return result;
    }

    private static string Quote(string arg) =>
        arg.Length > 0 && !arg.Any(c => char.IsWhiteSpace(c) || c == '"') ? arg : "\"" + arg.Replace("\"", "\\\"") + "\"";

    internal static string Cut(string line) =>
        line.Length <= MaxLineLength ? line : line[..MaxLineLength] + $"… ({line.Length - MaxLineLength} more characters)";
}

/// <summary>
/// The program's output in the log: stdout at Info, stderr at Warn, each line cut at
/// <see cref="AppUpgradeRunner.MaxLineLength"/>, and at most <see cref="AppUpgradeRunner.MaxLoggedBytes"/>
/// per run; lines past that are counted and summarised by <see cref="Finish"/>.
/// </summary>
internal sealed class AppUpgradeOutputLog(IInstellaLogger log)
{
    private long _bytes;
    private int _dropped;
    private bool _finished;

    /// <summary>The last non-empty stdout line that is not an instruction (for the failure message).</summary>
    public string? LastOutput { get; private set; }

    /// <summary>The last non-empty stderr line (for the failure message).</summary>
    public string? LastError { get; private set; }

    public void Write(ProgramStream stream, string line)
    {
        var text = AppUpgradeRunner.Cut(line);
        if (!string.IsNullOrWhiteSpace(line))
        {
            var brief = line.Length <= 300 ? line.Trim() : line[..300].Trim() + "…";
            if (stream == ProgramStream.Error) LastError = brief;
            else if (!line.StartsWith(AppUpgradeContract.InstructionPrefix, StringComparison.Ordinal)) LastOutput = brief;
        }

        if (_bytes >= AppUpgradeRunner.MaxLoggedBytes)
        {
            _dropped++;
            return;
        }
        _bytes += Encoding.UTF8.GetByteCount(text) + 1;
        if (stream == ProgramStream.Error)
            log.Warn($"app-upgrade (stderr): {text}");
        else
            log.Info($"app-upgrade: {text}");
    }

    /// <summary>Logs how many lines were not logged, once.</summary>
    public void Finish()
    {
        if (_finished) return;
        _finished = true;
        if (_dropped > 0)
            log.Warn($"app-upgrade: {_dropped} more output line(s) were not logged (the log keeps {AppUpgradeRunner.MaxLoggedBytes / (1024 * 1024)} MiB per run)");
    }
}
