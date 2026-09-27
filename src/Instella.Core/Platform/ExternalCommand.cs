using System.ComponentModel;
using System.Diagnostics;

namespace Instella.Core.Platform;

/// <summary>
/// Runs a helper tool (<c>xdg-mime</c>, <c>duti</c>, <c>launchctl</c>, ...) and reports its
/// outcome. A non-zero exit code is a failure carrying the tool's stderr; a missing tool is a
/// failure naming it.
/// </summary>
internal static class ExternalCommand
{
    public static async Task<PlatformResult> RunAsync(string command, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        using var process = new Process();
        process.StartInfo.FileName = command;
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.CreateNoWindow = true;

        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            return PlatformResult.Fail($"'{command}' could not be started ({ex.Message}); is it installed?");
        }

        // Both streams are drained concurrently: a tool that fills one pipe while the other is
        // unread would otherwise block forever.
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        await stdout.ConfigureAwait(false);
        var error = (await stderr.ConfigureAwait(false)).Trim();

        return process.ExitCode == 0
            ? PlatformResult.Ok
            : PlatformResult.Fail($"'{command}' exited {process.ExitCode}{(error.Length > 0 ? ": " + error : "")}");
    }
}
