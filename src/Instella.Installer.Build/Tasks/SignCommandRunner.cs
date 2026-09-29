using System;
using System.Diagnostics;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Instella.Installer.Build.Tasks;

/// <summary>Runs <c>InstellaSignCommand</c> on one file, for every task that signs.</summary>
internal static class SignCommandRunner
{
    /// <summary>
    /// Runs <paramref name="signCommand"/> with <c>{0}</c> replaced by <paramref name="path"/>,
    /// through <c>cmd.exe</c> (Windows) or <c>/bin/sh</c>. An empty command signs nothing.
    /// </summary>
    /// <exception cref="InstellaBuildException">INSTELLA0204: no <c>{0}</c>, or the command failed.</exception>
    public static void Run(string signCommand, string path, TaskLoggingHelper log)
    {
        if (string.IsNullOrWhiteSpace(signCommand))
            return;
        if (!signCommand.Contains("{0}", StringComparison.Ordinal))
            throw new InstellaBuildException("INSTELLA0204", "InstellaSignCommand must contain {0} where the file path goes.");

        var command = signCommand.Replace("{0}", path, StringComparison.Ordinal);
        // cmd /s strips exactly one pair of outer quotes and runs the rest verbatim, so the
        // user's own quoting survives; ArgumentList would escape it for the C runtime instead.
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/d /s /c \"" + command + "\"")
            : new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", command } };
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;

        log.LogMessage(MessageImportance.High, $"Instella: signing {path}");
        using var process = Process.Start(psi)
            ?? throw new InstellaBuildException("INSTELLA0204", $"could not start the sign command for '{path}'.");
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (!string.IsNullOrWhiteSpace(stdout))
            log.LogMessage(MessageImportance.Normal, stdout.TrimEnd());
        if (process.ExitCode != 0)
            throw new InstellaBuildException("INSTELLA0204",
                $"signing '{path}' failed (exit {process.ExitCode}): {stderr.Result.Trim()}");
    }
}
