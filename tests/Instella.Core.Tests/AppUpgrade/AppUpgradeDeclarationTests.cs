using System.Text;
using Instella.Core.Installation;
using Instella.Core.Platform;
using NUnit.Framework;

namespace Instella.Core.Tests.AppUpgrade;

/// <summary>
/// <c>instella-upgrade.json</c>: what a valid declaration reads as, and every declaration a reader
/// must refuse (the operation then fails and rolls back rather than skipping the app's upgrade).
/// </summary>
[TestFixture]
public class AppUpgradeDeclarationTests
{
    private static AppUpgradeDeclarationRead Parse(string json, TargetPlatform platform = TargetPlatform.Windows) =>
        AppUpgradeDeclarations.Parse(Encoding.UTF8.GetBytes(json), platform);

    private static string Refusal(string json, TargetPlatform platform = TargetPlatform.Windows)
    {
        var read = Parse(json, platform);
        Assert.That(read, Is.InstanceOf<AppUpgradeDeclarationRead.Refused>(), $"expected a refusal of {json}");
        var reason = ((AppUpgradeDeclarationRead.Refused)read).Reason;
        Assert.That(reason, Does.StartWith("instella-upgrade.json "), "the message names the file");
        return reason;
    }

    [Test]
    public void AFullDeclaration_ReadsEveryField()
    {
        var read = Parse("""
            {
              "contractVersion": 1,
              "program": "tools/App.Upgrade.exe",
              "arguments": ["--verbose", "a b"],
              "timeoutMinutes": 90,
              "handlesUninstall": true
            }
            """);

        var valid = (AppUpgradeDeclarationRead.Valid)read;
        Assert.Multiple(() =>
        {
            Assert.That(valid.ProgramRelativePath, Is.EqualTo("tools/App.Upgrade.exe"));
            Assert.That(valid.Declaration.ContractVersion, Is.EqualTo(1));
            Assert.That(valid.Declaration.Program, Is.EqualTo("tools/App.Upgrade.exe"));
            Assert.That(valid.Declaration.Arguments, Is.EqualTo(new[] { "--verbose", "a b" }));
            Assert.That(valid.Declaration.TimeoutMinutes, Is.EqualTo(90));
            Assert.That(valid.Declaration.HandlesUninstall, Is.True);
        });
    }

    [Test]
    public void OptionalFields_TakeTheirDefaults()
    {
        var valid = (AppUpgradeDeclarationRead.Valid)Parse("""{"contractVersion":1,"program":"App.Upgrade"}""");
        Assert.Multiple(() =>
        {
            Assert.That(valid.Declaration.Arguments, Is.Empty);
            Assert.That(valid.Declaration.TimeoutMinutes, Is.EqualTo(30));
            Assert.That(valid.Declaration.HandlesUninstall, Is.False);
        });
    }

    [Test]
    public void UnknownFields_CommentsAndTrailingCommas_AreAccepted()
    {
        var read = Parse("""
            {
              // written by a later SDK
              "contractVersion": 1,
              "program": "App.Upgrade.exe",
              "somethingNew": { "x": 1 },
            }
            """);
        Assert.That(read, Is.InstanceOf<AppUpgradeDeclarationRead.Valid>());
    }

    [TestCase("App.Upgrade", TargetPlatform.Windows, "App.Upgrade.exe")]
    [TestCase("App.Upgrade.exe", TargetPlatform.Windows, "App.Upgrade.exe")]
    [TestCase("App.Upgrade.EXE", TargetPlatform.Windows, "App.Upgrade.EXE")]
    [TestCase("App.Upgrade", TargetPlatform.Linux, "App.Upgrade")]
    [TestCase("App.Upgrade.exe", TargetPlatform.Linux, "App.Upgrade")]
    [TestCase("App.Upgrade.exe", TargetPlatform.MacOS, "App.Upgrade")]
    [TestCase(@"tools\App.Upgrade", TargetPlatform.Windows, "tools/App.Upgrade.exe")]
    public void TheProgramName_FollowsThePlatform(string program, TargetPlatform platform, string expected)
    {
        var valid = (AppUpgradeDeclarationRead.Valid)Parse($$"""{"contractVersion":1,"program":{{System.Text.Json.JsonSerializer.Serialize(program)}}}""", platform);
        Assert.That(valid.ProgramRelativePath, Is.EqualTo(expected));
    }

    [Test]
    public void NotJson_IsRefused() => Assert.That(Refusal("not json"), Does.Contain("is not valid"));

    [Test]
    public void AnArray_IsRefused() => Assert.That(Refusal("[1,2]"), Does.Contain("is not valid"));

    [Test]
    public void Null_IsRefused() => Assert.That(Refusal("null"), Does.Contain("is not a JSON object"));

    [Test]
    public void AFieldOfTheWrongType_IsRefused() =>
        Assert.That(Refusal("""{"contractVersion":1,"program":"a","timeoutMinutes":"soon"}"""), Does.Contain("is not valid"));

    [Test]
    public void AMissingContractVersion_IsRefused() =>
        Assert.That(Refusal("""{"program":"App.Upgrade"}"""), Does.Contain("has no contractVersion"));

    [TestCase(0)]
    [TestCase(-1)]
    public void AContractVersionBelowOne_IsRefused(int version) =>
        Assert.That(Refusal($$"""{"contractVersion":{{version}},"program":"App.Upgrade"}"""), Does.Contain("does not exist"));

    [Test]
    public void ANewerContractVersion_IsRefused_AndPointsAtTheLatestInstaller()
    {
        var reason = Refusal("""{"contractVersion":2,"program":"App.Upgrade"}""");
        Assert.That(reason, Does.Contain("contract version 2").And.Contain("supports up to 1").And.Contain("latest installer"));
    }

    [TestCase("""{"contractVersion":1}""", "names no program")]
    [TestCase("""{"contractVersion":1,"program":""}""", "names no program")]
    [TestCase("""{"contractVersion":1,"program":"   "}""", "names no program")]
    [TestCase("""{"contractVersion":1,"program":"C:\\tools\\up.exe"}""", "is absolute")]
    [TestCase("""{"contractVersion":1,"program":"/usr/bin/up"}""", "is absolute")]
    [TestCase("""{"contractVersion":1,"program":"../up.exe"}""", "illegal segment")]
    [TestCase("""{"contractVersion":1,"program":"tools/../../up.exe"}""", "illegal segment")]
    [TestCase("""{"contractVersion":1,"program":".instella/up.exe"}""", "belongs to Instella")]
    [TestCase("""{"contractVersion":1,"program":"instella.exe"}""", "belongs to Instella")]
    [TestCase("""{"contractVersion":1,"program":"instella"}""", "belongs to Instella")]
    [TestCase("""{"contractVersion":1,"program":".instella-manifest.json"}""", "belongs to Instella")]
    public void AnUnusableProgram_IsRefused(string json, string expected) =>
        Assert.That(Refusal(json), Does.Contain(expected));

    [Test]
    public void AProgramThatIsOnlyAnExeExtension_IsRefusedWhereTheExtensionIsDropped() =>
        Assert.That(Refusal("""{"contractVersion":1,"program":"tools/.exe"}""", TargetPlatform.Linux),
            Does.Contain("not a usable file name"));

    [Test]
    public void ANullArgument_IsRefused() =>
        Assert.That(Refusal("""{"contractVersion":1,"program":"a","arguments":["x",null]}"""), Does.Contain("null entry"));

    [Test]
    public void AnArgumentWithNul_IsRefused() =>
        Assert.That(Refusal("""{"contractVersion":1,"program":"a","arguments":["x\u0000y"]}"""), Does.Contain("NUL"));

    [TestCase(0)]
    [TestCase(1441)]
    [TestCase(-5)]
    public void ATimeoutOutOfRange_IsRefused(int minutes) =>
        Assert.That(Refusal($$"""{"contractVersion":1,"program":"a","timeoutMinutes":{{minutes}}}"""),
            Does.Contain("from 1 to 1440"));

    [TestCase(1)]
    [TestCase(1440)]
    public void TheTimeoutLimits_AreAccepted(int minutes) =>
        Assert.That(((AppUpgradeDeclarationRead.Valid)Parse($$"""{"contractVersion":1,"program":"a","timeoutMinutes":{{minutes}}}""")).Declaration.TimeoutMinutes,
            Is.EqualTo(minutes));

    [Test]
    public async Task ReadAsync_WithoutAFile_IsAbsent()
    {
        var root = Directory.CreateTempSubdirectory("instella-decl-").FullName;
        try
        {
            var read = await AppUpgradeDeclarations.ReadAsync(Instella.Core.FileSystem.RealFileSystem.Instance, root, TargetPlatform.Windows, CancellationToken.None);
            Assert.That(read, Is.InstanceOf<AppUpgradeDeclarationRead.Absent>());

            await File.WriteAllTextAsync(Path.Combine(root, "instella-upgrade.json"), """{"contractVersion":1,"program":"App.Upgrade"}""");
            read = await AppUpgradeDeclarations.ReadAsync(Instella.Core.FileSystem.RealFileSystem.Instance, root, TargetPlatform.Windows, CancellationToken.None);
            Assert.That(((AppUpgradeDeclarationRead.Valid)read).ProgramRelativePath, Is.EqualTo("App.Upgrade.exe"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
