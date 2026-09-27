using System.CommandLine;
using Instella.CLI.Commands;
using Instella.CLI.Templates;
using Instella.Core.Platform;
using NUnit.Framework;

namespace Instella.CLI.Tests;

/// <summary>Parse + exit-code behaviour of the CLI commands that needs no server.</summary>
[TestFixture]
[NonParallelizable]
public class CommandTests
{
    private string _dir = null!;
    private string? _savedKeyEnv;

    [SetUp]
    public void SetUp()
    {
        _dir = Directory.CreateTempSubdirectory("instella-cli-").FullName;
        File.WriteAllText(Path.Combine(_dir, "app.exe"), "x");
        _savedKeyEnv = Environment.GetEnvironmentVariable("INSTELLA_SIGNING_KEY");
        Environment.SetEnvironmentVariable("INSTELLA_SIGNING_KEY", null);
    }

    [TearDown]
    public void TearDown()
    {
        Environment.SetEnvironmentVariable("INSTELLA_SIGNING_KEY", _savedKeyEnv);
        Directory.Delete(_dir, recursive: true);
    }

    private static Task<int> Run(params string[] args)
    {
        var root = new RootCommand
        {
            InitCommand.Create(), KeysCommand.Create(), UploadCommand.Create(), ListCommand.Create(), DeleteCommand.Create(),
        };
        return root.Parse(args).InvokeAsync();
    }

    [Test]
    public async Task Init_WithASpacedName_PrintsTheRealFolder_AndNumbersItsSteps()
    {
        // "Quick Notes" creates QuickNotes.Installer, and the printed next step must name that folder.
        var output = new StringWriter();
        var saved = Console.Out;
        Console.SetOut(output);
        int exit;
        try
        {
            exit = await Run("init", "--name", "Quick Notes", "--output", _dir);
        }
        finally
        {
            Console.SetOut(saved);
        }

        Assert.That(exit, Is.EqualTo(ExitCodes.Success));
        var text = output.ToString();
        Assert.That(text, Does.Contain("1. cd QuickNotes.Installer "));
        Assert.That(text, Does.Contain("2. Run 'instella keys generate --out").And.Contain(".instella"));
        Assert.That(text, Does.Contain("3. Run 'dotnet publish"));
        Assert.That(Directory.Exists(Path.Combine(_dir, "QuickNotes.Installer")), Is.True);
    }

    [Test]
    public async Task Init_WithAPublisherKey_NumbersTheRemainingSteps()
    {
        var output = new StringWriter();
        var saved = Console.Out;
        Console.SetOut(output);
        try
        {
            await Run("init", "--name", "App", "--output", _dir, "--publisher-key", "BASE64KEY");
        }
        finally
        {
            Console.SetOut(saved);
        }

        Assert.That(output.ToString(), Does.Contain("2. Run 'dotnet publish").And.Not.Contain("3."));
    }

    [Test]
    public async Task Init_WithApp_ReferencesItRelativeToTheInstaller()
    {
        var app = Path.Combine(_dir, "src", "Notes", "Notes.csproj");

        await Run("init", "--name", "Notes", "--output", Path.Combine(_dir, "installer"), "--app", app);

        var csproj = File.ReadAllText(Path.Combine(_dir, "installer", "Notes.Installer", "Notes.Installer.csproj"));
        Assert.That(csproj, Does.Contain(Path.Combine("..", "..", "src", "Notes", "Notes.csproj")));
    }

    [Test]
    public async Task Upload_WithoutSigningKey_IsRefusedBeforeAnyNetworkCall()
    {
        // Port 1 on loopback: if the command tried to connect, it would fail with a server
        // error (2), not a usage error (1).
        var exit = await Run("upload", "--server", "http://127.0.0.1:1", "--api-key", "k", "--package", "p",
            "--version", "1.0.0", "--path", _dir, "--os", "windows", "--arch", "x64");
        Assert.That(exit, Is.EqualTo(ExitCodes.Usage));
    }

    [TestCase("Beta_1")]
    [TestCase("-rc")]
    [TestCase("abcdefghijabcdefghijabcdefghijabc")]
    public async Task Upload_AnInvalidChannel_IsRefusedWithTheRule(string channel)
    {
        // The same rule as the server, checked before any network call.
        var error = new StringWriter();
        var saved = Console.Error;
        Console.SetError(error);
        int exit;
        try
        {
            exit = await Run("upload", "--server", "http://127.0.0.1:1", "--api-key", "k", "--package", "p",
                "--version", "1.0.0", "--path", _dir, "--os", "windows", "--arch", "x64", "--unsigned", "--channel", channel);
        }
        finally
        {
            Console.SetError(saved);
        }

        Assert.That(exit, Is.EqualTo(ExitCodes.Usage));
        Assert.That(error.ToString(), Does.Contain(Instella.Core.Wire.ChannelNames.Rule));
    }

    [TestCase("latest")]
    [TestCase("1.2-beta")]
    public async Task Delete_AnInvalidVersion_IsRefused(string version)
    {
        var exit = await Run("delete", "--server", "http://127.0.0.1:1", "--api-key", "k", "--package", "p", "--version", version, "--yes");

        Assert.That(exit, Is.EqualTo(ExitCodes.Usage));
    }

    [Test]
    public async Task Upload_PlainHttpToRemoteHost_IsRefused()
    {
        var exit = await Run("upload", "--server", "http://updates.example.com", "--api-key", "k", "--package", "p",
            "--version", "1.0.0", "--path", _dir, "--os", "windows", "--arch", "x64", "--unsigned");
        Assert.That(exit, Is.EqualTo(ExitCodes.Usage));
    }

    [Test]
    public async Task Upload_UnknownArchitecture_IsAUsageError()
    {
        var exit = await Run("upload", "--server", "http://127.0.0.1:1", "--api-key", "k", "--package", "p",
            "--version", "1.0.0", "--path", _dir, "--os", "windows", "--arch", "sparc", "--unsigned");
        Assert.That(exit, Is.EqualTo(ExitCodes.Usage));
    }

    [Test]
    public async Task Upload_InvalidTrustedKey_IsAUsageError()
    {
        var keyFile = Path.Combine(_dir, "k.pem");
        Assert.That(await Run("keys", "generate", "--out", keyFile), Is.EqualTo(ExitCodes.Success));
        var exit = await Run("upload", "--server", "http://127.0.0.1:1", "--api-key", "k", "--package", "p",
            "--version", "1.0.0", "--path", _dir, "--os", "windows", "--arch", "x64", "--signing-key", keyFile,
            "--trusted-key", "not-a-key");
        Assert.That(exit, Is.EqualTo(ExitCodes.Usage));
    }

    [Test]
    public async Task Upload_TrustedKeyWithoutSigningKey_IsAUsageError()
    {
        using var key = Core.Trust.ReleaseKeys.Generate();
        var exit = await Run("upload", "--server", "http://127.0.0.1:1", "--api-key", "k", "--package", "p",
            "--version", "1.0.0", "--path", _dir, "--os", "windows", "--arch", "x64", "--unsigned",
            "--trusted-key", Core.Trust.ReleaseKeys.PublicKeyOf(key).PublicKey);
        Assert.That(exit, Is.EqualTo(ExitCodes.Usage));
    }

    [Test]
    public void TrustedKeys_AreParsedDeduplicatedAndKeyed()
    {
        using var a = Core.Trust.ReleaseKeys.Generate();
        using var b = Core.Trust.ReleaseKeys.Generate();
        var pa = Core.Trust.ReleaseKeys.PublicKeyOf(a);
        var pb = Core.Trust.ReleaseKeys.PublicKeyOf(b);

        Assert.That(UploadCommand.TryParseTrustedKeys([], out var none), Is.True);
        Assert.That(none, Is.Null, "no flag means no rotation");
        Assert.That(UploadCommand.TryParseTrustedKeys([pa.PublicKey, pb.PublicKey, pa.PublicKey], out var keys), Is.True);
        Assert.That(keys, Is.EqualTo(new[] { pa, pb }));
    }

    [Test]
    public async Task List_UnreachableServer_ExitsNonZero()
    {
        var exit = await Run("list", "packages", "--server", "http://127.0.0.1:1");
        Assert.That(exit, Is.EqualTo(ExitCodes.Server));
    }

    [Test]
    public async Task Delete_WithoutYesOnRedirectedInput_IsRefused()
    {
        if (!Console.IsInputRedirected) Assert.Ignore("stdin is interactive in this run");
        var exit = await Run("delete", "--server", "http://127.0.0.1:1", "--api-key", "k", "--package", "p", "--version", "1.0.0");
        Assert.That(exit, Is.EqualTo(ExitCodes.Usage));
    }

    [Test]
    public async Task Keys_GenerateThenShow_RoundTrips_AndRefusesOverwrite()
    {
        var keyFile = Path.Combine(_dir, "publisher.key.pem");
        Assert.That(await Run("keys", "generate", "--out", keyFile), Is.EqualTo(ExitCodes.Success));
        Assert.That(File.ReadAllText(keyFile), Does.Contain("BEGIN PRIVATE KEY"));
        Assert.That(await Run("keys", "show", "--key", keyFile), Is.EqualTo(ExitCodes.Success));
        Assert.That(await Run("keys", "generate", "--out", keyFile), Is.EqualTo(ExitCodes.Usage));
        Assert.That(await Run("keys", "generate", "--out", keyFile, "--force"), Is.EqualTo(ExitCodes.Success));
    }

    [Test]
    public async Task Keys_GenerateWithPassword_WritesEncryptedPem()
    {
        var keyFile = Path.Combine(_dir, "enc.key.pem");
        Environment.SetEnvironmentVariable("INSTELLA_TEST_KEY_PW", "s3cret");
        try
        {
            Assert.That(await Run("keys", "generate", "--out", keyFile, "--password-env", "INSTELLA_TEST_KEY_PW"), Is.EqualTo(0));
            Assert.That(File.ReadAllText(keyFile), Does.Contain("ENCRYPTED PRIVATE KEY"));
            Assert.That(await Run("keys", "show", "--key", keyFile, "--password-env", "INSTELLA_TEST_KEY_PW"), Is.EqualTo(0));
            Assert.That(await Run("keys", "show", "--key", keyFile), Is.EqualTo(ExitCodes.Usage));
        }
        finally
        {
            Environment.SetEnvironmentVariable("INSTELLA_TEST_KEY_PW", null);
        }
    }

    [TestCase(@"C:\build\bin\Release\net10.0\win-x64\publish", TargetPlatform.Windows, Architecture.X64)]
    [TestCase("/home/ci/out/linux-arm64/publish", TargetPlatform.Linux, Architecture.ARM64)]
    [TestCase("/out/osx-arm64", TargetPlatform.MacOS, Architecture.ARM64)]
    [TestCase("/out/linux-arm", TargetPlatform.Linux, Architecture.ARM32)]
    public void RidDetection(string path, TargetPlatform os, Architecture arch)
    {
        var (detectedOs, detectedArch) = UploadCommand.TryDetectRidFromPath(path.Replace('\\', Path.DirectorySeparatorChar));
        Assert.That(detectedOs, Is.EqualTo(os));
        Assert.That(detectedArch, Is.EqualTo(arch));
    }

    [Test]
    public async Task Init_Template_ReferencesThePublisherKeyAndTheCliVersion()
    {
        await ProjectTemplate.ExtractAsync(_dir, new TemplateOptions { AppName = "Demo", PublisherKey = "BASE64KEY" });
        var program = File.ReadAllText(Path.Combine(_dir, "Demo.Installer", "Program.cs"));
        var csproj = File.ReadAllText(Path.Combine(_dir, "Demo.Installer", "Demo.Installer.csproj"));
        Assert.That(program, Does.Contain(".WithPublisherKey(\"BASE64KEY\")"));
        Assert.That(csproj, Does.Contain($"Version=\"{ProjectTemplate.PackageVersion}\""));
        Assert.That(ProjectTemplate.PackageVersion, Does.Not.Contain("+"));
    }
}
