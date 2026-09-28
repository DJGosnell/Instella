using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Installation;
using Instella.Installer.Runtime.Core;
using Instella.Installer.Runtime.Migrations;
using Instella.Installer.Testing;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Migrations;

/// <summary>The installed-manifest fields, path guards and Run-command parsing migrations build on.</summary>
[TestFixture]
public class MigrationFoundationTests
{
    private static string Root => Path.Combine(Path.GetTempPath(), "instella-migration-foundation", "App");

    private static InstalledManifest Manifest() => new()
    {
        AppName = "App", AppId = "com.app", Version = new Version(1, 0, 0), InstallDirectory = Root,
        ExecutableName = "App.exe", InstalledAt = DateTime.UtcNow, Files = [],
    };

    [Test]
    public async Task CompletedMigrations_AndAdoptedItems_RoundTrip()
    {
        var fs = new InMemoryFileSystem();
        var writer = new InstallManifestWriter(fs);
        await writer.WriteAsync(Root, Manifest() with
        {
            CompletedMigrations = ["a", "b"],
            AdoptedItems = [new ManifestAdoptedItem(ManifestAdoptedItem.RunValue, "ExampleApp", true, "replace-old-copy")],
        }, CancellationToken.None);

        var json = Encoding.UTF8.GetString((await fs.ReadAllBytesAsync(Path.Combine(Root, InstellaOwnedPaths.InstalledManifest), CancellationToken.None)).Value!);
        Assert.That(json, Does.Contain("\"completedMigrations\"").And.Contain("\"adoptedItems\"").And.Contain("\"run-value\""));

        var read = (await writer.ReadAsync(Root, CancellationToken.None))!;
        Assert.That(read.CompletedMigrations, Is.EqualTo(new[] { "a", "b" }));
        Assert.That(read.AdoptedItems, Is.EqualTo(new[] { new ManifestAdoptedItem("run-value", "ExampleApp", true, "replace-old-copy") }));
    }

    [Test]
    public async Task ManifestWithoutTheFields_ReadsAsNull_AndWritesNeither()
    {
        var fs = new InMemoryFileSystem();
        var writer = new InstallManifestWriter(fs);
        await writer.WriteAsync(Root, Manifest(), CancellationToken.None);
        var json = Encoding.UTF8.GetString((await fs.ReadAllBytesAsync(Path.Combine(Root, InstellaOwnedPaths.InstalledManifest), CancellationToken.None)).Value!);
        Assert.That(json, Does.Not.Contain("completedMigrations").And.Not.Contain("adoptedItems"));

        var read = (await writer.ReadAsync(Root, CancellationToken.None))!;
        Assert.That(read.CompletedMigrations, Is.Null);
        Assert.That(read.AdoptedItems, Is.Null);
    }

    [Test]
    public void TheUpdatersWithRewrite_KeepsBothFields()
    {
        var installed = Manifest() with
        {
            CompletedMigrations = ["a"],
            AdoptedItems = [new ManifestAdoptedItem("run-value", "X", true, "a")],
        };
        // UpdaterEngine rewrites the manifest with "installed with { Version, Files, ... }".
        var updated = installed with { Version = new Version(2, 0, 0), Files = [] };
        var json = JsonSerializer.Serialize(updated, InstalledManifestJsonContext.Default.InstalledManifest);
        var back = JsonSerializer.Deserialize(json, InstalledManifestJsonContext.Default.InstalledManifest)!;
        Assert.That(back.CompletedMigrations, Is.EqualTo(new[] { "a" }));
        Assert.That(back.AdoptedItems, Has.Count.EqualTo(1));
    }

    [Test]
    public void AnOlderReader_KeepsTheFieldsAsUnknown()
    {
        // A 0.1.0 stub does not know the fields: it keeps them in UnknownFields. Simulated by a
        // field this version does not know.
        const string json = """{"manifestVersion":4,"appName":"A","appId":"a","version":"1.0.0","installDirectory":"x","executableName":"a.exe","installedAt":"2026-01-01T00:00:00Z","files":[],"futureField":["x"]}""";
        var read = JsonSerializer.Deserialize(json, InstalledManifestJsonContext.Default.InstalledManifest)!;
        var again = JsonSerializer.Serialize(read with { Version = new Version(2, 0) }, InstalledManifestJsonContext.Default.InstalledManifest);
        Assert.That(again, Does.Contain("futureField"));
    }

    [Test]
    public void PathGuards_RefuseVolumeRoots_ProtectedFolders_AndTheirAncestors()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.That(PathGuards.RefusalFor(Path.GetPathRoot(local)!, []), Is.Not.Null, "volume root");
        Assert.That(PathGuards.RefusalFor(local, []), Is.Not.Null, "protected folder");
        Assert.That(PathGuards.RefusalFor(local + Path.DirectorySeparatorChar, []), Is.Not.Null, "trailing separator");
        Assert.That(PathGuards.RefusalFor(Path.GetDirectoryName(local)!, []), Is.Not.Null, "ancestor");
        Assert.That(PathGuards.RefusalFor(Path.Combine(local, "ExampleApp"), []), Is.Null, "a child is allowed");
    }

    [Test]
    public void PathGuards_HonourExtraProtectedFolders()
    {
        var extra = Path.Combine(Path.GetTempPath(), "guards-extra", "Protected");
        Assert.That(PathGuards.RefusalFor(extra, [extra]), Is.Not.Null);
        Assert.That(PathGuards.RefusalFor(Path.GetDirectoryName(extra)!, [extra]), Is.Not.Null);
        Assert.That(PathGuards.RefusalFor(Path.Combine(extra, "child"), [extra]), Is.Null);
    }

    [Test]
    public void IsSameOrInside_IsSeparatorAware()
    {
        var root = Path.Combine(Path.GetTempPath(), "guards", "App");
        Assert.That(PathGuards.IsSameOrInside(root, root), Is.True);
        Assert.That(PathGuards.IsSameOrInside(Path.Combine(root, "a.exe"), root), Is.True);
        Assert.That(PathGuards.IsSameOrInside(root + "Other", root), Is.False, "a sibling sharing a prefix is not inside");
        Assert.That(PathGuards.IsSameOrInside(Path.GetDirectoryName(root)!, root), Is.False);
    }

    [TestCase("\"C:\\Users\\u\\AppData\\Local\\ExampleApp\\ExampleApp.exe\"", "C:\\Users\\u\\AppData\\Local\\ExampleApp\\ExampleApp.exe", "")]
    [TestCase("\"C:\\A B\\app.exe\" --minimized", "C:\\A B\\app.exe", "--minimized")]
    [TestCase("C:\\Program Files\\Example App\\app.exe --tray", "C:\\Program Files\\Example App\\app.exe", "--tray")]
    [TestCase("C:\\Tools\\app.exe", "C:\\Tools\\app.exe", "")]
    [TestCase("C:\\Tools\\run.cmd /x", "C:\\Tools\\run.cmd", "/x")]
    public void RunCommand_ParsesTheExecutable(string command, string exe, string args)
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("Windows paths");
        Assert.That(RunCommand.TryGetExecutable(command, out var path, out var arguments), Is.True);
        Assert.That(path, Is.EqualTo(exe));
        Assert.That(arguments, Is.EqualTo(args));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("\"unterminated")]
    [TestCase("app.exe --relative")]
    public void RunCommand_RejectsGarbage(string? command)
    {
        Assert.That(RunCommand.TryGetExecutable(command, out _, out _), Is.False);
    }

    [Test]
    public void RunCommand_ExpandsEnvironmentVariables()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("Windows paths");
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.That(RunCommand.TryGetExecutable("\"%LOCALAPPDATA%\\ExampleApp\\ExampleApp.exe\"", out var path, out _), Is.True);
        Assert.That(path, Is.EqualTo(Path.Combine(local, "ExampleApp", "ExampleApp.exe")).IgnoreCase);
    }

    [Test]
    public void RunCommand_FormatQuotesThePath()
    {
        Assert.That(RunCommand.Format(@"C:\A B\app.exe", ""), Is.EqualTo("\"C:\\A B\\app.exe\""));
        Assert.That(RunCommand.Format(@"C:\A B\app.exe", " --tray "), Is.EqualTo("\"C:\\A B\\app.exe\" --tray"));
    }
}
