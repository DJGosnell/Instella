using System.Diagnostics;
using System.IO.Compression;
using Instella.Core.Installation;
using Instella.Core.Platform;
using NUnit.Framework;

namespace Instella.Sdk.Tests;

/// <summary>
/// The MSBuild assets Instella.Sdk ships (<c>build/Instella.Sdk.props</c> and <c>.targets</c>): they are
/// in the package, and a project that sets <c>InstellaUpgradeProgram</c> gets an
/// <c>instella-upgrade.json</c> the runtime accepts, at the root of its output. Bad values are build
/// errors. Shells out to <c>dotnet</c>, hence <c>Packaging</c>.
/// </summary>
[TestFixture]
[Category("Packaging")]
[NonParallelizable]
public sealed class SdkBuildAssetsTests
{
    private const int TimeoutMs = 300_000;
    private string _work = null!;
    private string _project = null!;

    [OneTimeSetUp]
    public void CreateProbeProject()
    {
        _work = Directory.CreateTempSubdirectory("instella-sdk-targets-").FullName;
        var build = Path.Combine(RepoRoot(), "src", "Instella.Sdk", "build");
        // Empty Directory.* files stop MSBuild from importing anything above the temporary folder.
        foreach (var name in new[] { "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props" })
            File.WriteAllText(Path.Combine(_work, name), "<Project />");
        File.WriteAllText(Path.Combine(_work, "Program.cs"), "System.Console.WriteLine(\"probe\");");
        _project = Path.Combine(_work, "Probe.csproj");
        File.WriteAllText(_project, $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <Import Project="{Path.Combine(build, "Instella.Sdk.props")}" />
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <Import Project="Custom.props" Condition="Exists('Custom.props')" />
              <Import Project="{Path.Combine(build, "Instella.Sdk.targets")}" />
            </Project>
            """);
        var (exit, output) = Dotnet($"restore \"{_project}\" --nologo");
        Assert.That(exit, Is.Zero, output);
    }

    [OneTimeTearDown]
    public void Cleanup()
    {
        try { Directory.Delete(_work, recursive: true); } catch { /* best effort */ }
    }

    private (int Exit, string Output) Build(params string[] properties)
    {
        File.Delete(Path.Combine(_work, "Custom.props"));
        return BuildWith(properties);
    }

    private (int Exit, string Output) BuildWith(params string[] properties)
    {
        var props = string.Join(' ', properties.Select(p => $"\"-p:{p}\""));
        return Dotnet($"build \"{_project}\" --no-restore --nologo -nodeReuse:false -v q {props}");
    }

    private string OutputDeclaration => Path.Combine(_work, "bin", "Debug", "net10.0", "instella-upgrade.json");

    [Test]
    public void TheDeclaration_IsGenerated_AtTheRootOfTheOutput_AndReadsBack()
    {
        // In a project file, as an app sets them: a command line cannot carry the ';' and the quotes.
        File.WriteAllText(Path.Combine(_work, "Custom.props"), """
            <Project>
              <PropertyGroup>
                <InstellaUpgradeProgram>tools\Probe.Upgrade</InstellaUpgradeProgram>
                <InstellaUpgradeArguments>--db;C:\data\a "b".db</InstellaUpgradeArguments>
                <InstellaUpgradeTimeoutMinutes>45</InstellaUpgradeTimeoutMinutes>
                <InstellaUpgradeHandlesUninstall>True</InstellaUpgradeHandlesUninstall>
              </PropertyGroup>
            </Project>
            """);
        var (exit, output) = BuildWith();

        Assert.That(exit, Is.Zero, output);
        var read = AppUpgradeDeclarations.Parse(File.ReadAllBytes(OutputDeclaration), TargetPlatform.Windows);
        var valid = (AppUpgradeDeclarationRead.Valid)read;
        Assert.Multiple(() =>
        {
            Assert.That(valid.ProgramRelativePath, Is.EqualTo("tools/Probe.Upgrade.exe"));
            Assert.That(valid.Declaration.ContractVersion, Is.EqualTo(1));
            Assert.That(valid.Declaration.Arguments, Is.EqualTo(new[] { "--db", "C:\\data\\a \"b\".db" }));
            Assert.That(valid.Declaration.TimeoutMinutes, Is.EqualTo(45));
            Assert.That(valid.Declaration.HandlesUninstall, Is.True);
        });
    }

    [Test]
    public void Arguments_AreTakenLiterally_NotAsWildcards()
    {
        File.WriteAllText(Path.Combine(_work, "Custom.props"), """
            <Project>
              <PropertyGroup>
                <InstellaUpgradeProgram>Probe.Upgrade</InstellaUpgradeProgram>
                <InstellaUpgradeArguments>--pattern=*.db;--one=?;--name=O'Brien;--tab=a&#9;b;  --spaced  ;;</InstellaUpgradeArguments>
              </PropertyGroup>
            </Project>
            """);
        var (exit, output) = BuildWith();

        Assert.That(exit, Is.Zero, output);
        var valid = (AppUpgradeDeclarationRead.Valid)AppUpgradeDeclarations.Parse(File.ReadAllBytes(OutputDeclaration), TargetPlatform.Windows);
        Assert.That(valid.Declaration.Arguments, Is.EqualTo(new[]
        {
            "--pattern=*.db", "--one=?", "--name=O'Brien", "--tab=a\tb", "--spaced",
        }));
    }

    [Test]
    public void TheDefaults_AreThirtyMinutes_NoArguments_NoUninstall()
    {
        var (exit, output) = Build("InstellaUpgradeProgram=Probe.Upgrade");
        Assert.That(exit, Is.Zero, output);
        var valid = (AppUpgradeDeclarationRead.Valid)AppUpgradeDeclarations.Parse(File.ReadAllBytes(OutputDeclaration), TargetPlatform.Linux);
        Assert.That(valid.ProgramRelativePath, Is.EqualTo("Probe.Upgrade"));
        Assert.That(valid.Declaration.Arguments, Is.Empty);
        Assert.That(valid.Declaration.TimeoutMinutes, Is.EqualTo(30));
        Assert.That(valid.Declaration.HandlesUninstall, Is.False);
    }

    [Test]
    public void WithoutTheProperty_NoDeclarationIsWritten()
    {
        if (File.Exists(OutputDeclaration)) File.Delete(OutputDeclaration);
        var (exit, output) = Build();
        Assert.That(exit, Is.Zero, output);
        Assert.That(OutputDeclaration, Does.Not.Exist);
    }

    [TestCase("InstellaUpgradeProgram=..\\Probe.Upgrade", "INSTELLA0301")]
    [TestCase("InstellaUpgradeProgram=C:\\tools\\Probe.Upgrade", "INSTELLA0301")]
    [TestCase("InstellaUpgradeTimeoutMinutes=soon", "INSTELLA0302")]
    [TestCase("InstellaUpgradeTimeoutMinutes=0", "INSTELLA0302")]
    [TestCase("InstellaUpgradeTimeoutMinutes=1441", "INSTELLA0302")]
    [TestCase("InstellaUpgradeHandlesUninstall=maybe", "INSTELLA0303")]
    public void ABadValue_IsABuildError(string property, string code)
    {
        var properties = property.StartsWith("InstellaUpgradeProgram=", StringComparison.Ordinal)
            ? new[] { property }
            : new[] { "InstellaUpgradeProgram=Probe.Upgrade", property };
        var (exit, output) = Build(properties);
        Assert.That(exit, Is.Not.Zero);
        Assert.That(output, Does.Contain(code));
    }

    [Test]
    public void ThePackage_CarriesTheBuildAssets()
    {
        var feed = Path.Combine(_work, "feed");
        var sdk = Path.Combine(RepoRoot(), "src", "Instella.Sdk", "Instella.Sdk.csproj");
        var (exit, output) = Dotnet($"pack \"{sdk}\" -c Release -o \"{feed}\" --nologo -nodeReuse:false");
        Assert.That(exit, Is.Zero, output);

        var nupkg = Directory.GetFiles(feed, "Instella.Sdk.*.nupkg").Single(f => !f.EndsWith(".snupkg", StringComparison.Ordinal));
        using var archive = ZipFile.OpenRead(nupkg);
        var entries = archive.Entries.Select(e => e.FullName).ToList();
        Assert.That(entries, Does.Contain("build/Instella.Sdk.props"));
        Assert.That(entries, Does.Contain("build/Instella.Sdk.targets"));
    }

    private static (int ExitCode, string Output) Dotnet(string arguments)
    {
        var psi = new ProcessStartInfo("dotnet", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeoutMs))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail($"dotnet {arguments} did not finish in {TimeoutMs / 1000} s");
        }
        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Instella.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
