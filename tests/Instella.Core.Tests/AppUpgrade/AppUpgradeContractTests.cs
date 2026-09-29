using Instella.Core.Installation;
using NUnit.Framework;

namespace Instella.Core.Tests.AppUpgrade;

/// <summary>
/// The launch contract, version 1: the exact arguments for every operation, and how progress
/// lines are read. Both are frozen formats (docs/compatibility.md).
/// </summary>
[TestFixture]
internal class AppUpgradeContractTests
{
    private const string Path = @"C:\Program Files\App";

    private static IReadOnlyList<string> Args(AppUpgradeLaunchMode mode, Version? from, Version? to,
        InstallationScope scope = InstallationScope.PerUser, params string[] extra) =>
        AppUpgradeContract.BuildArguments(new AppUpgradeLaunch(mode, from, to, scope, Path, "com.example.app"), extra);

    [Test]
    public void AFirstInstall_HasNoFromVersion()
    {
        Assert.That(Args(AppUpgradeLaunchMode.FirstInstall, null, new Version(1, 2, 0)), Is.EqualTo(new[]
        {
            "--instella-upgrade", "--contract", "1", "--mode", "first-install", "--from", "none", "--to", "1.2.0",
            "--scope", "user", "--install-path", Path, "--app-id", "com.example.app",
        }));
    }

    [TestCase(AppUpgradeLaunchMode.Upgrade, "1.0.0", "2.0.0", "upgrade")]
    [TestCase(AppUpgradeLaunchMode.Downgrade, "2.0.0", "1.0.0", "downgrade")]
    [TestCase(AppUpgradeLaunchMode.Repair, "1.5.0", "1.5.0", "repair")]
    [TestCase(AppUpgradeLaunchMode.Update, "1.5.0", "1.6.0", "update")]
    public void EveryUpgradeMode_UsesTheUpgradeSwitch(AppUpgradeLaunchMode mode, string from, string to, string name)
    {
        var args = Args(mode, Version.Parse(from), Version.Parse(to), InstallationScope.SystemWide);
        Assert.That(args, Is.EqualTo(new[]
        {
            "--instella-upgrade", "--contract", "1", "--mode", name, "--from", from, "--to", to,
            "--scope", "machine", "--install-path", Path, "--app-id", "com.example.app",
        }));
    }

    [Test]
    public void Uninstall_UsesTheUninstallSwitch_AndHasNoToVersion()
    {
        Assert.That(Args(AppUpgradeLaunchMode.Uninstall, new Version(3, 1), null), Is.EqualTo(new[]
        {
            "--instella-uninstall", "--contract", "1", "--mode", "uninstall", "--from", "3.1.0", "--to", "none",
            "--scope", "user", "--install-path", Path, "--app-id", "com.example.app",
        }));
    }

    [Test]
    public void Versions_AreCanonical()
    {
        var args = Args(AppUpgradeLaunchMode.Upgrade, new Version(1, 2), new Version(1, 3, 0, 0));
        Assert.That(args[6], Is.EqualTo("1.2.0"));
        Assert.That(args[8], Is.EqualTo("1.3.0"));
        Assert.That(Args(AppUpgradeLaunchMode.Upgrade, new Version(1, 2), new Version(1, 3, 0, 4))[8], Is.EqualTo("1.3.0.4"));
    }

    [Test]
    public void DeclaredArguments_ComeLast_Unchanged()
    {
        var args = Args(AppUpgradeLaunchMode.Upgrade, new Version(1, 0), new Version(2, 0), InstallationScope.PerUser, "--verbose", "a b", "");
        Assert.That(args.Skip(15), Is.EqualTo(new[] { "--verbose", "a b", "" }));
    }

    [Test]
    public void EveryModeName_RoundTrips()
    {
        foreach (var mode in Enum.GetValues<AppUpgradeLaunchMode>())
        {
            Assert.That(AppUpgradeContract.TryParseMode(AppUpgradeContract.ModeName(mode), out var parsed), Is.True);
            Assert.That(parsed, Is.EqualTo(mode));
        }
        Assert.That(AppUpgradeContract.TryParseMode("Upgrade", out _), Is.False, "mode names are exact and lower case");
        Assert.That(AppUpgradeContract.TryParseMode(null, out _), Is.False);
    }

    [Test]
    public void TheReservedExitCodes_AreFrozen()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AppUpgradeContract.ExitSuccess, Is.EqualTo(0));
            Assert.That(AppUpgradeContract.ExitFailed, Is.EqualTo(1));
            Assert.That(AppUpgradeContract.ExitRefused, Is.EqualTo(2));
            Assert.That(AppUpgradeContract.ExitNotUnderstood, Is.EqualTo(3));
            Assert.That(AppUpgradeContract.CurrentVersion, Is.EqualTo(1));
            Assert.That(AppUpgradeContract.DeclarationFileName, Is.EqualTo("instella-upgrade.json"));
        });
    }

    [TestCase("Migrating table 3 of 7")]
    [TestCase("")]
    [TestCase("  ##instella progress 5 indented lines are ordinary")]
    [TestCase("##instellaprogress 5")]
    [TestCase("#instella progress 5")]
    public void OrdinaryLines_AreNotInstructions(string line)
    {
        Assert.That(AppUpgradeContract.TryParseInstruction(line, out _, out var problem), Is.False);
        Assert.That(problem, Is.Null);
    }

    [TestCase("##instella progress 40 Migrating the database", 40, "Migrating the database")]
    [TestCase("##instella progress 40", 40, null)]
    [TestCase("##instella progress 100 Done ", 100, "Done")]
    [TestCase("##instella progress 0", 0, null)]
    [TestCase("##instella progress -5 x", 0, "x")]
    [TestCase("##instella progress 250 x", 100, "x")]
    [TestCase("##instella progress 12.6 x", 13, "x")]
    [TestCase("##instella  progress  7  spaced   text", 7, "spaced   text")]
    public void ProgressLines_AreRead(string line, int percent, string? text)
    {
        Assert.That(AppUpgradeContract.TryParseInstruction(line, out var progress, out var problem), Is.True, problem);
        Assert.That(progress, Is.EqualTo(new AppUpgradeProgressLine(percent, text)));
    }

    [TestCase("##instella", "without an instruction")]
    [TestCase("##instella ", "without an instruction")]
    [TestCase("##instella progress", "percentage")]
    [TestCase("##instella progress abc text", "percentage")]
    [TestCase("##instella progress NaN", "percentage")]
    [TestCase("##instella frobnicate 3", "unknown instruction 'frobnicate'")]
    [TestCase("##instella Progress 3", "unknown instruction 'Progress'")]
    public void MalformedInstructions_AreProblems_NotErrors(string line, string expected)
    {
        Assert.That(AppUpgradeContract.TryParseInstruction(line, out _, out var problem), Is.False);
        Assert.That(problem, Does.Contain(expected));
    }

    [Test]
    public void FormatProgress_WritesOneLineThatReadsBack()
    {
        var line = AppUpgradeContract.FormatProgress(55, "two\r\nlines\nhere");
        Assert.That(line, Is.EqualTo("##instella progress 55 two lines here"));
        Assert.That(AppUpgradeContract.TryParseInstruction(line, out var progress, out _), Is.True);
        Assert.That(progress, Is.EqualTo(new AppUpgradeProgressLine(55, "two lines here")));
        Assert.That(AppUpgradeContract.FormatProgress(120, null), Is.EqualTo("##instella progress 100"));
    }
}
