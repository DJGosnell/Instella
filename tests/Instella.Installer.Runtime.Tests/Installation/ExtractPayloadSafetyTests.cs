using System.IO.Compression;
using Instella.Core.FileSystem;
using Instella.Installer.Runtime.Installation.BuiltIn;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Installation;

/// <summary>
/// Payload entry names are untrusted. A crafted archive must never write
/// outside the install directory, and the executable bit recorded by the build must
/// reach the file system.
/// </summary>
[TestFixture]
public class ExtractPayloadSafetyTests
{
    [TestCase("../evil.dll")]
    [TestCase("a/../../evil.dll")]
    [TestCase("C:\\evil.dll")]
    [TestCase("\\\\?\\C:\\evil.dll")]
    [TestCase("CON")]
    [TestCase("a:b")]
    public void MaliciousEntry_IsRefused_AndNothingEscapes(string entryName)
    {
        var builder = new TestContextBuilder { PayloadArchive = Zip((entryName, 0)) };
        var context = builder.Build();

        Assert.ThrowsAsync<UnsafePathException>(() =>
            new ExtractPayloadStep().ExecuteAsync(context, new NullProgress(), CancellationToken.None));

        var root = Path.GetFullPath(builder.InstallPath) + Path.DirectorySeparatorChar;
        Assert.That(builder.FileSystem.Files.Keys.Where(k => !Path.GetFullPath(k).StartsWith(root, StringComparison.OrdinalIgnoreCase)),
            Is.Empty);
    }

    [Test]
    public async Task ExecutableBit_IsCarriedToTheFileSystem()
    {
        const int rwxr_xr_x = 0b111_101_101;
        const int rw_r__r__ = 0b110_100_100;
        var builder = new TestContextBuilder { PayloadArchive = Zip(("bin/tool", rwxr_xr_x), ("readme.txt", rw_r__r__)) };

        var context = builder.Build();
        var result = await new ExtractPayloadStep().ExecuteAsync(context, new NullProgress(), CancellationToken.None);
        Assert.That(result.Success, Is.True, result.Error);
        await context.Transaction!.CommitAsync();

        // The mode is set while staging and a rename keeps it.
        Assert.That(builder.FileSystem.Modes[SafePath.Combine(builder.InstallPath, "bin/tool")], Is.EqualTo(ExtractPayloadStep.ExecutableMode));
        Assert.That(builder.FileSystem.Modes.ContainsKey(SafePath.Combine(builder.InstallPath, "readme.txt")), Is.False);
    }

    [Test]
    public async Task InstalledFileList_UsesCanonicalRelativePaths()
    {
        var builder = new TestContextBuilder { PayloadArchive = Zip(("sub\\file.txt", 0)) };
        var context = builder.Build();

        await new ExtractPayloadStep().ExecuteAsync(context, new NullProgress(), CancellationToken.None);

        Assert.That(context.ExtractedFiles.Select(f => f.RelativePath), Is.EqualTo(new[] { "sub/file.txt" }));
    }

    [Test]
    public async Task CarriedStub_IsNotExtracted_ButStagedAsTheStub()
    {
        // The payload carries the signed stub; extraction leaves it to the stub step.
        var stubEntry = Instella.Core.Installation.InstellaOwnedPaths.PayloadStub;
        var builder = new TestContextBuilder { PayloadArchive = Zip(("app.dll", 0), (stubEntry, 0b111_101_101)) };
        var context = builder.Build();

        var extract = await new ExtractPayloadStep().ExecuteAsync(context, new NullProgress(), CancellationToken.None);
        Assert.That(extract.Success, Is.True, extract.Error);
        Assert.That(context.ExtractedFiles.Select(f => f.RelativePath), Is.EqualTo(new[] { "app.dll" }));

        var stage = await new StageUninstallerStubStep().ExecuteAsync(context, new NullProgress(), CancellationToken.None);
        Assert.That(stage.Success, Is.True, stage.Error);
        Assert.That(stage.Warnings, Is.Null.Or.Empty);
        await context.Transaction!.CommitAsync();

        var staged = SafePath.Combine(builder.InstallPath, StageUninstallerStubStep.UninstallExeName);
        Assert.That(builder.FileSystem.Files[staged], Is.EqualTo("x"u8.ToArray()), "the carried stub, not a truncated installer");
    }

    private static MemoryStream Zip(params (string Name, int UnixMode)[] entries)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, mode) in entries)
            {
                var entry = zip.CreateEntry(name);
                if (mode != 0) entry.ExternalAttributes = mode << 16;
                using var s = entry.Open();
                s.Write("x"u8);
            }
        }
        ms.Position = 0;
        return ms;
    }

    private sealed class NullProgress : Instella.Core.Installation.IStepProgress
    {
        public void Report(double fraction, string? status = null) { }
    }
}
