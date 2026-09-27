using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Instella.Installer.Build.Tests;

/// <summary>
/// The QuickNotes sample must take its whole PE version from
/// <c>-p:Version</c>, the way the <c>instella ci init</c> workflows and the demo build it.
/// SampleApp.csproj pinned <c>AssemblyVersion</c>/<c>FileVersion</c> to 1.2.0.0, so every
/// build of 1.3.0 or 1.4.0 reported 1.2.0 and the toolbar still read "QuickNotes 1.2.0"
/// after an update, although the installed manifest said 1.4.0.
///
/// Runs only the SDK's <c>GetAssemblyVersion</c> target (which derives both from Version),
/// no build; marked <c>Packaging</c> because it still shells out to dotnet.
/// </summary>
[TestFixture]
[Category("Packaging")]
public sealed class SampleVersionTests
{
    private const int TimeoutMs = 120_000;

    [TestCase("SampleApp")]
    [TestCase("SampleApp.Installer")]
    public void VersionOnTheCommandLine_SetsAssemblyAndFileVersion(string project)
    {
        var projectPath = Path.Combine(RepoRoot(), "samples", project, project + ".csproj");
        Assert.That(projectPath, Does.Exist);

        var (exitCode, output) = Run(
            "dotnet",
            $"msbuild \"{projectPath}\" -p:Version=9.8.7 -t:GetAssemblyVersion -getProperty:AssemblyVersion -getProperty:FileVersion -nologo");

        Assert.That(exitCode, Is.Zero, output);
        Assert.That(output, Does.Contain("\"AssemblyVersion\": \"9.8.7.0\""), output);
        Assert.That(output, Does.Contain("\"FileVersion\": \"9.8.7.0\""), output);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Instella.sln")))
        {
            dir = dir.Parent;
        }

        Assert.That(dir, Is.Not.Null, "walked past the filesystem root without finding Instella.sln");
        return dir!.FullName;
    }

    private static (int ExitCode, string Output) Run(string fileName, string arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };

        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(TimeoutMs))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            Assert.Fail($"'{fileName} {arguments}' did not exit within {TimeoutMs} ms.");
        }

        Task.WaitAll([stdout, stderr], TimeoutMs);
        return (process.ExitCode, stdout.Result + stderr.Result);
    }
}
