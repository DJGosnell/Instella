using System.Reflection;
using Instella.Core.Installation;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Installation;

/// <summary>
/// Validates the arg-string → <see cref="InstallerMode"/> dispatch table. The
/// dispatcher type is internal; access via reflection through the
/// <c>InternalsVisibleTo</c> declared in <c>Instella.Installer.Runtime.csproj</c>.
/// </summary>
[TestFixture]
public sealed class ModeDispatcherTests
{
    private static readonly Assembly RuntimeAsm = typeof(Instella.Installer.Runtime.Builders.InstellaInstaller).Assembly;
    private static readonly System.Type DispatcherType =
        RuntimeAsm.GetType("Instella.Installer.Runtime.Runners.ModeDispatcher", throwOnError: true)!;
    private static readonly System.Type ResultType =
        RuntimeAsm.GetType("Instella.Installer.Runtime.Runners.DispatchResult", throwOnError: true)!;
    private static readonly System.Type KindType =
        RuntimeAsm.GetType("Instella.Installer.Runtime.Runners.DispatchKind", throwOnError: true)!;

    private static object Resolve(string[] args, bool? siblingManifestProbe = null)
    {
        var method = DispatcherType.GetMethod("Resolve", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        return method.Invoke(null, new object?[] { args, siblingManifestProbe })!;
    }

    private static InstallerMode ModeOf(object result) => (InstallerMode)ResultType.GetProperty("Mode")!.GetValue(result)!;
    private static string? PathOf(object result) => (string?)ResultType.GetProperty("InstallPath")!.GetValue(result);
    private static bool SilentOf(object result) => (bool)ResultType.GetProperty("IsSilent")!.GetValue(result)!;
    private static object KindOf(object result) => ResultType.GetProperty("Kind")!.GetValue(result)!;

    [Test]
    public void Help_short_circuits()
    {
        var r = Resolve(new[] { "--help" });
        Assert.That(KindOf(r).ToString(), Is.EqualTo("Help"));
    }

    [Test]
    public void Help_alternateForms()
    {
        foreach (var arg in new[] { "-h", "-?", "/?" })
        {
            var r = Resolve(new[] { arg });
            Assert.That(KindOf(r).ToString(), Is.EqualTo("Help"), $"form: {arg}");
        }
    }

    [Test]
    public void Uninstall_flag_setsMode()
    {
        var r = Resolve(new[] { "--uninstall", "--path", "C:\\app" });
        Assert.That(ModeOf(r), Is.EqualTo(InstallerMode.Uninstall));
        Assert.That(PathOf(r), Is.EqualTo("C:\\app"));
    }

    [Test]
    [Platform("Win")]
    public void Path_TrailingBackslash_IsStripped()
    {
        var r = Resolve(new[] { "--uninstall", "--path", @"D:\Apps\QN\" });
        Assert.That(PathOf(r), Is.EqualTo(@"D:\Apps\QN"));
    }

    [Test]
    public void Path_Relative_IsRootedAgainstTheCurrentDirectory()
    {
        var r = Resolve(new[] { "--install", "--path", "apps" });
        Assert.That(PathOf(r), Is.EqualTo(System.IO.Path.Combine(System.Environment.CurrentDirectory, "apps")));
    }

    [Test]
    public void Path_DevicePath_IsInvalid()
    {
        var r = Resolve(new[] { "--install", "--path", @"\\?\C:\x" });
        Assert.That(KindOf(r).ToString(), Is.EqualTo("Invalid"));
        Assert.That((string?)ResultType.GetProperty("Problem")!.GetValue(r), Does.Contain("device path"));
    }

    [Test]
    public void ExtraArgs_AreTheAppsAndNeverChangeTheMode()
    {
        var r = Resolve(new[] { "--update", "--app-path", "X", "--extra-args", "--uninstall", "--path", "Y", "--help" });
        Assert.That(KindOf(r).ToString(), Is.EqualTo("Mode"));
        Assert.That(ModeOf(r), Is.EqualTo(InstallerMode.Update));
        Assert.That(PathOf(r), Is.Null);
        Assert.That(SilentOf(r), Is.False);
    }

    [Test]
    public void Silent_flag_is_recorded()
    {
        var r = Resolve(new[] { "--silent" }, siblingManifestProbe: false);
        Assert.That(ModeOf(r), Is.EqualTo(InstallerMode.FirstInstall));
        Assert.That(SilentOf(r), Is.True);
    }

    [Test]
    public void NoArgs_noSibling_defaultsFirstInstall()
    {
        var r = Resolve(System.Array.Empty<string>(), siblingManifestProbe: false);
        Assert.That(ModeOf(r), Is.EqualTo(InstallerMode.FirstInstall));
    }

    [Test]
    public void NoArgs_withSibling_defaultsManage()
    {
        var r = Resolve(System.Array.Empty<string>(), siblingManifestProbe: true);
        Assert.That(ModeOf(r), Is.EqualTo(InstallerMode.Manage));
    }

    [Test]
    public void Manage_flag_is_explicit()
    {
        var r = Resolve(new[] { "--manage" });
        Assert.That(ModeOf(r), Is.EqualTo(InstallerMode.Manage));
    }

    [Test]
    public void Cleanup_flag_is_explicit()
    {
        var r = Resolve(new[] { "--cleanup", "--path", "C:\\app" });
        Assert.That(ModeOf(r), Is.EqualTo(InstallerMode.Cleanup));
        Assert.That(PathOf(r), Is.EqualTo("C:\\app"));
    }

    [Test]
    public void Update_flag_is_explicit()
    {
        var r = Resolve(new[] { "--update" });
        Assert.That(ModeOf(r), Is.EqualTo(InstallerMode.Update));
    }

    [Test]
    public void Install_flag_is_explicit()
    {
        var r = Resolve(new[] { "--install" });
        Assert.That(ModeOf(r), Is.EqualTo(InstallerMode.FirstInstall));
    }

    [Test]
    public void Path_equals_form_is_parsed()
    {
        var r = Resolve(new[] { "--manage", "--path=D:\\apps\\MyApp" });
        Assert.That(PathOf(r), Is.EqualTo("D:\\apps\\MyApp"));
    }
}
