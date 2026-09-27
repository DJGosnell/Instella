using System;
using System.IO;
using Instella.Installer.Runtime.Runners;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Installation;

/// <summary>
/// The one normalisation of install paths (the uninstall string, target binding and the
/// re-install probe all compare folders): <c>D:\Apps\QN\</c> once became <c>D:\Apps\QN"</c>
/// on the uninstall command line.
/// </summary>
[TestFixture]
public class InstallPathsTests
{
    [TestCase(@"D:\Apps\QN\", @"D:\Apps\QN")]
    [TestCase(@"D:\Apps\QN\\", @"D:\Apps\QN")]
    [TestCase(@"D:\Apps\QN", @"D:\Apps\QN")]
    [TestCase(@"C:\", @"C:\")]
    [TestCase(@"C:\a\..\b", @"C:\b")]
    [TestCase(@"C:/Apps/QN/", @"C:\Apps\QN")]
    [Platform("Win")]
    public void Normalizes(string raw, string expected)
    {
        Assert.That(InstallPaths.TryNormalize(raw, requireRooted: true, out var path, out var problem), Is.True, problem);
        Assert.That(path, Is.EqualTo(expected));
    }

    [Test]
    public void RelativePath_IsRootedAgainstTheCurrentDirectory_WhenAllowed()
    {
        Assert.That(InstallPaths.TryNormalize("apps" + Path.DirectorySeparatorChar + "qn", requireRooted: false, out var path, out _), Is.True);
        Assert.That(path, Is.EqualTo(Path.Combine(Environment.CurrentDirectory, "apps", "qn")));
    }

    [Test]
    public void RelativePath_IsRefused_WhenARootIsRequired()
    {
        Assert.That(InstallPaths.TryNormalize("apps", requireRooted: true, out _, out var problem), Is.False);
        Assert.That(problem, Does.Contain("not a full folder path"));
    }

    [TestCase(@"\\?\C:\x")]
    [TestCase(@"\\.\C:\x")]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase(null)]
    public void Refused(string? raw)
    {
        Assert.That(InstallPaths.TryNormalize(raw, requireRooted: false, out var path, out var problem), Is.False);
        Assert.That(path, Is.Null);
        Assert.That(problem, Is.Not.Empty);
    }

    [Test]
    [Platform("Win")]
    public void SameFolder_IgnoresCaseOnWindows() =>
        Assert.That(InstallPaths.SameFolder(@"C:\Apps\QN", @"c:\apps\qn"), Is.True);

    [Test]
    [Platform(Exclude = "Win")]
    public void SameFolder_IsCaseSensitiveElsewhere() =>
        Assert.That(InstallPaths.SameFolder("/opt/QN", "/opt/qn"), Is.False);
}
