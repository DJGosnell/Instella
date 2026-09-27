using System;
using System.IO;
using Instella.Installer.Runtime.Core.Processes;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Runners;

/// <summary>
/// After an update of a machine-wide install the updater is elevated, and a process it starts
/// directly would inherit its admin token. An elevated launcher therefore starts the app through
/// Explorer, which runs as the signed-in user, when there is an unelevated token to fall back to.
/// </summary>
[TestFixture]
public class AppLauncherTests
{
    [Test]
    public void Elevated_StartsTheAppThroughExplorer_ByAbsolutePath_WithoutArguments()
    {
        var psi = AppLauncher.BuildStartInfo(@"C:\Program Files\QN\QuickNotes.exe", @"C:\Program Files\QN", dropElevation: true);

        Assert.That(psi.FileName, Is.EqualTo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")));
        Assert.That(Path.IsPathFullyQualified(psi.FileName) || !OperatingSystem.IsWindows(), Is.True, "never a search-path lookup");
        Assert.That(psi.ArgumentList, Is.EqualTo(new[] { @"C:\Program Files\QN\QuickNotes.exe" }));
        Assert.That(psi.UseShellExecute, Is.False);
    }

    [TestCase(true, (int)TokenElevationType.Full, true, TestName = "Elevated half of a UAC pair: through Explorer")]
    [TestCase(true, (int)TokenElevationType.Default, false, TestName = "UAC off or built-in Administrator: directly")]
    [TestCase(false, (int)TokenElevationType.Limited, false, TestName = "Not elevated: directly")]
    [TestCase(false, (int)TokenElevationType.Default, false, TestName = "Standard user without UAC: directly")]
    public void DropsElevation_OnlyWhenAnUnelevatedTokenExists(bool privileged, int elevationType, bool expected)
    {
        // With UAC off there is no unelevated token, and Explorer may not be running at all (a
        // service session or a headless build machine), where the Explorer route starts nothing.
        Assert.That(AppLauncher.ShouldDropElevation(privileged, (TokenElevationType)elevationType), Is.EqualTo(expected));
    }

    [Test]
    public void TheCurrentProcessHasAReadableElevationType()
    {
        if (OperatingSystem.IsWindows())
            Assert.That(Enum.IsDefined(TokenElevation.Current()), Is.True);
        else
            Assert.Ignore("Windows tokens only");
    }

    [Test]
    public void NotElevated_StartsTheAppDirectly_InItsFolder()
    {
        var psi = AppLauncher.BuildStartInfo(@"C:\Users\me\QN\QuickNotes.exe", @"C:\Users\me\QN", dropElevation: false);

        Assert.That(psi.FileName, Is.EqualTo(@"C:\Users\me\QN\QuickNotes.exe"));
        Assert.That(psi.ArgumentList, Is.Empty);
        Assert.That(psi.WorkingDirectory, Is.EqualTo(@"C:\Users\me\QN"));
    }
}
