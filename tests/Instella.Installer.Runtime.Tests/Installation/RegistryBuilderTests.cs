using System;
using System.Linq;
using System.Reflection;
using Instella.Core.Platform;
using Instella.Installer.Runtime.Builders;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Installation;

/// <summary>
/// Validates the registry fluent surface: OnWindows lambda runs only on
/// Windows, AddRegistryKey + RegistryKeyBuilder setters accumulate
/// RegistryWriteSpec entries on the FrozenConfig (inspected via reflection
/// through InternalsVisibleTo), and non-Windows platforms skip the block.
/// </summary>
[TestFixture]
public sealed class RegistryBuilderTests
{
    private static readonly Assembly RuntimeAsm = typeof(InstallerBuilder).Assembly;
    private static readonly Type FrozenConfigType = RuntimeAsm.GetType("Instella.Installer.Runtime.Builders.FrozenConfig", throwOnError: true)!;
    private static readonly Type ImplType = RuntimeAsm.GetType("Instella.Installer.Runtime.Builders.InstellaInstallerImpl", throwOnError: true)!;

    private static object GetConfig(IInstellaInstaller installer)
    {
        var configField = ImplType.GetField("_config", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return configField.GetValue(installer)!;
    }

    private static System.Collections.IList GetWrites(IInstellaInstaller installer)
    {
        var config = GetConfig(installer);
        var prop = FrozenConfigType.GetProperty("RegistryWrites")!;
        return (System.Collections.IList)prop.GetValue(config)!;
    }

    [Test]
    public void OnWindows_onWindowsPlatform_appliesRegistryWrites()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Ignore("Test only meaningful on Windows");
            return;
        }

        var installer = InstellaInstaller.Create()
            .WithApp("TestApp", "com.test.app", new Version(1, 0, 0))
            .OnWindows(w => w.AddRegistryKey(RegistryHive.CurrentUser, "Software\\TestApp", k => k
                .SetString("InstallDir", "C:\\apps\\test")
                .SetDWord("Version", 100)))
            .Build();

        var writes = GetWrites(installer);
        Assert.That(writes.Count, Is.EqualTo(2));
    }

    [Test]
    public void OnLinux_onNonLinuxPlatform_skipsDelegate()
    {
        if (OperatingSystem.IsLinux())
        {
            Assert.Ignore("Test only meaningful off Linux");
            return;
        }

        var wasInvoked = false;
        InstellaInstaller.Create()
            .WithApp("TestApp", "com.test.app", new Version(1, 0, 0))
            .OnLinux(_ => { wasInvoked = true; })
            .Build();

        Assert.That(wasInvoked, Is.False);
    }

    [Test]
    public void RegistryKeyBuilder_staticOverloads_storeInstellaRegistryValueKind()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Ignore("Test only meaningful on Windows");
            return;
        }

        var installer = InstellaInstaller.Create()
            .WithApp("TestApp", "com.test.app", new Version(1, 0, 0))
            .OnWindows(w => w.AddRegistryKey(RegistryHive.CurrentUser, "Software\\TestApp", k => k
                .SetString("A", "x")
                .SetExpandString("B", "%PATH%")
                .SetDWord("C", 1)
                .SetQWord("D", 2L)
                .SetMultiString("E", new[] { "one", "two" })
                .SetBinary("F", new byte[] { 1, 2 })))
            .Build();

        var writes = GetWrites(installer);
        Assert.That(writes.Count, Is.EqualTo(6));

        // Inspect kinds using reflection on RegistryWriteSpec.Kind.
        var specType = RuntimeAsm.GetType("Instella.Installer.Runtime.Builders.RegistryWriteSpec", throwOnError: true)!;
        var kindProp = specType.GetProperty("Kind")!;
        var kinds = writes.Cast<object>().Select(s => kindProp.GetValue(s)!.ToString()).ToArray();
        Assert.That(kinds, Is.EqualTo(new[] { "String", "ExpandString", "DWord", "QWord", "MultiString", "Binary" }));
    }

    [Test]
    public void AddRegistryKey_rejectsEmptyKeyPath()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Ignore("Test only meaningful on Windows");
            return;
        }

        var builder = InstellaInstaller.Create()
            .WithApp("TestApp", "com.test.app", new Version(1, 0, 0));

        Assert.Throws<ArgumentException>(() => builder.OnWindows(w =>
            w.AddRegistryKey(RegistryHive.CurrentUser, "", k => k.SetString("x", "y"))));
    }

    [Test]
    public void NoOnWindowsCall_yieldsEmptyRegistryWrites()
    {
        var installer = InstellaInstaller.Create()
            .WithApp("TestApp", "com.test.app", new Version(1, 0, 0))
            .Build();

        var writes = GetWrites(installer);
        Assert.That(writes.Count, Is.EqualTo(0));
    }
}
