using Instella.Core.Installation;
using Instella.Installer.Runtime.Builders;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Installation;

/// <summary>
/// <see cref="PreviewCliArgsParser"/>: the argv parse
/// surface for all four <c>--preview*</c> flags.
/// </summary>
[TestFixture]
public sealed class PreviewCliArgsTests
{
    [Test]
    public void Parse_bareFlag_setsPresent()
    {
        var result = PreviewCliArgsParser.Parse(new[] { "--preview" });
        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Args!.Value.PreviewFlagPresent, Is.True);
        Assert.That(result.Args.Value.Mode, Is.EqualTo(InstallerMode.FirstInstall));
        Assert.That(result.Args.Value.Speed, Is.EqualTo(PreviewSpeed.Normal));
        Assert.That(result.Args.Value.FailAtStep, Is.Null);
    }

    [Test]
    public void Parse_twoTokenMode_resolvesMode()
    {
        var result = PreviewCliArgsParser.Parse(new[] { "--preview", "--preview-mode", "uninstall" });
        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Args!.Value.Mode, Is.EqualTo(InstallerMode.Uninstall));
    }

    [Test]
    public void Parse_equalsFormSpeed_resolvesSpeed()
    {
        var result = PreviewCliArgsParser.Parse(new[] { "--preview", "--preview-speed=slow" });
        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Args!.Value.Speed, Is.EqualTo(PreviewSpeed.Slow));
    }

    [Test]
    public void Parse_caseInsensitiveMode_accepted()
    {
        var result = PreviewCliArgsParser.Parse(new[] { "--PREVIEW", "--Preview-Mode=UPGRADE" });
        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Args!.Value.PreviewFlagPresent, Is.True);
        Assert.That(result.Args.Value.Mode, Is.EqualTo(InstallerMode.Upgrade));
    }

    [Test]
    public void Parse_unknownMode_fails()
    {
        var result = PreviewCliArgsParser.Parse(new[] { "--preview", "--preview-mode=bogus" });
        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Error, Does.Contain("bogus"));
        Assert.That(result.Error, Does.Contain("install"));
    }

    [Test]
    public void Parse_unknownSpeed_fails()
    {
        var result = PreviewCliArgsParser.Parse(new[] { "--preview", "--preview-speed", "turbo" });
        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Error, Does.Contain("turbo"));
    }

    [Test]
    public void Parse_failAt_capturesStepName()
    {
        var result = PreviewCliArgsParser.Parse(new[] { "--preview", "--preview-fail", "extract-payload" });
        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Args!.Value.FailAtStep, Is.EqualTo("extract-payload"));
    }

    [Test]
    public void Parse_failAt_missingValue_fails()
    {
        var result = PreviewCliArgsParser.Parse(new[] { "--preview", "--preview-fail" });
        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Error, Does.Contain("step name"));
    }

    [Test]
    public void ContainsPreviewFlag_detectsAnywhere()
    {
        Assert.That(PreviewCliArgsParser.ContainsPreviewFlag(new[] { "--silent", "--preview", "--path", "x" }), Is.True);
        Assert.That(PreviewCliArgsParser.ContainsPreviewFlag(new[] { "--path", "x" }), Is.False);
        Assert.That(PreviewCliArgsParser.ContainsPreviewFlag(new[] { "--PREVIEW" }), Is.True);
    }

    [Test]
    public void DelayFor_returnsExpectedBuckets()
    {
        Assert.That(PreviewCliArgsParser.DelayFor(PreviewSpeed.Fast).TotalMilliseconds, Is.EqualTo(50));
        Assert.That(PreviewCliArgsParser.DelayFor(PreviewSpeed.Normal).TotalMilliseconds, Is.EqualTo(300));
        Assert.That(PreviewCliArgsParser.DelayFor(PreviewSpeed.Slow).TotalSeconds, Is.EqualTo(1));
    }
}
