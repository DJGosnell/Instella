using System;
using Instella.Core.Manifest;
using Instella.Installer.Runtime.Builders;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Installation;

/// <summary>
/// Exercises <c>.WithPayloadFilter(...)</c> on the fluent
/// builder, the <see cref="PayloadFilterBuilder"/> API, and the preset
/// expansion. Checks that filter state flows through to the frozen
/// config and projects into the emitted manifest.
/// </summary>
[TestFixture]
public sealed class PayloadFilterBuilderTests
{
    private static InstallerBuilder MinimalBuilder()
    {
        var impl = new InstallerBuilder();
        impl.WithApp("PayloadFilterTest", "com.example.filter", new Version(1, 0, 0));
        return impl;
    }

    [Test]
    public void WithoutCall_payloadFilterIsNull()
    {
        var installer = MinimalBuilder().Build();
        var config = InstallerBuilderProbeAccess.FrozenConfig(installer);
        Assert.That(config.PayloadFilter, Is.Null, "No filter authored → FrozenConfig.PayloadFilter is null so the task takes the fast path.");
    }

    [Test]
    public void WithPayloadFilter_onlyIncludes_flowsToFrozenConfig()
    {
        var installer = MinimalBuilder()
            .WithPayloadFilter(f => f.Include("**/*.dll", "**/*.exe"))
            .Build();
        var config = InstallerBuilderProbeAccess.FrozenConfig(installer);
        Assert.That(config.PayloadFilter, Is.Not.Null);
        Assert.That(config.PayloadFilter!.Include, Is.EqualTo(new[] { "**/*.dll", "**/*.exe" }));
        Assert.That(config.PayloadFilter.Exclude, Is.Empty);
    }

    [Test]
    public void WithPayloadFilter_onlyExcludes_flowsToFrozenConfig()
    {
        var installer = MinimalBuilder()
            .WithPayloadFilter(f => f.Exclude("**/*.pdb"))
            .Build();
        var config = InstallerBuilderProbeAccess.FrozenConfig(installer);
        Assert.That(config.PayloadFilter, Is.Not.Null);
        Assert.That(config.PayloadFilter!.Include, Is.Empty);
        Assert.That(config.PayloadFilter.Exclude, Is.EqualTo(new[] { "**/*.pdb" }));
    }

    [Test]
    public void WithPayloadFilter_chainedCalls_append()
    {
        var installer = MinimalBuilder()
            .WithPayloadFilter(f => f.Exclude("**/*.pdb"))
            .WithPayloadFilter(f => f.Exclude("**/*.xml"))
            .Build();
        var config = InstallerBuilderProbeAccess.FrozenConfig(installer);
        Assert.That(config.PayloadFilter!.Exclude, Is.EqualTo(new[] { "**/*.pdb", "**/*.xml" }),
            "Second WithPayloadFilter call appends to the same list instead of replacing.");
    }

    [Test]
    public void ExcludeDefaults_debugArtifacts_expandsToKnownGlobs()
    {
        var installer = MinimalBuilder()
            .WithPayloadFilter(f => f.ExcludeDefaults(PayloadFilterPreset.DebugArtifacts))
            .Build();
        var config = InstallerBuilderProbeAccess.FrozenConfig(installer);
        Assert.That(config.PayloadFilter!.Exclude, Is.EqualTo(new[]
        {
            "**/*.pdb",
            "**/*.xml",
            "**/*.deps.json",
            "**/*.runtimeconfig.dev.json",
        }));
    }

    [Test]
    public void ExcludeDefaults_plusCustom_concatenates()
    {
        var installer = MinimalBuilder()
            .WithPayloadFilter(f => f
                .ExcludeDefaults(PayloadFilterPreset.DebugArtifacts)
                .Exclude("**/*.map"))
            .Build();
        var config = InstallerBuilderProbeAccess.FrozenConfig(installer);
        Assert.That(config.PayloadFilter!.Exclude, Contains.Item("**/*.pdb"));
        Assert.That(config.PayloadFilter.Exclude, Contains.Item("**/*.map"));
        Assert.That(config.PayloadFilter.Exclude, Has.Count.EqualTo(5));
    }

    [Test]
    public void Include_withEmptyGlob_throws()
    {
        var impl = MinimalBuilder();
        Assert.Throws<ArgumentException>(() =>
            impl.WithPayloadFilter(f => f.Include(" ")));
    }

    [Test]
    public void Exclude_withNullArray_throws()
    {
        var impl = MinimalBuilder();
        Assert.Throws<ArgumentNullException>(() =>
            impl.WithPayloadFilter(f => f.Exclude(null!)));
    }

    [Test]
    public void ExpandPreset_unknownValue_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PayloadFilterBuilder.ExpandPreset((PayloadFilterPreset)999));
    }

    [Test]
    public void ToManifest_projectsPayloadFilter()
    {
        var installer = MinimalBuilder()
            .WithPayloadFilter(f => f.Exclude("**/*.pdb"))
            .Build();
        var config = InstallerBuilderProbeAccess.FrozenConfig(installer);
        var manifest = FrozenConfigExtensions.ToManifest(config);
        Assert.That(manifest.PayloadFilter, Is.Not.Null);
        Assert.That(manifest.PayloadFilter!.Exclude, Is.EqualTo(new[] { "**/*.pdb" }));
    }

    [Test]
    public void ToManifest_withoutFilter_leavesNull()
    {
        var installer = MinimalBuilder().Build();
        var config = InstellerBuilderProbeAccess_Null(installer);
        var manifest = FrozenConfigExtensions.ToManifest(config);
        Assert.That(manifest.PayloadFilter, Is.Null);
    }

    // Local helper so test name parses cleanly; wraps the same reach-in used elsewhere.
    private static FrozenConfig InstellerBuilderProbeAccess_Null(IInstellaInstaller installer) =>
        InstallerBuilderProbeAccess.FrozenConfig(installer);
}
