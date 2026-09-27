using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Installation;
using Instella.Core.Manifest;
using Instella.Installer.Runtime.Builders;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Installation;

/// <summary>
/// Exercises the <c>--emit-manifest &lt;path&gt;</c> build-time
/// CLI path. Split out from the broader
/// <see cref="InstellaInstallerImplTests"/> so the parse / emit shape
/// is pinned independently of mode-dispatch behaviour.
/// </summary>
[TestFixture]
public sealed class EmitManifestTests
{
    [Test]
    public void TryExtract_twoToken_returnsPath()
    {
        var args = new[] { "--emit-manifest", @"C:\out\manifest.json" };
        var ok = InstellaInstallerImpl.TryExtractEmitManifestPath(args, out var path);
        Assert.That(ok, Is.True);
        Assert.That(path, Is.EqualTo(@"C:\out\manifest.json"));
    }

    [Test]
    public void TryExtract_equalsForm_returnsPath()
    {
        var args = new[] { "--emit-manifest=/tmp/manifest.json" };
        var ok = InstellaInstallerImpl.TryExtractEmitManifestPath(args, out var path);
        Assert.That(ok, Is.True);
        Assert.That(path, Is.EqualTo("/tmp/manifest.json"));
    }

    [Test]
    public void TryExtract_flagWithoutValue_returnsFalse()
    {
        var args = new[] { "--emit-manifest" };
        var ok = InstellaInstallerImpl.TryExtractEmitManifestPath(args, out var path);
        Assert.That(ok, Is.False);
        Assert.That(path, Is.Null);
    }

    [Test]
    public void TryExtract_missingFlag_returnsFalse()
    {
        var args = new[] { "--install", "--path", @"C:\app" };
        var ok = InstellaInstallerImpl.TryExtractEmitManifestPath(args, out var path);
        Assert.That(ok, Is.False);
        Assert.That(path, Is.Null);
    }

    [Test]
    public void TryExtract_isCaseInsensitive()
    {
        var args = new[] { "--EMIT-MANIFEST", "x.json" };
        var ok = InstellaInstallerImpl.TryExtractEmitManifestPath(args, out var path);
        Assert.That(ok, Is.True);
        Assert.That(path, Is.EqualTo("x.json"));
    }

    [Test]
    public async Task RunAsync_emitManifest_writesJsonWithAppMetadata()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"instella-emit-{Guid.NewGuid():N}.json");
        try
        {
            var installer = InstellaInstaller.Create()
                .WithApp("EmitApp", "com.test.emit", new Version(2, 3, 4))
                .WithPublisher("Test Inc.")
                .WithServer("https://updates.example.com").AllowUnsignedUpdates()
                .WithDownloadToken("idt_" + new string('A', 43))
                .Build();

            var exit = await installer.RunAsync(new[] { "--emit-manifest", tempPath }, CancellationToken.None);
            Assert.That(exit, Is.EqualTo((int)InstellaExitCode.Success));
            Assert.That(File.Exists(tempPath), Is.True);

            var json = await File.ReadAllTextAsync(tempPath);
            var roundTrip = JsonSerializer.Deserialize(json, ManifestJsonContext.Default.InstellaManifest);
            Assert.That(roundTrip, Is.Not.Null);
            Assert.That(roundTrip!.AppName, Is.EqualTo("EmitApp"));
            Assert.That(roundTrip.AppId, Is.EqualTo("com.test.emit"));
            Assert.That(roundTrip.Version, Is.EqualTo(new Version(2, 3, 4)));
            Assert.That(roundTrip.Publisher, Is.EqualTo("Test Inc."));
            Assert.That(json, Does.Contain("\"downloadToken\": \"idt_"), "9.1: the token travels in the build manifest");
            Assert.That(roundTrip.DownloadToken, Is.EqualTo("idt_" + new string('A', 43)));
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    [Test]
    public async Task RunAsync_emitManifest_createsParentDirectories()
    {
        var nestedDir = Path.Combine(Path.GetTempPath(), $"instella-emit-dir-{Guid.NewGuid():N}", "nested", "deep");
        var manifestPath = Path.Combine(nestedDir, "manifest.json");
        try
        {
            var installer = InstellaInstaller.Create()
                .WithApp("NestApp", "com.test.nest", new Version(1, 0, 0))
                .Build();

            var exit = await installer.RunAsync(new[] { "--emit-manifest", manifestPath }, CancellationToken.None);
            Assert.That(exit, Is.EqualTo((int)InstellaExitCode.Success));
            Assert.That(File.Exists(manifestPath), Is.True);
        }
        finally
        {
            var root = Path.Combine(Path.GetTempPath(), Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(nestedDir)!)!)!);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task RunAsync_emitManifest_equalsForm_works()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"instella-emit-eq-{Guid.NewGuid():N}.json");
        try
        {
            var installer = InstellaInstaller.Create()
                .WithApp("EqApp", "com.test.eq", new Version(1, 0, 0))
                .Build();

            var exit = await installer.RunAsync(new[] { $"--emit-manifest={tempPath}" }, CancellationToken.None);
            Assert.That(exit, Is.EqualTo((int)InstellaExitCode.Success));
            Assert.That(File.Exists(tempPath), Is.True);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }
}
