using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Instella.Installer.Build.Tasks;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using NUnit.Framework;

namespace Instella.Installer.Build.Tests;

/// <summary>
/// Exercises the MSBuild task that wraps
/// <see cref="PayloadAppender.AppendOfflinePayload"/>. The task's input is
/// <c>PayloadFiles</c> (ITaskItem[]): each item's <c>Identity</c> is the on-disk source,
/// <c>TargetPath</c> is the zip-entry path. These tests construct the
/// item array directly and verify the resulting zip.
/// </summary>
[TestFixture]
public sealed class AppendPayloadToSelfTaskTests
{
    private string _tempDir = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"instella-task-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); }
            catch { /* best effort — CI cleanup */ }
        }
    }

    [Test]
    public void Execute_happyPath_appendsPayloadAndReturnsTrue()
    {
        var exePath = Path.Combine(_tempDir, "test.exe");
        var manifestPath = Path.Combine(_tempDir, "instella.json");

        File.WriteAllBytes(exePath, new byte[] { 0x4D, 0x5A, 0x90, 0x00 }); // MZ header
        File.WriteAllText(manifestPath, """{"appName":"TaskTest","appId":"com.task.test"}""");

        var files = new[]
        {
            MakePayloadFile("app.txt", "hello"),
            MakePayloadFile("readme.md", "# test"),
        };

        var task = new AppendPayloadToSelf
        {
            BuildEngine = new StubBuildEngine(),
            InstallerExePath = exePath,
            ManifestPath = manifestPath,
            PayloadFiles = files,
        };

        var ok = task.Execute();

        Assert.That(ok, Is.True);
        Assert.That(PayloadAppender.HasPayload(exePath), Is.True);
    }

    [Test]
    public void Execute_missingExe_logsErrorAndReturnsFalse()
    {
        var engine = new StubBuildEngine();
        var task = new AppendPayloadToSelf
        {
            BuildEngine = engine,
            InstallerExePath = Path.Combine(_tempDir, "does-not-exist.exe"),
            ManifestPath = Path.Combine(_tempDir, "manifest.json"),
            PayloadFiles = Array.Empty<ITaskItem>(),
        };

        var ok = task.Execute();

        Assert.That(ok, Is.False);
        Assert.That(engine.Errors, Has.Count.GreaterThan(0));
    }

    [Test]
    public void Execute_missingManifest_logsErrorAndReturnsFalse()
    {
        var exePath = Path.Combine(_tempDir, "test.exe");
        File.WriteAllBytes(exePath, new byte[] { 0x4D, 0x5A });

        var engine = new StubBuildEngine();
        var task = new AppendPayloadToSelf
        {
            BuildEngine = engine,
            InstallerExePath = exePath,
            ManifestPath = Path.Combine(_tempDir, "missing.json"),
            PayloadFiles = Array.Empty<ITaskItem>(),
        };

        Assert.That(task.Execute(), Is.False);
        Assert.That(engine.Errors, Has.Count.GreaterThan(0));
    }

    [Test]
    public void Execute_missingSourceFile_warnsAndSkips()
    {
        var exePath = Path.Combine(_tempDir, "test.exe");
        var manifestPath = Path.Combine(_tempDir, "instella.json");
        var archivePath = Path.Combine(_tempDir, "keepme.zip");
        File.WriteAllBytes(exePath, new byte[] { 0x4D, 0x5A });
        File.WriteAllText(manifestPath, "{}");

        var engine = new StubBuildEngine();
        var files = new[]
        {
            MakePayloadFile("real.txt", "here"),
            new TaskItem(Path.Combine(_tempDir, "ghost.txt"), new System.Collections.Generic.Dictionary<string, string>
            {
                ["TargetPath"] = "ghost.txt",
            }),
        };

        var task = new AppendPayloadToSelf
        {
            BuildEngine = engine,
            InstallerExePath = exePath,
            ManifestPath = manifestPath,
            PayloadFiles = files,
            IntermediateArchivePath = archivePath,
        };

        Assert.That(task.Execute(), Is.True);
        Assert.That(engine.Warnings, Has.Some.Property("Message").Contains("ghost.txt"));
        Assert.That(ReadZipEntries(archivePath), Is.EquivalentTo(new[] { "real.txt" }));
    }

    [Test]
    public void Execute_customIntermediateArchive_keepsFileAfter()
    {
        var exePath = Path.Combine(_tempDir, "test.exe");
        var manifestPath = Path.Combine(_tempDir, "instella.json");
        var archivePath = Path.Combine(_tempDir, "keepme.zip");

        File.WriteAllBytes(exePath, new byte[] { 0x4D, 0x5A });
        File.WriteAllText(manifestPath, "{}");

        var task = new AppendPayloadToSelf
        {
            BuildEngine = new StubBuildEngine(),
            InstallerExePath = exePath,
            ManifestPath = manifestPath,
            PayloadFiles = new[] { MakePayloadFile("a.txt", "x") },
            IntermediateArchivePath = archivePath,
        };

        Assert.That(task.Execute(), Is.True);
        // When the caller supplies the intermediate path explicitly, the
        // task must NOT delete it — callers may re-use the archive for
        // reproducibility checks or signing.
        Assert.That(File.Exists(archivePath), Is.True);
    }

    [Test]
    public void Execute_targetPathMissing_fallsBackToFilename()
    {
        var exePath = Path.Combine(_tempDir, "test.exe");
        var manifestPath = Path.Combine(_tempDir, "instella.json");
        var archivePath = Path.Combine(_tempDir, "keepme.zip");
        File.WriteAllBytes(exePath, new byte[] { 0x4D, 0x5A });
        File.WriteAllText(manifestPath, "{}");

        var srcPath = Path.Combine(_tempDir, "buried", "deep", "foo.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(srcPath)!);
        File.WriteAllText(srcPath, "contents");

        // TaskItem with no TargetPath metadata → task uses the file's base name.
        var item = new TaskItem(srcPath);

        var task = new AppendPayloadToSelf
        {
            BuildEngine = new StubBuildEngine(),
            InstallerExePath = exePath,
            ManifestPath = manifestPath,
            PayloadFiles = new ITaskItem[] { item },
            IntermediateArchivePath = archivePath,
        };

        Assert.That(task.Execute(), Is.True);
        Assert.That(ReadZipEntries(archivePath), Is.EquivalentTo(new[] { "foo.dll" }));
    }

    [Test]
    public void Execute_targetPathCollision_lastWritesAndWarns()
    {
        var exePath = Path.Combine(_tempDir, "test.exe");
        var manifestPath = Path.Combine(_tempDir, "instella.json");
        var archivePath = Path.Combine(_tempDir, "keepme.zip");
        File.WriteAllBytes(exePath, new byte[] { 0x4D, 0x5A });
        File.WriteAllText(manifestPath, "{}");

        var firstSrc = Path.Combine(_tempDir, "first.dll");
        var secondSrc = Path.Combine(_tempDir, "second.dll");
        File.WriteAllText(firstSrc, "FIRST");
        File.WriteAllText(secondSrc, "SECOND");

        var engine = new StubBuildEngine();
        var task = new AppendPayloadToSelf
        {
            BuildEngine = engine,
            InstallerExePath = exePath,
            ManifestPath = manifestPath,
            PayloadFiles = new ITaskItem[]
            {
                new TaskItem(firstSrc, new System.Collections.Generic.Dictionary<string, string> { ["TargetPath"] = "shared.dll" }),
                new TaskItem(secondSrc, new System.Collections.Generic.Dictionary<string, string> { ["TargetPath"] = "shared.dll" }),
            },
            IntermediateArchivePath = archivePath,
        };

        Assert.That(task.Execute(), Is.True);
        Assert.That(engine.Warnings, Has.Some.Property("Message").Contains("shared.dll"),
            "Collision should emit an MSBuild warning naming the conflicting target path.");

        using var zip = ZipFile.OpenRead(archivePath);
        Assert.That(zip.Entries.Count, Is.EqualTo(1));
        using var reader = new StreamReader(zip.Entries[0].Open());
        Assert.That(reader.ReadToEnd(), Is.EqualTo("SECOND"),
            "Last item with the same TargetPath wins — second source's bytes should be in the zip.");
    }

    [Test]
    public void Execute_backslashesInTargetPath_normalizedToForwardSlash()
    {
        var exePath = Path.Combine(_tempDir, "test.exe");
        var manifestPath = Path.Combine(_tempDir, "instella.json");
        var archivePath = Path.Combine(_tempDir, "keepme.zip");
        File.WriteAllBytes(exePath, new byte[] { 0x4D, 0x5A });
        File.WriteAllText(manifestPath, "{}");

        var src = Path.Combine(_tempDir, "src.txt");
        File.WriteAllText(src, "x");

        var item = new TaskItem(src, new System.Collections.Generic.Dictionary<string, string>
        {
            ["TargetPath"] = @"sub\nested\file.txt",
        });

        var task = new AppendPayloadToSelf
        {
            BuildEngine = new StubBuildEngine(),
            InstallerExePath = exePath,
            ManifestPath = manifestPath,
            PayloadFiles = new ITaskItem[] { item },
            IntermediateArchivePath = archivePath,
        };

        Assert.That(task.Execute(), Is.True);
        Assert.That(ReadZipEntries(archivePath), Is.EquivalentTo(new[] { "sub/nested/file.txt" }));
    }

    [Test]
    public void Execute_payloadFilterExclude_pdbDropped()
    {
        var (exePath, manifestPath, archivePath, files) = SetupFilterFixture(
            manifestJson: """{"payloadFilter":{"exclude":["**/*.pdb"]}}""",
            files: new (string target, string content)[]
            {
                ("app.dll", "dll"),
                ("app.pdb", "pdb-should-be-excluded"),
                ("sub/child.dll", "child"),
                ("sub/child.pdb", "child-pdb-should-be-excluded"),
            });

        var task = new AppendPayloadToSelf
        {
            BuildEngine = new StubBuildEngine(),
            InstallerExePath = exePath,
            ManifestPath = manifestPath,
            PayloadFiles = files,
            IntermediateArchivePath = archivePath,
        };

        Assert.That(task.Execute(), Is.True);

        var entries = ReadZipEntries(archivePath);
        Assert.That(entries, Contains.Item("app.dll"));
        Assert.That(entries, Contains.Item("sub/child.dll"));
        Assert.That(entries, Does.Not.Contain("app.pdb"));
        Assert.That(entries, Does.Not.Contain("sub/child.pdb"));
    }

    [Test]
    public void Execute_payloadFilterInclude_onlyMatchesZipped()
    {
        var (exePath, manifestPath, archivePath, files) = SetupFilterFixture(
            manifestJson: """{"payloadFilter":{"include":["**/*.dll"]}}""",
            files: new (string, string)[]
            {
                ("app.dll", "dll"),
                ("app.exe", "exe"),
                ("app.pdb", "pdb"),
            });

        var task = new AppendPayloadToSelf
        {
            BuildEngine = new StubBuildEngine(),
            InstallerExePath = exePath,
            ManifestPath = manifestPath,
            PayloadFiles = files,
            IntermediateArchivePath = archivePath,
        };

        Assert.That(task.Execute(), Is.True);

        var entries = ReadZipEntries(archivePath);
        Assert.That(entries, Is.EquivalentTo(new[] { "app.dll" }));
    }

    [Test]
    public void Execute_payloadFilterIncludePlusExclude_excludeWins()
    {
        var (exePath, manifestPath, archivePath, files) = SetupFilterFixture(
            manifestJson: """{"payloadFilter":{"include":["**/*"],"exclude":["**/debug/*"]}}""",
            files: new (string, string)[]
            {
                ("app.dll", "dll"),
                ("debug/symbols.pdb", "debug"),
                ("release/app.exe", "exe"),
            });

        var task = new AppendPayloadToSelf
        {
            BuildEngine = new StubBuildEngine(),
            InstallerExePath = exePath,
            ManifestPath = manifestPath,
            PayloadFiles = files,
            IntermediateArchivePath = archivePath,
        };

        Assert.That(task.Execute(), Is.True);

        var entries = ReadZipEntries(archivePath);
        Assert.That(entries, Is.EquivalentTo(new[] { "app.dll", "release/app.exe" }));
    }

    [Test]
    public void Execute_manifestWithoutFilter_includesEveryItem()
    {
        var (exePath, manifestPath, archivePath, files) = SetupFilterFixture(
            manifestJson: """{"appName":"NoFilter"}""",
            files: new (string, string)[]
            {
                ("a.txt", "1"),
                ("b.pdb", "2"),
            });

        var task = new AppendPayloadToSelf
        {
            BuildEngine = new StubBuildEngine(),
            InstallerExePath = exePath,
            ManifestPath = manifestPath,
            PayloadFiles = files,
            IntermediateArchivePath = archivePath,
        };

        Assert.That(task.Execute(), Is.True);

        var entries = ReadZipEntries(archivePath);
        Assert.That(entries, Is.EquivalentTo(new[] { "a.txt", "b.pdb" }));
    }

    [Test]
    public void Execute_payloadFilterEmptyArrays_includesEveryItem()
    {
        var (exePath, manifestPath, archivePath, files) = SetupFilterFixture(
            manifestJson: """{"payloadFilter":{"include":[],"exclude":[]}}""",
            files: new (string, string)[]
            {
                ("a.txt", "1"),
                ("b.pdb", "2"),
            });

        var task = new AppendPayloadToSelf
        {
            BuildEngine = new StubBuildEngine(),
            InstallerExePath = exePath,
            ManifestPath = manifestPath,
            PayloadFiles = files,
            IntermediateArchivePath = archivePath,
        };

        Assert.That(task.Execute(), Is.True);

        var entries = ReadZipEntries(archivePath);
        Assert.That(entries, Is.EquivalentTo(new[] { "a.txt", "b.pdb" }),
            "A payloadFilter object with both lists empty is treated as absent so no items are dropped.");
    }

    // ---- executable name and icon are resolved at build time ----

    [Test]
    public void Execute_singlePayloadExecutable_isWrittenIntoTheManifest()
    {
        var (exe, manifest, archive) = Fixture("""{"appName":"A","appId":"a"}""");
        var task = NewTask(exe, manifest, archive, new StubBuildEngine(), names: ["QuickNotes.exe"]);

        Assert.That(task.Execute(), Is.True);
        Assert.That(EmbeddedManifest(exe), Does.Contain("\"executableName\":\"QuickNotes.exe\""));
        Assert.That(File.ReadAllText(manifest), Does.Not.Contain("executableName"), "the emitted manifest is never rewritten");
    }

    [Test]
    public void Execute_explicitExecutableName_isKept()
    {
        var (exe, manifest, archive) = Fixture("""{"appName":"A","appId":"a","executableName":"Main.exe"}""");
        var task = NewTask(exe, manifest, archive, new StubBuildEngine(), names: ["Other.exe", "Third.exe"]);

        Assert.That(task.Execute(), Is.True);
        Assert.That(EmbeddedManifest(exe), Does.Contain("Main.exe"));
    }

    [Test]
    public void Execute_multiplePayloadProjectsWithoutExecutableName_failsWithINSTELLA0102()
    {
        var engine = new StubBuildEngine();
        var (exe, manifest, archive) = Fixture("""{"appName":"A","appId":"a"}""");
        var task = NewTask(exe, manifest, archive, engine, names: ["One.exe", "Two.exe"]);

        Assert.That(task.Execute(), Is.False);
        Assert.That(engine.Errors.Select(e => e.Code), Does.Contain("INSTELLA0102"));
    }

    [Test]
    public void Execute_icon_isEmbeddedAtOwnedPath_andManifestRewritten()
    {
        var icon = Path.Combine(_tempDir, "brand.ico");
        File.WriteAllBytes(icon, [0, 0, 1, 0]);
        var (exe, manifest, archive) = Fixture($$"""{"appName":"A","appId":"a","iconPath":{{System.Text.Json.JsonSerializer.Serialize(icon)}}}""");
        var task = NewTask(exe, manifest, archive, new StubBuildEngine(), names: ["A.exe"]);
        task.ApplicationIcon = "set.ico";   // the exe icon comes from the project; nothing to stamp

        Assert.That(task.Execute(), Is.True);
        Assert.That(ReadZipEntries(archive), Does.Contain(".instella/app.ico"));
        Assert.That(EmbeddedManifest(exe), Does.Contain("\"iconPath\":\".instella/app.ico\""));
    }

    [Test]
    public void Execute_missingIcon_failsWithINSTELLA0104()
    {
        var engine = new StubBuildEngine();
        var (exe, manifest, archive) = Fixture("""{"appName":"A","appId":"a","iconPath":"C:/nope/missing.ico"}""");

        Assert.That(NewTask(exe, manifest, archive, engine, names: ["A.exe"]).Execute(), Is.False);
        Assert.That(engine.Errors.Select(e => e.Code), Does.Contain("INSTELLA0104"));
    }

    // ---- idempotent, deterministic, signed stub ----

    [Test]
    public void Execute_twice_replacesThePayload_andProducesIdenticalBytes()
    {
        var (exe, manifest, _) = Fixture("""{"appName":"A","appId":"a","executableName":"A.exe"}""");
        var files = new[] { MakePayloadFile("A.exe", "app"), MakePayloadFile("lib/b.dll", "lib") };
        var first = Run(exe, manifest, files);
        var second = Run(exe, manifest, files);

        Assert.That(second, Is.EqualTo(first), "republishing never stacks payloads, and the output is deterministic");
    }

    [Test]
    public void Execute_withAStamp_skipsAnUnchangedRepublish_butNotAFreshExe()
    {
        var (exe, manifest, _) = Fixture("""{"appName":"A","appId":"a","executableName":"A.exe"}""");
        var files = new[] { MakePayloadFile("A.exe", "app") };
        var stamp = Path.Combine(_tempDir, "append.stamp");
        var first = Run(exe, manifest, files, stamp: stamp);
        var writeTime = File.GetLastWriteTimeUtc(exe);

        var engine = new StubBuildEngine();
        Run(exe, manifest, files, engine, stamp: stamp);
        Assert.That(File.GetLastWriteTimeUtc(exe), Is.EqualTo(writeTime), "an unchanged republish does not touch the exe");

        File.WriteAllBytes(exe, [0x4D, 0x5A, 0x90, 0x00]);   // publish copied a fresh, payload-less exe
        Assert.That(Run(exe, manifest, files, stamp: stamp), Is.EqualTo(first));
    }

    [Test]
    public void Execute_zipEntries_areSortedWithFixedTimestampsAndUnixModes()
    {
        var (exe, manifest, archive) = Fixture("""{"appName":"A","appId":"a","executableName":"A.exe"}""");
        var task = new AppendPayloadToSelf
        {
            BuildEngine = new StubBuildEngine(),
            InstallerExePath = exe,
            ManifestPath = manifest,
            PayloadFiles = [MakePayloadFile("z.txt", "z"), MakePayloadFile("A.exe", "app"), MakePayloadFile("m/n.dll", "n")],
            IntermediateArchivePath = archive,
        };

        Assert.That(task.Execute(), Is.True);
        using var zip = ZipFile.OpenRead(archive);
        Assert.That(zip.Entries.Select(e => e.FullName), Is.EqualTo(new[] { "A.exe", "m/n.dll", "z.txt" }));
        Assert.That(zip.Entries.Select(e => e.LastWriteTime.Year), Is.All.EqualTo(1980));
        var modeOf = zip.Entries.ToDictionary(e => e.FullName, e => (e.ExternalAttributes >> 16) & 0x1FF);
        if (OperatingSystem.IsWindows())
        {
            Assert.That(modeOf["A.exe"], Is.EqualTo(Convert.ToInt32("755", 8)), "the main executable is marked executable");
            Assert.That(modeOf["z.txt"], Is.EqualTo(Convert.ToInt32("644", 8)));
        }
    }

    [Test]
    public void Execute_withAStubPath_carriesTheStrippedExeAsTheStub()
    {
        var (exe, manifest, archive) = Fixture("""{"appName":"A","appId":"a","executableName":"A.exe"}""");
        var stub = Path.Combine(_tempDir, "stub", "installer.exe");
        var task = NewTask(exe, manifest, archive, new StubBuildEngine(), names: []);
        task.StubOutputPath = stub;

        Assert.That(task.Execute(), Is.True);
        using var zip = ZipFile.OpenRead(archive);
        var entry = zip.GetEntry(".instella/instella.exe");
        Assert.That(entry, Is.Not.Null);
        using var content = new MemoryStream();
        using (var s = entry!.Open()) s.CopyTo(content);
        Assert.That(content.ToArray(), Is.EqualTo(new byte[] { 0x4D, 0x5A, 0x90, 0x00 }), "the stub is the exe without any payload");
        if (OperatingSystem.IsWindows())
            Assert.That((entry.ExternalAttributes >> 16) & 0x1FF, Is.EqualTo(Convert.ToInt32("755", 8)));
    }

    [Test]
    public void Execute_withASignCommand_signsTheStubAndTheInstaller()
    {
        var (exe, manifest, archive) = Fixture("""{"appName":"A","appId":"a","executableName":"A.exe"}""");
        var stub = Path.Combine(_tempDir, "stub", "installer.exe");
        var task = NewTask(exe, manifest, archive, new StubBuildEngine(), names: []);
        task.StubOutputPath = stub;
        task.SignCommand = "echo signed> \"{0}.sig\"";

        Assert.That(task.Execute(), Is.True);
        Assert.That(File.Exists(stub + ".sig"), Is.True, "the stub is signed before it is zipped");
        Assert.That(File.Exists(exe + ".sig"), Is.True, "the finished installer is signed");
    }

    [TestCase("echo no placeholder")]
    [TestCase("exit 3 {0}")]
    public void Execute_aBadSignCommand_failsWithINSTELLA0204(string command)
    {
        var engine = new StubBuildEngine();
        var (exe, manifest, archive) = Fixture("""{"appName":"A","appId":"a","executableName":"A.exe"}""");
        var task = NewTask(exe, manifest, archive, engine, names: []);
        task.SignCommand = command;

        Assert.That(task.Execute(), Is.False);
        Assert.That(engine.Errors.Select(e => e.Code), Does.Contain("INSTELLA0204"));
    }

    [Test]
    public void Execute_anAlreadySignedExe_failsWithINSTELLA0201()
    {
        var engine = new StubBuildEngine();
        var (exe, manifest, archive) = Fixture("""{"appName":"A","appId":"a","executableName":"A.exe"}""");
        File.WriteAllBytes(exe, PeFixtures.SignedPe());

        Assert.That(NewTask(exe, manifest, archive, engine, names: []).Execute(), Is.False);
        Assert.That(engine.Errors.Select(e => e.Code), Does.Contain("INSTELLA0201"));
        Assert.That(File.ReadAllBytes(exe), Is.EqualTo(PeFixtures.SignedPe()), "the signed exe is left untouched");
    }

    [Test]
    public void Execute_overItsOwnSignedOutput_dropsSignatureAndPayload_andAppendsAgain()
    {
        // A signed publish followed by a republish without a clean build.
        var (exe, manifest, _) = Fixture("""{"appName":"A","appId":"a","executableName":"A.exe"}""");
        var unsignedPe = PeFixtures.UnsignedPe();
        File.WriteAllBytes(exe, unsignedPe);
        var files = new[] { MakePayloadFile("A.exe", "app") };
        var appended = Run(exe, manifest, files);
        File.WriteAllBytes(exe, PeFixtures.SimulateSigning(appended));

        Assert.That(Run(exe, manifest, files), Is.EqualTo(appended), "same bytes as the unsigned publish");
    }

    [Test]
    [Platform("Win")]
    public void Execute_icoIcon_isStampedIntoTheExe_whenNoApplicationIcon()
    {
        var exe = Path.Combine(_tempDir, "host.exe");
        File.Copy(Environment.ProcessPath!, exe);
        var icon = Path.Combine(_tempDir, "brand.ico");
        File.WriteAllBytes(icon, PeFixtures.PngIcon());
        var manifest = Path.Combine(_tempDir, "stamp.json");
        File.WriteAllText(manifest, $$"""{"appName":"A","appId":"a","executableName":"A.exe","iconPath":{{System.Text.Json.JsonSerializer.Serialize(icon)}}}""");

        var task = new AppendPayloadToSelf
        {
            BuildEngine = new StubBuildEngine(),
            InstallerExePath = exe,
            ManifestPath = manifest,
            PayloadFiles = [MakePayloadFile("A.exe", "app")],
        };
        // A copy of a signed host exe: drop the signature first, as a fresh link would have none.
        PeFixtures.StripSignature(exe);

        Assert.That(task.Execute(), Is.True);
        Assert.That(PeFixtures.HasGroupIcon(exe, 1), Is.True, "RT_GROUP_ICON 1 was written");
    }

    private byte[] Run(string exe, string manifest, ITaskItem[] files, StubBuildEngine? engine = null, string? stamp = null)
    {
        var task = new AppendPayloadToSelf
        {
            BuildEngine = engine ?? new StubBuildEngine(),
            InstallerExePath = exe,
            ManifestPath = manifest,
            PayloadFiles = files,
            StampPath = stamp ?? string.Empty,
        };
        Assert.That(task.Execute(), Is.True);
        return File.ReadAllBytes(exe);
    }

    private static string EmbeddedManifest(string exe)
    {
        using var stream = File.OpenRead(exe);
        var footer = Instella.Core.Internal.PayloadFooterReader.ReadFooter(stream, stream.Length);
        Assert.That(footer.IsValid, Is.True);
        var bytes = new byte[footer.ManifestLength];
        stream.Seek(footer.ManifestOffset, SeekOrigin.Begin);
        stream.ReadExactly(bytes);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    private (string Exe, string Manifest, string Archive) Fixture(string manifestJson)
    {
        var exe = Path.Combine(_tempDir, $"{Guid.NewGuid():N}.exe");
        var manifest = Path.Combine(_tempDir, $"{Guid.NewGuid():N}.json");
        File.WriteAllBytes(exe, [0x4D, 0x5A, 0x90, 0x00]);
        File.WriteAllText(manifest, manifestJson);
        return (exe, manifest, Path.Combine(_tempDir, $"{Guid.NewGuid():N}.zip"));
    }

    private AppendPayloadToSelf NewTask(string exe, string manifest, string archive, StubBuildEngine engine, string[] names) => new()
    {
        BuildEngine = engine,
        InstallerExePath = exe,
        ManifestPath = manifest,
        PayloadFiles = [MakePayloadFile("app.dll", "dll")],
        PayloadExecutableNames = names,
        IntermediateArchivePath = archive,
    };

    private ITaskItem MakePayloadFile(string targetPath, string content)
    {
        var sourcePath = Path.Combine(_tempDir, "src-" + Guid.NewGuid().ToString("N"), targetPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        File.WriteAllText(sourcePath, content);
        return new TaskItem(sourcePath, new System.Collections.Generic.Dictionary<string, string>
        {
            ["TargetPath"] = targetPath,
        });
    }

    private (string exe, string manifest, string archive, ITaskItem[] files) SetupFilterFixture(
        string manifestJson, (string target, string content)[] files)
    {
        var exePath = Path.Combine(_tempDir, "test.exe");
        var manifestPath = Path.Combine(_tempDir, "instella.json");
        var archivePath = Path.Combine(_tempDir, "keepme.zip");

        File.WriteAllBytes(exePath, new byte[] { 0x4D, 0x5A, 0x90, 0x00 });
        File.WriteAllText(manifestPath, manifestJson);

        var items = files.Select(f => MakePayloadFile(f.target, f.content)).ToArray();
        return (exePath, manifestPath, archivePath, items);
    }

    private static string[] ReadZipEntries(string archivePath)
    {
        using var zip = ZipFile.OpenRead(archivePath);
        return zip.Entries.Select(e => e.FullName.Replace('\\', '/')).ToArray();
    }

    private sealed class StubBuildEngine : IBuildEngine
    {
        public List<BuildErrorEventArgs> Errors { get; } = new();
        public List<BuildWarningEventArgs> Warnings { get; } = new();
        public bool ContinueOnError => false;
        public int LineNumberOfTaskNode => 0;
        public int ColumnNumberOfTaskNode => 0;
        public string ProjectFileOfTaskNode => string.Empty;

        public bool BuildProjectFile(string projectFileName, string[] targetNames, System.Collections.IDictionary globalProperties, System.Collections.IDictionary targetOutputs) => true;
        public void LogCustomEvent(CustomBuildEventArgs e) { }
        public void LogErrorEvent(BuildErrorEventArgs e) => Errors.Add(e);
        public void LogMessageEvent(BuildMessageEventArgs e) { }
        public void LogWarningEvent(BuildWarningEventArgs e) => Warnings.Add(e);
    }
}
