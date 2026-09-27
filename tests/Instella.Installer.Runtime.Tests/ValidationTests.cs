using Instella.Core.Manifest;
using Instella.Installer.Runtime.Core;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests;

[TestFixture]
public sealed class ValidationTests
{
    [Test]
    public void Manifest_Validate_ValidManifest_DoesNotThrow()
    {
        var manifest = new InstellaManifest
        {
            AppName = "TestApp",
            AppId = "com.test.app",
            Version = new Version(1, 0, 0),
            ServerUrl = "https://example.com"
        };

        Assert.DoesNotThrow(() => manifest.Validate());
    }

    [Test]
    public void Manifest_Validate_EmptyAppName_Throws()
    {
        var manifest = new InstellaManifest
        {
            AppName = "",
            AppId = "com.test.app",
            Version = new Version(1, 0, 0),
            ServerUrl = "https://example.com"
        };

        var ex = Assert.Throws<InvalidManifestException>(() => manifest.Validate());
        Assert.That(ex!.FieldName, Is.EqualTo("AppName"));
    }

    [Test]
    public void Manifest_Validate_WhitespaceAppId_Throws()
    {
        var manifest = new InstellaManifest
        {
            AppName = "TestApp",
            AppId = "   ",
            Version = new Version(1, 0, 0),
            ServerUrl = "https://example.com"
        };

        var ex = Assert.Throws<InvalidManifestException>(() => manifest.Validate());
        Assert.That(ex!.FieldName, Is.EqualTo("AppId"));
    }

    [Test]
    public void Manifest_Validate_EmptyServerUrl_Throws()
    {
        var manifest = new InstellaManifest
        {
            AppName = "TestApp",
            AppId = "com.test.app",
            Version = new Version(1, 0, 0),
            ServerUrl = ""
        };

        var ex = Assert.Throws<InvalidManifestException>(() => manifest.Validate());
        Assert.That(ex!.FieldName, Is.EqualTo("ServerUrl"));
    }

    [Test]
    public void BoundedStream_NonSeekableInner_ThrowsArgumentException()
    {
        using var nonSeekable = new NonSeekableStream();
        Assert.Throws<ArgumentException>(() =>
            new EmbeddedResources.BoundedStream(nonSeekable, 100, ownsStream: false));
    }

    [Test]
    public void BoundedStream_SeekableInner_Succeeds()
    {
        using var seekable = new MemoryStream(new byte[100]);
        using var bounded = new EmbeddedResources.BoundedStream(seekable, 50, ownsStream: false);
        Assert.That(bounded.Length, Is.EqualTo(50));
        Assert.That(bounded.CanSeek, Is.True);
    }

    private sealed class NonSeekableStream : MemoryStream
    {
        public override bool CanSeek => false;
    }
}
