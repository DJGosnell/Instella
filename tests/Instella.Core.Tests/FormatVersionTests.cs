using System.Linq;
using System.Text.Json;
using Instella.Core.Manifest;
using NUnit.Framework;

namespace Instella.Core.Tests;

/// <summary>Reader rules for the versioned formats Core owns.</summary>
[TestFixture]
public class FormatVersionTests
{
    private static InstellaManifest Manifest(int schemaVersion) => new()
    {
        SchemaVersion = schemaVersion,
        AppName = "App",
        AppId = "com.test.app",
        Version = new Version(1, 0, 0),
        ServerUrl = "https://updates.example.com",
    };

    [Test]
    public void BuildManifest_IsWrittenWithTheCurrentSchemaVersion()
    {
        var json = JsonSerializer.Serialize(Manifest(InstellaManifest.CurrentSchemaVersion), ManifestJsonContext.Default.InstellaManifest);
        Assert.That(json, Does.Contain("\"schemaVersion\":1").Or.Contain("\"schemaVersion\": 1"));
    }

    [Test]
    public void BuildManifest_WithoutASchemaVersion_IsAccepted()
    {
        var manifest = JsonSerializer.Deserialize(
            """{"appName":"App","appId":"com.test.app","version":"1.0.0","serverUrl":"https://updates.example.com"}""",
            ManifestJsonContext.Default.InstellaManifest)!;
        Assert.That(manifest.SchemaVersion, Is.Zero, "source-generated deserialization defaults absent fields");
        Assert.DoesNotThrow(manifest.Validate);
    }

    [Test]
    public void BuildManifest_FromANewerInstella_IsRejected()
    {
        var ex = Assert.Throws<InvalidManifestException>(() => Manifest(InstellaManifest.CurrentSchemaVersion + 1).Validate());
        Assert.That(ex!.Message, Does.Contain("newer Instella"));
    }

    // ---- patch manifest ----

    [Test]
    public void PatchManifest_WithoutAFormatVersion_ReadsAsVersion1()
    {
        const string json = """
            {"fromVersion":"1.0.0","toVersion":"1.1.0",
             "patchedFiles":[{"relativePath":"App.exe","patchSha256":"aa","patchSize":3,"expectedSha256":"bb"}]}
            """;

        var manifest = JsonSerializer.Deserialize(json, Instella.Core.Wire.WireJsonContext.Default.PatchManifest)!;

        Assert.That(manifest.FormatVersion, Is.EqualTo(1));
        Assert.That(manifest.PatchedFiles.Single().RelativePath, Is.EqualTo("App.exe"));
        Assert.That(manifest.NewFiles, Is.Empty, "the fields the client doesn't read are optional");
        Assert.That(manifest.VerificationList, Is.Empty);
    }

    [Test]
    public void PatchManifest_IsWrittenWithFormatVersion1()
    {
        var manifest = new Instella.Core.Update.PatchManifest
        {
            FromVersion = new Version(1, 0, 0), ToVersion = new Version(1, 1, 0), PatchedFiles = [],
        };

        var json = JsonSerializer.Serialize(manifest, Instella.Core.Wire.WireJsonContext.Default.PatchManifest);

        Assert.That(json, Does.Contain("\"formatVersion\":1"));
    }
}
