using System.CommandLine;
using System.Security.Cryptography;
using Instella.CLI.Commands;
using Instella.CLI.Services;
using Instella.Core.Platform;
using Instella.Core.Trust;
using Instella.Core.Wire;
using NUnit.Framework;

namespace Instella.CLI.Tests;

/// <summary>
/// <c>instella publish</c> refuses to sign a draft that does not match what the person expects;
/// <c>upload --draft</c> never signs.
/// </summary>
[TestFixture]
[NonParallelizable]
public class PublishCommandTests
{
    private string _dir = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = Directory.CreateTempSubdirectory("instella-publish-").FullName;
        File.WriteAllText(Path.Combine(_dir, "App.exe"), "app");
        Directory.CreateDirectory(Path.Combine(_dir, "lib"));
        File.WriteAllText(Path.Combine(_dir, "lib", "core.dll"), "core");
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_dir, recursive: true);

    [Test]
    public void Check_MatchingFilesAndInstaller_HasNoProblems()
    {
        var installer = Path.Combine(_dir, "..", Path.GetFileName(_dir) + "-setup.exe");
        File.WriteAllText(installer, "setup");
        try
        {
            var manifest = Manifest(installers: [new ReleaseInstaller(InstallerKinds.Online, "Setup.exe", 5, Sha("setup"))]);
            var problems = PublishCommand.Check(manifest, "com.app", new Version(1, 0, 0), TargetPlatform.Windows, Architecture.X64,
                new DirectoryInfo(_dir), new FileInfo(installer), offlineInstaller: null);
            Assert.That(problems, Is.Empty);
        }
        finally
        {
            File.Delete(installer);
        }
    }

    [Test]
    public void Check_ReportsChangedMissingAndExtraFiles_AndAWrongIdentity()
    {
        File.WriteAllText(Path.Combine(_dir, "lib", "core.dll"), "tampered");
        File.WriteAllText(Path.Combine(_dir, "extra.txt"), "x");
        var manifest = Manifest(extra: new ReleaseFile("missing.dll", 1, Sha("m")));

        var problems = PublishCommand.Check(manifest, "com.other", new Version(1, 0, 0), TargetPlatform.Linux, Architecture.X64,
            new DirectoryInfo(_dir), installer: null, offlineInstaller: null);

        Assert.That(problems, Has.Some.Contains("'lib/core.dll' differs"));
        Assert.That(problems, Has.Some.Contains("lists 'missing.dll'"));
        Assert.That(problems, Has.Some.Contains("'extra.txt' in --path is not in the draft"));
        Assert.That(problems, Has.Some.Contains("not 'com.other'"));
        Assert.That(problems, Has.Some.Contains("is for windows/x64"));
    }

    [Test]
    public async Task UploadDraft_WithASigningKey_IsAUsageError()
    {
        var saved = Environment.GetEnvironmentVariable(SigningKeyLoader.KeyEnvironmentVariable);
        Environment.SetEnvironmentVariable(SigningKeyLoader.KeyEnvironmentVariable, null);
        try
        {
            using var key = ReleaseKeys.Generate();
            var keyFile = Path.Combine(_dir, "..", Path.GetFileName(_dir) + ".pem");
            File.WriteAllText(keyFile, key.ExportPkcs8PrivateKeyPem());
            var root = new RootCommand { UploadCommand.Create() };
            var exit = await root.Parse(["upload", "--server", "http://127.0.0.1:1", "--api-key", "k", "--package", "p",
                "--version", "1.0.0", "--path", _dir, "--os", "windows", "--arch", "x64", "--draft", "--signing-key", keyFile]).InvokeAsync();
            File.Delete(keyFile);
            Assert.That(exit, Is.EqualTo(ExitCodes.Usage));
        }
        finally
        {
            Environment.SetEnvironmentVariable(SigningKeyLoader.KeyEnvironmentVariable, saved);
        }
    }

    [Test]
    public async Task Publish_WithoutAnyKey_IsRefusedBeforeContactingTheServer()
    {
        var saved = Environment.GetEnvironmentVariable(SigningKeyLoader.KeyEnvironmentVariable);
        Environment.SetEnvironmentVariable(SigningKeyLoader.KeyEnvironmentVariable, null);
        try
        {
            var root = new RootCommand { PublishCommand.Create() };
            var exit = await root.Parse(["publish", "--server", "http://127.0.0.1:1", "--api-key", "k", "--package", "p",
                "--version", "1.0.0", "--os", "windows", "--arch", "x64", "--yes"]).InvokeAsync();
            Assert.That(exit, Is.EqualTo(ExitCodes.Usage), "a server error (2) would mean it tried to connect");
        }
        finally
        {
            Environment.SetEnvironmentVariable(SigningKeyLoader.KeyEnvironmentVariable, saved);
        }
    }

    private static ReleaseManifest Manifest(IReadOnlyList<ReleaseInstaller>? installers = null, ReleaseFile? extra = null) => new()
    {
        FormatVersion = ReleaseManifest.CurrentFormatVersion,
        AppId = "com.app",
        Version = new Version(1, 0, 0),
        Os = "windows",
        Arch = "x64",
        Channel = "stable",
        CreatedAt = DateTimeOffset.UtcNow,
        Files = [new ReleaseFile("App.exe", 3, Sha("app")), new ReleaseFile("lib/core.dll", 4, Sha("core")), .. extra is null ? [] : new[] { extra }],
        Installers = installers,
    };

    private static string Sha(string s) => Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s)));
}
