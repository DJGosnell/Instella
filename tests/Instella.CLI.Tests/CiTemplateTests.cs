using System.CommandLine;
using Instella.CLI.Commands;
using Instella.CLI.Templates;
using NUnit.Framework;

namespace Instella.CLI.Tests;

/// <summary>
/// <c>instella ci init</c>: the workflow for each host and signing method keeps credentials out of
/// the build job, asks for OIDC only where the host issues it, and never mixes signing methods.
/// </summary>
[TestFixture]
public class CiTemplateTests
{
    private static CiOptions Options(CiHost host, CiSigning signing) => new()
    {
        Host = host,
        Signing = signing,
        AppProject = "src/QuickNotes/QuickNotes.csproj",
        InstallerProject = "src/QuickNotes.Installer/QuickNotes.Installer.csproj",
        PackageId = "com.example.quicknotes",
        ServerUrl = "https://updates.example.com",
        AppName = "QuickNotes",
    };

    private static IEnumerable<TestCaseData> AllCombinations() =>
        from host in Enum.GetValues<CiHost>()
        from signing in Enum.GetValues<CiSigning>()
        select new TestCaseData(host, signing).SetName($"{host} {signing}");

    [TestCaseSource(nameof(AllCombinations))]
    public void EveryWorkflow_BuildsWithoutCredentials_AndUploadsBothInstallers(CiHost host, CiSigning signing)
    {
        var yaml = CiTemplate.Generate(Options(host, signing));
        var build = yaml[yaml.IndexOf("  build:", StringComparison.Ordinal)..yaml.IndexOf("  publish:", StringComparison.Ordinal)];

        Assert.That(yaml, Does.Not.Contain("\t"), "YAML must not contain tabs");
        Assert.That(build, Does.Not.Contain("secrets.").And.Not.Contain("id-token"), "the build job holds no credentials");
        Assert.That(build, Does.Contain("-p:InstellaEnabled=false -o out/online"));
        Assert.That(build.IndexOf("out/online", StringComparison.Ordinal), Is.LessThan(build.IndexOf("-o out/offline", StringComparison.Ordinal)),
            "the online installer is built before the offline one");
        Assert.That(yaml, Does.Contain("--installer \"out/installers/$APP_NAME-WebSetup-$VERSION$ext\""));
        Assert.That(yaml, Does.Contain("--offline-installer \"out/installers/$APP_NAME-Setup-$VERSION$ext\""));
        Assert.That(yaml, Does.Contain("secrets.INSTELLA_API_KEY"));
        Assert.That(yaml, Does.Contain("--os windows --arch x64"));
    }

    [TestCase(CiSigning.KmsAzure, "az keyvault key sign", "azure/login@")]
    [TestCase(CiSigning.KmsAws, "aws kms sign", "aws-actions/configure-aws-credentials@")]
    [TestCase(CiSigning.KmsGcp, "gcloud kms asymmetric-sign", "google-github-actions/auth@")]
    public void GitHubKms_UsesOidcInAProtectedEnvironment_AndNoKeyMaterial(CiSigning signing, string command, string login)
    {
        var yaml = CiTemplate.Generate(Options(CiHost.GitHub, signing));

        Assert.That(yaml, Does.Contain("id-token: write").And.Contain("environment: release"));
        Assert.That(yaml, Does.Contain("INSTELLA_SIGN_COMMAND: " + command).And.Contain("INSTELLA_SIGNING_PUBLIC_KEY"));
        Assert.That(yaml, Does.Contain(login));
        Assert.That(yaml, Does.Not.Contain("INSTELLA_SIGNING_KEY:").And.Not.Contain("SECRET_ACCESS_KEY").And.Not.Contain("credentials_json"));
    }

    [Test]
    public void GiteaKms_HasNoOidc_SoCredentialsComeFromSecrets()
    {
        var yaml = CiTemplate.Generate(Options(CiHost.Gitea, CiSigning.KmsAws));

        Assert.That(CiTemplate.WorkflowPath(CiHost.Gitea), Is.EqualTo(".gitea/workflows/instella-release.yml"));
        Assert.That(yaml, Does.Not.Contain("id-token").And.Not.Contain("environment:"));
        Assert.That(yaml, Does.Contain("secrets.AWS_SECRET_ACCESS_KEY"));
        Assert.That(yaml, Does.Contain("actions/upload-artifact@v3").And.Contain("actions/download-artifact@v3"));
    }

    [TestCase(CiHost.GitHub)]
    [TestCase(CiHost.Gitea)]
    public void Draft_UploadsWithoutAnyKey(CiHost host)
    {
        var yaml = CiTemplate.Generate(Options(host, CiSigning.Draft));

        Assert.That(yaml, Does.Contain("--draft"));
        Assert.That(yaml, Does.Not.Contain("INSTELLA_SIGN").And.Not.Contain("id-token").And.Not.Contain("environment:"));
        Assert.That(CiTemplate.SetupNotes(Options(host, CiSigning.Draft)), Does.Contain("instella publish"));
    }

    [Test]
    public void Secret_OnGitHub_KeepsTheKeyInTheProtectedEnvironment()
    {
        var yaml = CiTemplate.Generate(Options(CiHost.GitHub, CiSigning.Secret));

        Assert.That(yaml, Does.Contain("INSTELLA_SIGNING_KEY: ${{ secrets.INSTELLA_SIGNING_KEY }}").And.Contain("environment: release"));
        Assert.That(yaml, Does.Not.Contain("id-token").And.Not.Contain("INSTELLA_SIGN_COMMAND"));
    }

    [Test]
    public async Task Command_WritesTheWorkflow_AndRefusesToOverwriteWithoutForce()
    {
        var dir = Directory.CreateTempSubdirectory("instella-ci-").FullName;
        try
        {
            string[] args = ["ci", "init", "--host", "github", "--signing", "kms-aws", "--app-project", "src/App/App.csproj",
                "--installer-project", "src/App.Installer/App.Installer.csproj", "--package", "com.example.app",
                "--server", "https://updates.example.com", "--output", dir];
            Assert.That(await Run(args), Is.EqualTo(ExitCodes.Success));
            var path = Path.Combine(dir, ".github", "workflows", "instella-release.yml");
            Assert.That(File.ReadAllText(path), Does.Contain("APP_NAME: 'App'"), "the name defaults to the app project's");

            Assert.That(await Run(args), Is.EqualTo(ExitCodes.Usage), "an existing workflow is not overwritten");
            Assert.That(await Run([.. args, "--force"]), Is.EqualTo(ExitCodes.Success));
            Assert.That(await Run([.. args[..5], "unknown", .. args[6..], "--force"]), Is.EqualTo(ExitCodes.Usage), "unknown signing method");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestCase(CiHost.GitHub, "linux-x64")]
    [TestCase(CiHost.Gitea, "win-x64")]
    public void Workflow_TakesVersionAndChannelFromTheTag_AndPublishesToThatChannel(CiHost host, string rid)
    {
        var yaml = CiTemplate.Generate(Options(host, CiSigning.Draft) with { Rid = rid });
        var build = yaml[yaml.IndexOf("  build:", StringComparison.Ordinal)..yaml.IndexOf("  publish:", StringComparison.Ordinal)];
        var publish = yaml[yaml.IndexOf("  publish:", StringComparison.Ordinal)..];

        Assert.That(yaml, Does.Contain("tags: ['v[0-9]*']"));
        Assert.That(build, Does.Contain("- name: Version and channel from the tag").And.Contain("id: tag"));
        Assert.That(build, Does.Contain(rid.StartsWith("win", StringComparison.Ordinal) ? CiTemplate.TagParsePwsh[0] : CiTemplate.TagParseBash[0]));
        Assert.That(build, Does.Contain("version: ${{ steps.tag.outputs.version }}").And.Contain("channel: ${{ steps.tag.outputs.channel }}"));
        Assert.That(publish, Does.Contain("VERSION: ${{ needs.build.outputs.version }}").And.Contain("CHANNEL: ${{ needs.build.outputs.channel }}"));
        Assert.That(publish, Does.Contain("--channel \"$CHANNEL\"").And.Not.Contain("GITHUB_REF_NAME"), "the publish job does not parse the tag again");
        Assert.That(build, Does.Contain("out/online/QuickNotes.Installer").And.Not.Contain("*.Installer"), "the installer is found by its assembly name");
        Assert.That(yaml.Contains("include-hidden-files: true", StringComparison.Ordinal), Is.EqualTo(host == CiHost.GitHub),
            "upload-artifact v4 leaves out dot-files unless asked; Gitea's v3 keeps them");
    }

    [Test]
    public void Links_UseTheDocsUrlCompiledFromTheBuild()
    {
        var yaml = CiTemplate.Generate(Options(CiHost.GitHub, CiSigning.Draft));

        Assert.That(Services.InstellaDocs.Url, Does.StartWith("https://").And.EndWith("/docs"), "InstellaDocsUrl assembly metadata");
        Assert.That(yaml, Does.Contain(Services.InstellaDocs.Page("publishing.md")));
        Assert.That(yaml, Does.Not.Contain("in the Instella repository"));
    }

    private static IEnumerable<TestCaseData> Tags() =>
    [
        new TestCaseData("v1.3.0", "1.3.0", "stable"),
        new TestCaseData("v1.3.0-beta", "1.3.0", "beta"),
        new TestCaseData("v1.3.0.2-rc.1", "1.3.0.2", "rc"),
        new TestCaseData("v1.3", null, null),
        new TestCaseData("v1.3.0-Beta_1", null, null),
    ];

    [TestCaseSource(nameof(Tags))]
    public void BashTagParsing(string tag, string? version, string? channel)
    {
        var bash = FindBash();
        if (bash is null) Assert.Ignore("bash is not available");
        AssertTagParsing(bash, ["-c", string.Join("\n", CiTemplate.TagParseBash)], tag, version, channel);
    }

    [TestCaseSource(nameof(Tags))]
    public void PwshTagParsing(string tag, string? version, string? channel)
    {
        var pwsh = FindOnPath(OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh");
        if (pwsh is null) Assert.Ignore("pwsh is not available");
        AssertTagParsing(pwsh, ["-NoProfile", "-NonInteractive", "-Command", string.Join("\n", CiTemplate.TagParsePwsh)], tag, version, channel);
    }

    private static void AssertTagParsing(string shell, string[] args, string tag, string? version, string? channel)
    {
        var dir = Directory.CreateTempSubdirectory("instella-tag-").FullName;
        try
        {
            var env = Path.Combine(dir, "env");
            var output = Path.Combine(dir, "output");
            File.WriteAllText(env, "");
            File.WriteAllText(output, "");
            var psi = new System.Diagnostics.ProcessStartInfo(shell) { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.Environment["GITHUB_REF_NAME"] = tag;
            psi.Environment["GITHUB_ENV"] = env;
            psi.Environment["GITHUB_OUTPUT"] = output;
            using var process = System.Diagnostics.Process.Start(psi)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            Assert.That(process.WaitForExit(60_000), Is.True, "the snippet finished");
            var log = stdout.Result + stderr.Result;

            if (version is null)
            {
                Assert.That(process.ExitCode, Is.Not.Zero, log);
                Assert.That(log, Does.Contain("::error::"));
                return;
            }
            Assert.That(process.ExitCode, Is.Zero, log);
            Assert.That(Lines(env), Is.EqualTo(new[] { $"VERSION={version}", $"CHANNEL={channel}" }));
            Assert.That(Lines(output), Is.EqualTo(new[] { $"version={version}", $"channel={channel}" }));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }

        static string[] Lines(string path) =>
            File.ReadAllLines(path).Select(l => l.Trim('\uFEFF', ' ')).Where(l => l.Length > 0).ToArray();
    }

    /// <summary>Git's bash on Windows (not WSL's launcher in System32), bash on the path elsewhere.</summary>
    private static string? FindBash()
    {
        if (!OperatingSystem.IsWindows()) return FindOnPath("bash");
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"C:\Program Files" })
        {
            var candidate = Path.Combine(root, "Git", "bin", "bash.exe");
            if (File.Exists(candidate)) return candidate;
        }
        var git = FindOnPath("git.exe");
        var fromGit = git is null ? null : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(git)!, "..", "bin", "bash.exe"));
        return fromGit is not null && File.Exists(fromGit) ? fromGit : null;
    }

    private static string? FindOnPath(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists);

    private static Task<int> Run(string[] args) => new RootCommand { CiCommand.Create() }.Parse(args).InvokeAsync();
}
