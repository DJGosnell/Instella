using Instella.Core.Utilities;
using NUnit.Framework;

namespace Instella.Core.Tests;

/// <summary>
/// One spelling per version. An installer often carries its assembly version (1.2.0.0) while
/// the release is uploaded as 1.2.0, and <see cref="Version"/> orders 1.3 below 1.3.0, so the
/// same release spelled two ways would pass the anti-downgrade check.
/// </summary>
[TestFixture]
public class AppVersionsTests
{
    [TestCase("1.2", "1.2.0", "0000000001.0000000002.0000000000.0000000000")]
    [TestCase("1.2.0", "1.2.0", "0000000001.0000000002.0000000000.0000000000")]
    [TestCase("1.2.0.0", "1.2.0", "0000000001.0000000002.0000000000.0000000000")]
    [TestCase("1.2.3.0", "1.2.3", "0000000001.0000000002.0000000003.0000000000")]
    [TestCase("1.2.3.4", "1.2.3.4", "0000000001.0000000002.0000000003.0000000004")]
    [TestCase("10.0.1", "10.0.1", "0000000010.0000000000.0000000001.0000000000")]
    [TestCase("2147483647.0.0", "2147483647.0.0", "2147483647.0000000000.0000000000.0000000000")]
    public void TryParse_GivesTheCanonicalFormAndSortKey(string input, string canonical, string sortKey)
    {
        Assert.That(AppVersions.TryParse(input, out var v), Is.True);
        Assert.That(v!.ToString(), Is.EqualTo(canonical));
        Assert.That(AppVersions.ToCanonicalString(Version.Parse(input)), Is.EqualTo(canonical));
        Assert.That(AppVersions.ToSortKey(v), Is.EqualTo(sortKey));
        Assert.That(AppVersions.ToSortKey(v), Has.Length.EqualTo(43));
    }

    [TestCase("1.2.0.0", "1.2.0")]
    [TestCase("1.2.3.4", "1.2.3.4")]
    [TestCase("1.2", "1.2.0")]
    public void Normalize(string input, string expected) =>
        Assert.That(AppVersions.Normalize(Version.Parse(input)), Is.EqualTo(Version.Parse(expected)));

    [TestCase("1.3", "1.3.0", 0)]
    [TestCase("1.3.0.0", "1.3", 0)]
    [TestCase("1.3.0.1", "1.3", 1)]
    [TestCase("1.2.9", "1.3", -1)]
    public void Compare_UsesCanonicalForms(string a, string b, int sign)
    {
        Assert.That(Math.Sign(AppVersions.Compare(Version.Parse(a), Version.Parse(b))), Is.EqualTo(sign));
        Assert.That(AppVersions.Equal(Version.Parse(a), Version.Parse(b)), Is.EqualTo(sign == 0));
    }

    [TestCase("v1.0")]
    [TestCase("1.0.0-rc.1")]
    [TestCase("latest")]
    [TestCase(" 1.0")]
    [TestCase("1.0 ")]
    [TestCase("1.2.3.4.5")]
    [TestCase("1")]
    [TestCase("+1.0")]
    [TestCase("1.-1")]
    [TestCase("")]
    [TestCase(null)]
    public void TryParse_Refuses(string? input) =>
        Assert.That(AppVersions.TryParse(input, out _), Is.False);

    [Test]
    public void SortKeys_OrderLikeVersions()
    {
        string[] versions = ["1.10.0", "1.2.0", "1.2.0.1", "0.9", "2.0", "1.2.1"];
        var byKey = versions.OrderBy(v => AppVersions.ToSortKey(Version.Parse(v)), StringComparer.Ordinal).ToArray();
        var byVersion = versions.OrderBy(v => AppVersions.Normalize(Version.Parse(v))).ToArray();
        Assert.That(byKey, Is.EqualTo(byVersion));
    }
}
