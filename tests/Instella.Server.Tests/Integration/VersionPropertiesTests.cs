using Bunit;
using Instella.Server.Components.Shared;
using Instella.Server.Data.Entities;
using Instella.Server.Services;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Instella.Server.Tests.Integration;

/// <summary>
/// The version panel offers no manual upload (user decision after the hand test): builds and
/// installers arrive through the documented API / <c>instella upload</c>, so "Add Build" is gone.
/// Deprecation stays, as the "Mark as deprecated" checkbox.
/// </summary>
[TestFixture]
public sealed class VersionPropertiesTests
{
    [Test]
    public void VersionPanel_HasNoAddBuildButton_ButCanDeprecate()
    {
        using var fixture = new ServerTestFixture();
        using var ctx = new BunitContext();
        ctx.Services.AddSingleton<PackageService>(fixture.PackageService);
        var package = new Package { PackageId = "com.test.app", DisplayName = "Test App" };
        var version = new PackageVersion { VersionString = "1.4.0", Package = package };

        var cut = ctx.Render<VersionProperties>(p => p
            .Add(x => x.Package, package)
            .Add(x => x.Version, version));

        var buttons = cut.FindAll("button").Select(b => b.TextContent.Trim()).ToList();
        Assert.That(buttons, Does.Not.Contain("Add Build"));
        Assert.That(buttons, Does.Contain("Delete"));
        Assert.That(cut.Markup, Does.Contain("Mark as deprecated"));
    }
}
