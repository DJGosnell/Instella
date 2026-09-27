using System;
using Instella.Installer.Runtime.Builders;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Installation;

/// <summary>
/// Pins that the four <c>--preview*</c> reserved flags collide with user
/// <c>AddCliFlag&lt;T&gt;</c> attempts the same way <c>--silent</c> and
/// <c>--emit-manifest</c> do.
/// </summary>
[TestFixture]
public sealed class ReservedCliFlagsPreviewTests
{
    [TestCase("--preview")]
    [TestCase("--preview-mode")]
    [TestCase("--preview-speed")]
    [TestCase("--preview-fail")]
    public void AddCliFlag_rejectsReservedPreviewName(string name)
    {
        var builder = new InstallerBuilder();
        builder.WithApp("App", "com.example.app", new Version(1, 0, 0));
        builder.AddCliFlag<string>(name, defaultValue: "");

        var ex = Assert.Throws<InvalidOperationException>(() => builder.Build());
        Assert.That(ex!.Message, Does.Contain(name));
        Assert.That(ex.Message, Does.Contain("reserved"));
    }

    [Test]
    public void AddCliFlag_rejectsBareNameNormalizedToReserved()
    {
        // Flag names are normalized: a bare "preview" without leading "--" is
        // still checked against the reserved set.
        var builder = new InstallerBuilder();
        builder.WithApp("App", "com.example.app", new Version(1, 0, 0));
        builder.AddCliFlag<bool>("preview", defaultValue: false);

        var ex = Assert.Throws<InvalidOperationException>(() => builder.Build());
        Assert.That(ex!.Message, Does.Contain("--preview"));
    }
}
