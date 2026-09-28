using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Instella.Core.Installation;
using Instella.Core.Utilities;

namespace Instella.Sdk;

/// <summary>
/// The entry point of an app upgrade program: the separate executable that <c>instella-upgrade.json</c>
/// names (MSBuild property <c>InstellaUpgradeProgram</c>). The installer and the in-app updater run it
/// headless, on the new files, every time the installed version changes; a non-zero exit rolls the
/// operation back. <see cref="RunAsync(string[], Func{AppUpgradeContext, IAppUpgradeProgress, CancellationToken, Task}, Func{AppUpgradeContext, CancellationToken, Task})"/>
/// parses the launch contract, reports progress, and turns exceptions into exit codes, so the
/// program's <c>Main</c> is one call.
/// </summary>
/// <remarks>
/// Exit codes: 0 success; 1 the handler threw (the data must still be usable by the old version);
/// 2 the handler refused (<see cref="AppUpgradeRefusedException"/>); 3 the program was not started by
/// Instella, or with a contract version this SDK does not know.
/// </remarks>
public static class InstellaUpgrade
{
    /// <summary>
    /// Runs <paramref name="upgrade"/> as <paramref name="args"/> say (install, upgrade, repair, downgrade,
    /// update) and returns the exit code for <c>Main</c> to return. An uninstall call exits 0: the
    /// program has no uninstall handler. Progress goes to stdout, errors to stderr; Ctrl+C cancels the token.
    /// </summary>
    /// <param name="args">The program's command-line arguments.</param>
    /// <param name="upgrade">Upgrades the app's data. Throw to fail, or <see cref="AppUpgradeRefusedException"/> to refuse.</param>
    public static Task<int> RunAsync(string[] args, Func<AppUpgradeContext, IAppUpgradeProgress, CancellationToken, Task> upgrade) =>
        RunAsync(args, upgrade, uninstall: null);

    /// <summary>
    /// Runs <paramref name="upgrade"/> (install, upgrade, repair, downgrade, update) or
    /// <paramref name="uninstall"/> (before an uninstall, when <c>InstellaUpgradeHandlesUninstall</c> is
    /// set), as <paramref name="args"/> say, and returns the exit code for <c>Main</c> to return. Progress
    /// goes to stdout, errors to stderr; Ctrl+C cancels the token.
    /// </summary>
    /// <param name="args">The program's command-line arguments.</param>
    /// <param name="upgrade">Upgrades the app's data. Throw to fail, or <see cref="AppUpgradeRefusedException"/> to refuse.</param>
    /// <param name="uninstall">Cleans up before an uninstall; null means there is nothing to do (exit 0).</param>
    public static async Task<int> RunAsync(
        string[] args,
        Func<AppUpgradeContext, IAppUpgradeProgress, CancellationToken, Task> upgrade,
        Func<AppUpgradeContext, CancellationToken, Task>? uninstall)
    {
        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        Console.CancelKeyPress += onCancel;
        try
        {
            return await RunAsync(args, upgrade, uninstall, Console.Out, Console.Error, cts.Token);
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }

    /// <summary>
    /// <see cref="RunAsync(string[], Func{AppUpgradeContext, IAppUpgradeProgress, CancellationToken, Task}, Func{AppUpgradeContext, CancellationToken, Task})"/>
    /// writing to <paramref name="output"/> and <paramref name="error"/> instead of the console: for tests.
    /// </summary>
    /// <param name="args">The arguments, for example <see cref="AppUpgradeContext.ToArguments"/>.</param>
    /// <param name="upgrade">Upgrades the app's data.</param>
    /// <param name="uninstall">Cleans up before an uninstall, or null.</param>
    /// <param name="output">Receives the progress lines and messages (stdout).</param>
    /// <param name="error">Receives the errors (stderr).</param>
    /// <param name="cancellationToken">Passed to the handler.</param>
    public static async Task<int> RunAsync(
        string[] args,
        Func<AppUpgradeContext, IAppUpgradeProgress, CancellationToken, Task> upgrade,
        Func<AppUpgradeContext, CancellationToken, Task>? uninstall,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(upgrade);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (!IsInstellaInvocation(args))
        {
            await error.WriteLineAsync("This program is run by the app's installer and updater; it is not meant to be started directly.");
            await error.FlushAsync(cancellationToken);
            return AppUpgradeContract.ExitNotUnderstood;
        }
        if (!TryParse(args, out var context, out var problem))
        {
            await error.WriteLineAsync($"instella-upgrade: {problem}");
            await error.FlushAsync(cancellationToken);
            return AppUpgradeContract.ExitNotUnderstood;
        }

        try
        {
            if (context.Mode == AppUpgradeMode.Uninstall)
            {
                if (uninstall is null)
                    await output.WriteLineAsync("instella-upgrade: no uninstall handler; nothing to do");
                else
                    await uninstall(context, cancellationToken);
            }
            else
            {
                var progress = new WriterProgress(output);
                await upgrade(context, progress, cancellationToken);
                if (progress.Last < 100) progress.Report(100);
            }
            await output.FlushAsync(cancellationToken);
            return AppUpgradeContract.ExitSuccess;
        }
        catch (AppUpgradeRefusedException ex)
        {
            await error.WriteLineAsync(ex.Message);
            await error.FlushAsync(CancellationToken.None);
            return AppUpgradeContract.ExitRefused;
        }
        catch (Exception ex)
        {
            await error.WriteLineAsync(ex.ToString());
            await error.FlushAsync(CancellationToken.None);
            return AppUpgradeContract.ExitFailed;
        }
    }

    /// <summary>Whether <paramref name="args"/> start with <c>--instella-upgrade</c> or <c>--instella-uninstall</c>.</summary>
    public static bool IsInstellaInvocation(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Count > 0 && args[0] is AppUpgradeContract.UpgradeSwitch or AppUpgradeContract.UninstallSwitch;
    }

    /// <summary>
    /// Parses the launch contract. Returns false, with the reason in <paramref name="error"/>, when
    /// <paramref name="args"/> are not a contract call, a value is missing or malformed, or the contract
    /// version is newer than this SDK knows. Arguments after the contract's own are
    /// <see cref="AppUpgradeContext.ExtraArguments"/>.
    /// </summary>
    public static bool TryParse(IReadOnlyList<string> args, [NotNullWhen(true)] out AppUpgradeContext? context, out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);
        context = null;
        if (!IsInstellaInvocation(args))
        {
            error = $"the first argument must be {AppUpgradeContract.UpgradeSwitch} or {AppUpgradeContract.UninstallSwitch}";
            return false;
        }

        var known = new Dictionary<string, string>(StringComparer.Ordinal);
        var extra = new List<string>();
        string[] names =
        [
            AppUpgradeContract.ContractArg, AppUpgradeContract.ModeArg, AppUpgradeContract.FromArg, AppUpgradeContract.ToArg,
            AppUpgradeContract.ScopeArg, AppUpgradeContract.InstallPathArg, AppUpgradeContract.AppIdArg,
        ];
        for (var i = 1; i < args.Count; i++)
        {
            // The contract's own arguments come first; once all are read, the rest belong to the app.
            if (known.Count < names.Length && names.Contains(args[i]) && !known.ContainsKey(args[i]))
            {
                if (i + 1 >= args.Count)
                {
                    error = $"{args[i]} needs a value";
                    return false;
                }
                known[args[i]] = args[++i];
            }
            else
            {
                extra.Add(args[i]);
            }
        }

        if (!known.TryGetValue(AppUpgradeContract.ContractArg, out var contractText)
            || !int.TryParse(contractText, NumberStyles.None, CultureInfo.InvariantCulture, out var contract) || contract < 1)
        {
            error = $"{AppUpgradeContract.ContractArg} must be a contract version";
            return false;
        }
        if (contract > AppUpgradeContract.CurrentVersion)
        {
            error = $"launch contract version {contract} is newer than this Instella.Sdk supports ({AppUpgradeContract.CurrentVersion}); update Instella.Sdk";
            return false;
        }
        foreach (var name in names)
        {
            if (!known.ContainsKey(name))
            {
                error = $"{name} is missing";
                return false;
            }
        }

        if (!AppUpgradeContract.TryParseMode(known[AppUpgradeContract.ModeArg], out var mode))
        {
            error = $"unknown {AppUpgradeContract.ModeArg} '{known[AppUpgradeContract.ModeArg]}'";
            return false;
        }
        var uninstalling = args[0] == AppUpgradeContract.UninstallSwitch;
        if (uninstalling != (mode == AppUpgradeLaunchMode.Uninstall))
        {
            error = $"{args[0]} does not go with {AppUpgradeContract.ModeArg} {known[AppUpgradeContract.ModeArg]}";
            return false;
        }
        if (!TryVersion(known[AppUpgradeContract.FromArg], out var from) || !TryVersion(known[AppUpgradeContract.ToArg], out var to))
        {
            error = $"{AppUpgradeContract.FromArg} and {AppUpgradeContract.ToArg} must be versions or '{AppUpgradeContract.NoVersion}'";
            return false;
        }
        var scope = known[AppUpgradeContract.ScopeArg] switch
        {
            AppUpgradeContract.UserScope => AppUpgradeScope.PerUser,
            AppUpgradeContract.MachineScope => AppUpgradeScope.Machine,
            _ => (AppUpgradeScope?)null,
        };
        if (scope is null)
        {
            error = $"{AppUpgradeContract.ScopeArg} must be '{AppUpgradeContract.UserScope}' or '{AppUpgradeContract.MachineScope}'";
            return false;
        }
        if (string.IsNullOrEmpty(known[AppUpgradeContract.InstallPathArg]) || string.IsNullOrEmpty(known[AppUpgradeContract.AppIdArg]))
        {
            error = $"{AppUpgradeContract.InstallPathArg} and {AppUpgradeContract.AppIdArg} must not be empty";
            return false;
        }

        context = new AppUpgradeContext
        {
            Mode = (AppUpgradeMode)mode,
            FromVersion = from,
            ToVersion = to,
            Scope = scope.Value,
            InstallPath = known[AppUpgradeContract.InstallPathArg],
            AppId = known[AppUpgradeContract.AppIdArg],
            ContractVersion = contract,
            ExtraArguments = extra,
        };
        error = null;
        return true;
    }

    private static bool TryVersion(string text, out Version? version)
    {
        version = null;
        if (text == AppUpgradeContract.NoVersion) return true;
        if (!AppVersions.TryParse(text, out var parsed)) return false;
        version = parsed;
        return true;
    }

    /// <summary>Writes <c>##instella progress</c> lines and flushes each one, so the window follows at once.</summary>
    private sealed class WriterProgress(TextWriter output) : IAppUpgradeProgress
    {
        public int Last { get; private set; } = -1;

        public void Report(int percent, string? text = null)
        {
            Last = Math.Clamp(percent, 0, 100);
            lock (output)
            {
                output.WriteLine(AppUpgradeContract.FormatProgress(percent, text));
                output.Flush();
            }
        }
    }
}
