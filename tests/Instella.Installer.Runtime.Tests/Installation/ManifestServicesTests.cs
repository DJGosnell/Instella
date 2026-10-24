using System;
using System.Text.Json;
using Instella.Core.Installation;
using Instella.Core.Platform;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Installation;

/// <summary>
/// The installed manifest's optional <c>services</c> list: round-trips through the source-generated
/// context, is absent (null) in older manifests, keeps version 4 and unknown-field preservation.
/// </summary>
[TestFixture]
public sealed class ManifestServicesTests
{
    private static InstalledManifest Manifest() => new()
    {
        AppName = "Test",
        AppId = "com.test.app",
        Version = new Version(1, 0, 0),
        InstallDirectory = @"C:\apps\test",
        ExecutableName = "Test.exe",
        InstalledAt = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc),
        Platform = TargetPlatform.Windows,
        Files = Array.Empty<InstalledFile>(),
    };

    [Test]
    public void Services_RoundTrip()
    {
        var manifest = Manifest() with
        {
            Services =
            [
                new ManifestService("TestSvc", @"bin\TestSvc.exe", @"NT AUTHORITY\LocalService", ManifestService.AutomaticDelayed, CreatedByInstella: true),
                new ManifestService("Existing", "Existing.exe", null, ManifestService.Manual, CreatedByInstella: false),
            ],
        };

        var json = JsonSerializer.Serialize(manifest, InstalledManifestJsonContext.Default.InstalledManifest);
        var read = JsonSerializer.Deserialize(json, InstalledManifestJsonContext.Default.InstalledManifest)!;

        Assert.That(json, Does.Contain("\"services\""));
        Assert.That(json, Does.Contain("\"createdByInstella\""));
        Assert.That(read.ManifestVersion, Is.EqualTo(4));
        Assert.That(read.Services, Is.EqualTo(manifest.Services));
    }

    [Test]
    public void StartTypes_AreStableStrings()
    {
        Assert.That(new[] { ManifestService.Automatic, ManifestService.AutomaticDelayed, ManifestService.Manual, ManifestService.Disabled },
            Is.EqualTo(new[] { "automatic", "automatic-delayed", "manual", "disabled" }));
    }

    [Test]
    public void AManifestWithoutServices_ReadsAsNull_AndWritesNoServicesKey()
    {
        const string json = """{"manifestVersion":4,"appName":"A","appId":"a","version":"1.0.0","installDirectory":"x","executableName":"a.exe","installedAt":"2026-01-01T00:00:00Z","files":[]}""";
        var read = JsonSerializer.Deserialize(json, InstalledManifestJsonContext.Default.InstalledManifest)!;

        Assert.That(read.Services, Is.Null);
        Assert.That(JsonSerializer.Serialize(read, InstalledManifestJsonContext.Default.InstalledManifest), Does.Not.Contain("\"services\""));
    }

    [Test]
    public void UnknownFields_SurviveBesideServices()
    {
        const string json = """{"manifestVersion":4,"appName":"A","appId":"a","version":"1.0.0","installDirectory":"x","executableName":"a.exe","installedAt":"2026-01-01T00:00:00Z","files":[],"services":[{"name":"S","executablePath":"s.exe","accountName":null,"startType":"automatic","createdByInstella":true,"futureServiceField":1}],"futureField":["x"]}""";
        var read = JsonSerializer.Deserialize(json, InstalledManifestJsonContext.Default.InstalledManifest)!;
        var again = JsonSerializer.Serialize(read with { Version = new Version(2, 0) }, InstalledManifestJsonContext.Default.InstalledManifest);

        Assert.That(read.Services, Has.Count.EqualTo(1));
        Assert.That(read.Services![0].Name, Is.EqualTo("S"));
        Assert.That(read.Services[0].StartType, Is.EqualTo(ManifestService.Automatic));
        Assert.That(again, Does.Contain("futureField"));
        Assert.That(again, Does.Contain("\"services\""));
    }
}
