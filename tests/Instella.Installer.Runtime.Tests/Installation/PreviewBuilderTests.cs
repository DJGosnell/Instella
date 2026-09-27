using System;
using Instella.Installer.Runtime.Builders;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Installation;

/// <summary>
/// Exercises the <c>.EnablePreview()</c> opt-in on the fluent
/// builder. The flag lives on <see cref="FrozenConfig.PreviewEnabled"/>, is
/// off by default, and idempotent on repeated calls.
/// </summary>
[TestFixture]
public sealed class PreviewBuilderTests
{
    private static InstallerBuilder MinimalBuilder()
    {
        var b = new InstellaBuilderProbe();
        b.WithApp("Preview Test", "com.example.preview", new Version(1, 0, 0));
        return b.Impl;
    }

    [Test]
    public void EnablePreview_flipsFrozenFlag()
    {
        var impl = MinimalBuilder();
        var installer = impl.EnablePreview().Build();
        var config = InstallerBuilderProbeAccess.FrozenConfig(installer);
        Assert.That(config.PreviewEnabled, Is.True);
    }

    [Test]
    public void EnablePreview_default_isFalse()
    {
        var impl = MinimalBuilder();
        var installer = impl.Build();
        var config = InstallerBuilderProbeAccess.FrozenConfig(installer);
        Assert.That(config.PreviewEnabled, Is.False);
    }

    [Test]
    public void EnablePreview_calledTwice_isIdempotent()
    {
        var impl = MinimalBuilder();
        var installer = impl.EnablePreview().EnablePreview().Build();
        var config = InstallerBuilderProbeAccess.FrozenConfig(installer);
        Assert.That(config.PreviewEnabled, Is.True);
    }
}

/// <summary>
/// Small utility that wraps a real <see cref="InstallerBuilder"/> so tests
/// can keep a typed reference while still exercising the
/// <see cref="InstallerBuilder"/> surface.
/// </summary>
internal sealed class InstellaBuilderProbe
{
    public InstallerBuilder Impl { get; } = new();

    public InstellaBuilderProbe WithApp(string name, string id, Version version)
    {
        Impl.WithApp(name, id, version);
        return this;
    }
}

/// <summary>
/// Reflection-free reach-in helper so preview tests can inspect the
/// internal <see cref="FrozenConfig"/> stamped into <see cref="IInstellaInstaller"/>.
/// </summary>
internal static class InstallerBuilderProbeAccess
{
    public static FrozenConfig FrozenConfig(IInstellaInstaller installer)
    {
        // The only concrete IInstellaInstaller is InstellaInstallerImpl; reach
        // the config field via the typed cast (InternalsVisibleTo grants
        // access already).
        var impl = (InstellaInstallerImpl)installer;
        return impl.ConfigForTests;
    }
}
