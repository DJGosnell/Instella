using System.Diagnostics;
using System.Text;

namespace Instella.E2E.Tests;

/// <summary>Runs a process to completion and captures its output.</summary>
internal static class ProcessRunner
{
    public sealed record Result(int ExitCode, string Output);

    public static Result Run(
        string fileName, IEnumerable<string> arguments, string? workingDirectory = null,
        IReadOnlyDictionary<string, string?>? environment = null, TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
        };
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);
        // Child dotnet builds must not inherit the test host's MSBuild/SDK state.
        foreach (var name in new[] { "MSBuildExtensionsPath", "MSBuildSDKsPath", "MSBUILD_EXE_PATH", "DOTNET_HOST_PATH" })
            psi.Environment.Remove(name);
        if (environment is not null)
            foreach (var (name, value) in environment)
                psi.Environment[name] = value;

        var output = new StringBuilder();
        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (!process.WaitForExit(timeout ?? TimeSpan.FromMinutes(10)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"'{fileName} {string.Join(' ', psi.ArgumentList)}' did not finish:\n{output}");
        }
        process.WaitForExit();
        lock (output)
            return new Result(process.ExitCode, output.ToString());
    }

    /// <summary>Runs <c>dotnet</c> and fails the test with the output when it does not exit 0.</summary>
    public static string Dotnet(string workingDirectory, params string[] arguments)
    {
        var result = Run("dotnet", arguments, workingDirectory);
        if (result.ExitCode != 0)
            NUnit.Framework.Assert.Fail($"dotnet {string.Join(' ', arguments)} exited {result.ExitCode}:\n{Tail(result.Output)}");
        return result.Output;
    }

    public static string Tail(string text, int lines = 60) =>
        string.Join('\n', text.Split('\n').TakeLast(lines));
}
