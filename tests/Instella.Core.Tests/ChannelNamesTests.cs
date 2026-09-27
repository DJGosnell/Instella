using Instella.Core.Wire;
using NUnit.Framework;

namespace Instella.Core.Tests;

[TestFixture]
public class ChannelNamesTests
{
    [TestCase("stable", "stable")]
    [TestCase("Beta", "beta")]
    [TestCase(" rc-2 ", "rc-2")]
    [TestCase("a", "a")]
    [TestCase("abcdefghijklmnopqrstuvwxyz012345", "abcdefghijklmnopqrstuvwxyz012345")]
    public void Accepted(string input, string expected)
    {
        Assert.That(ChannelNames.TryNormalize(input, out var channel), Is.True);
        Assert.That(channel, Is.EqualTo(expected));
        Assert.That(ChannelNames.IsValid(input), Is.True);
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("-beta")]
    [TestCase("beta-")]
    [TestCase("be_ta")]
    [TestCase("bêta")]
    [TestCase("be ta")]
    [TestCase("abcdefghijklmnopqrstuvwxyz0123456")]
    [TestCase(null)]
    public void Rejected(string? input)
    {
        Assert.That(ChannelNames.TryNormalize(input, out var channel), Is.False);
        Assert.That(channel, Is.Null);
    }

    [Test]
    public void Constants()
    {
        Assert.That(ChannelNames.Stable, Is.EqualTo("stable"));
        Assert.That(ChannelNames.IsValid(ChannelNames.Stable), Is.True);
        Assert.That(ChannelNames.MaxLength, Is.EqualTo(32));
    }
}
