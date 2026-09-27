using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using NUnit.Framework;

namespace Instella.Installer.Build.Tests;

/// <summary>
/// Checks the shape of the produced <c>Instella.Installer.Build</c>
/// nupkg.
///
/// MSBuild loads the task through <c>UsingTask AssemblyFile=</c>, which gives it
/// no <c>.deps.json</c> and therefore no way to resolve NuGet dependencies at
/// runtime — every assembly the task touches has to sit physically beside it in
/// <c>tasks/</c>. With only the task DLL packed,
/// <c>PayloadAppender</c>'s static initialiser
/// (<c>MagicBytes = PayloadFooterReader.MagicBytes</c>, which lives in
/// Instella.Core) throws <see cref="TypeInitializationException"/> on a consumer's
/// very first publish, while every unit test still passes, because in-tree tests
/// resolve Instella.Core the ordinary way.
///
/// The inverse also matters: <c>Microsoft.Build.*</c> must NOT be packed. Those
/// are supplied by the MSBuild that hosts the task, and shipping our own copies
/// invites a second, conflicting version being loaded.
///
/// Marked <c>Packaging</c> because it shells out to <c>dotnet pack</c> and costs
/// seconds rather than milliseconds:
/// <c>dotnet test --filter "TestCategory != Packaging"</c> skips it.
/// </summary>
[TestFixture]
[Category("Packaging")]
public sealed class PackageLayoutTests
{
    /// <summary>
    /// Shelling out to <c>dotnet pack</c> takes a few seconds. The ceiling is a
    /// backstop so a wedged child process fails this fixture instead of stalling
    /// the merge gate indefinitely.
    /// </summary>
    private const int PackTimeoutMs = 300_000;

    private const string Tfm = "net10.0";

    private string _outputDir = null!;
    private string _packageContents = null!;
    private string _nuspec = null!;

    [OneTimeSetUp]
    public void PackOnce()
    {
        _outputDir = Path.Combine(Path.GetTempPath(), $"instella-pack-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_outputDir);

        var projectPath = Path.Combine(RepoRoot(), "src", "Instella.Installer.Build", "Instella.Installer.Build.csproj");
        Assert.That(projectPath, Does.Exist, "could not locate the build task project from the test assembly");

        // -nodeReuse:false is required: persistent MSBuild nodes hold a lock on
        // Instella.Installer.Build.dll and the pack fails MSB3027 without it.
        var (exitCode, output) = Run(
            "dotnet",
            $"pack \"{projectPath}\" -c Release -o \"{_outputDir}\" --nologo -nodeReuse:false");

        Assert.That(exitCode, Is.Zero, $"dotnet pack failed:{Environment.NewLine}{output}");

        var nupkg = Directory.GetFiles(_outputDir, "Instella.Installer.Build.*.nupkg").SingleOrDefault();
        Assert.That(nupkg, Is.Not.Null, "pack produced no Instella.Installer.Build nupkg");

        using var archive = ZipFile.OpenRead(nupkg!);
        _packageContents = string.Join(
            Environment.NewLine,
            archive.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal));

        var nuspecEntry = archive.Entries.SingleOrDefault(
            e => e.FullName.EndsWith(".nuspec", StringComparison.Ordinal) && !e.FullName.Contains('/'));
        Assert.That(nuspecEntry, Is.Not.Null, "package has no .nuspec at its root");

        using var reader = new StreamReader(nuspecEntry!.Open());
        _nuspec = reader.ReadToEnd();
    }

    [OneTimeTearDown]
    public void Cleanup()
    {
        if (Directory.Exists(_outputDir))
        {
            try { Directory.Delete(_outputDir, recursive: true); }
            catch { /* best effort */ }
        }
    }

    [TestCase("Instella.Installer.Build.dll", Description = "the task assembly itself")]
    [TestCase("Instella.Core.dll", Description = "PayloadAppender's static initialiser reads PayloadFooterReader from it")]
    [TestCase("Microsoft.Extensions.FileSystemGlobbing.dll", Description = "payload filter glob matching")]
    public void Package_carriesTaskDependency_besideTheTask(string assembly)
    {
        Assert.That(
            Entries(), Contains.Item($"tasks/{Tfm}/{assembly}"),
            $"{assembly} is missing from tasks/{Tfm}/. The task cannot resolve it at " +
            $"runtime, so a consumer's first publish will fail.{Environment.NewLine}" +
            $"Package contained:{Environment.NewLine}{_packageContents}");
    }

    [Test]
    public void Package_doesNotShipMSBuildAssemblies()
    {
        var msbuild = Entries()
            .Where(e => Path.GetFileName(e).StartsWith("Microsoft.Build", StringComparison.Ordinal))
            .ToArray();

        Assert.That(
            msbuild, Is.Empty,
            "MSBuild assemblies are provided by the host and must not be packed; " +
            $"shipping them risks loading a conflicting version. Found: {string.Join(", ", msbuild)}");
    }

    /// <summary>
    /// The other half of step 6's fix, and the half that leaves no trace in the
    /// packed file list: <c>PrivateAssets="all"</c> stops
    /// <c>Microsoft.Build.Framework</c> / <c>Microsoft.Build.Utilities.Core</c>
    /// being declared as nuspec <em>dependencies</em>. Without it every consumer
    /// of this package drags MSBuild into its own restore graph — the classic
    /// task-package conflict. Dropping <c>PrivateAssets</c> while keeping
    /// <c>ExcludeAssets="runtime"</c> leaves every file-based assertion in this
    /// fixture green, so the nuspec has to be asserted directly.
    ///
    /// <c>Instella.Core</c> is the one dependency that must be declared: it is
    /// vendored into <c>tasks/</c> (a NuGet dependency cannot satisfy the task at
    /// run time), and declaring it as well is what keeps the embedded version
    /// visible to audit and SBOM tooling.
    /// </summary>
    [Test]
    public void Package_declaresOnlyInstellaCoreAsADependency()
    {
        var dependencies = XDocument.Parse(_nuspec)
            .Descendants()
            .Where(e => e.Name.LocalName == "dependency")
            .Select(e => e.Attribute("id")?.Value ?? "(unnamed)")
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        Assert.That(
            dependencies, Is.EqualTo(new[] { "Instella.Core" }),
            "MSBuild assemblies are supplied by the host and must not be declared, or every " +
            "consumer drags MSBuild into its restore graph; Instella.Core must be declared so " +
            "the vendored copy's version stays on the record. " +
            $"Found: {string.Join(", ", dependencies)}");
    }

    [Test]
    public void Package_carriesTheMSBuildIntegrationFiles()
    {
        Assert.That(Entries(), Contains.Item("build/Instella.Installer.Build.props"));
        Assert.That(Entries(), Contains.Item("build/Instella.Installer.Build.targets"));
        Assert.That(Entries(), Contains.Item("build/Instella.Installer.App.manifest"));
    }

    private string[] Entries() =>
        _packageContents.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Walks up from the test assembly until the directory holding Instella.sln
    /// is found, so the test does not depend on the working directory.
    /// </summary>
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

        // Read both pipes concurrently. Draining stdout to completion first
        // deadlocks if the child fills the stderr buffer while we are still
        // blocked on stdout — and this runs inside the merge gate, so a hang
        // there stalls the whole run instead of failing it.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(PackTimeoutMs))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            Assert.Fail($"'{fileName} {arguments}' did not exit within {PackTimeoutMs} ms.");
        }

        Task.WaitAll([stdout, stderr], PackTimeoutMs);
        return (process.ExitCode, stdout.Result + stderr.Result);
    }
}
