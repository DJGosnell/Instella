using Instella.Core.FileSystem;
using Instella.Core.Platform;
using NUnit.Framework;

namespace Instella.Installer.Testing.Tests;

/// <summary>The fakes report denied access the way the real file system and registry do.</summary>
[TestFixture]
public class AccessDeniedFakesTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "instella-denied-fakes");
    private static readonly string Locked = Path.Combine(Root, "Locked");
    private static readonly string File1 = Path.Combine(Locked, "a.txt");
    private static readonly string Nested = Path.Combine(Locked, "sub", "b.txt");
    private static readonly string Outside = Path.Combine(Root, "Open", "c.txt");

    private static InMemoryFileSystem Seeded()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile(File1, [1]);
        fs.AddFile(Nested, [2]);
        fs.AddFile(Outside, [3]);
        return fs;
    }

    private static void AssertDenied(FileSystemResult result)
    {
        Assert.That(result.Success, Is.False);
        Assert.That(result.Error!.Type, Is.EqualTo(FileSystemErrorType.AccessDenied));
        Assert.That(result.Error.Message, Does.Contain("denied"));
    }

    [Test]
    public async Task DenyWrites_FailsEveryChange_AndKeepsReads()
    {
        var fs = Seeded();
        fs.DenyWrites(Locked);
        var ct = CancellationToken.None;

        AssertDenied(await fs.DeleteFileAsync(File1, ct));
        AssertDenied(await fs.DeleteFileAsync(Nested, ct));
        AssertDenied(await fs.MoveFileAsync(File1, Outside + ".moved", overwrite: false, ct));
        AssertDenied(await fs.MoveFileAsync(Outside, Path.Combine(Locked, "in.txt"), overwrite: false, ct));
        AssertDenied(await fs.CopyFileAsync(Outside, Path.Combine(Locked, "copy.txt"), overwrite: false, ct));
        AssertDenied(await fs.WriteAllBytesAsync(File1, [9], ct));
        AssertDenied(await fs.CreateDirectoryAsync(Path.Combine(Locked, "new"), ct));
        AssertDenied(await fs.DeleteDirectoryAsync(Locked, recursive: true, ct));
        AssertDenied(await fs.DeleteDirectoryAsync(Root, recursive: true, ct));
        Assert.That((await fs.OpenWriteAsync(File1, ct)).Success, Is.False);

        Assert.That(fs.Exists(File1), Is.True, "reads still work");
        Assert.That((await fs.ReadAllBytesAsync(File1, ct)).Value, Is.EqualTo(new byte[] { 1 }));
        Assert.That(fs.EnumerateFiles(Locked, "*", recursive: true).Count(), Is.EqualTo(2));
        Assert.That((await fs.CopyFileAsync(File1, Outside + ".copy", overwrite: false, ct)).Success, Is.True, "copying out is a read");
        Assert.That((await fs.DeleteFileAsync(Outside, ct)).Success, Is.True, "elsewhere is unaffected");
        Assert.That(fs.Exists(File1) && fs.Exists(Nested), Is.True);
    }

    [Test]
    public async Task DenyReads_HidesTheFolder_LikeTheRealFileSystem()
    {
        var fs = Seeded();
        fs.DenyReads(Locked);
        var ct = CancellationToken.None;

        Assert.That(fs.Exists(File1), Is.False, "File.Exists returns false without read access");
        Assert.That(fs.DirectoryExists(Locked), Is.False);
        Assert.That(fs.GetFileSize(File1), Is.EqualTo(-1));
        Assert.That((await fs.ReadAllBytesAsync(File1, ct)).Error!.Type, Is.EqualTo(FileSystemErrorType.AccessDenied));
        Assert.That((await fs.OpenReadAsync(File1, ct)).Error!.Type, Is.EqualTo(FileSystemErrorType.AccessDenied));
        Assert.Throws<UnauthorizedAccessException>(() => fs.EnumerateFiles(Locked));
        Assert.Throws<UnauthorizedAccessException>(() => fs.EnumerateFiles(Root, "*", recursive: true), "a recursive listing reaches it");
        Assert.That(fs.EnumerateFiles(Root, "*", recursive: false), Is.Empty, "a flat listing of the parent does not");
        Assert.ThrowsAsync<UnauthorizedAccessException>(() => fs.ComputeSha256Async(File1, ct));
        AssertDenied(await fs.DeleteFileAsync(File1, ct));
        AssertDenied(await fs.CopyFileAsync(File1, Outside + ".copy", overwrite: false, ct));
    }

    [Test]
    public async Task AllowAll_RemovesTheDenials()
    {
        var fs = Seeded();
        fs.DenyReads(Locked);
        fs.DenyWrites(Root);
        fs.AllowAll();
        Assert.That(fs.Exists(File1), Is.True);
        Assert.That((await fs.DeleteFileAsync(File1, CancellationToken.None)).Success, Is.True);
    }

    [Test]
    public void SeedingStillWorks_UnderADeniedFolder()
    {
        var fs = new InMemoryFileSystem();
        fs.DenyWrites(Locked);
        fs.AddFile(File1, [1]);
        Assert.That(fs.Exists(File1), Is.True);
    }

    [Test]
    public void GetEntryState_TellsDeniedFromMissing()
    {
        var fs = Seeded();
        Assert.That(fs.GetEntryState(File1), Is.EqualTo(FileSystemEntryState.File));
        Assert.That(fs.GetEntryState(Locked), Is.EqualTo(FileSystemEntryState.Directory));
        Assert.That(fs.GetEntryState(Path.Combine(Root, "nothing")), Is.EqualTo(FileSystemEntryState.Missing));
        fs.DenyReads(Locked);
        Assert.That(fs.GetEntryState(File1), Is.EqualTo(FileSystemEntryState.Denied));
        Assert.That(fs.GetEntryState(Locked), Is.EqualTo(FileSystemEntryState.Denied));
        Assert.That(fs.Exists(File1), Is.False, "Exists still answers false, as File.Exists does");
    }

    [Test]
    public void DeniedFilesInAReadableFolder_AreStillListed()
    {
        var fs = Seeded();
        fs.DenyReads(File1);
        Assert.That(fs.EnumerateFiles(Root, "*", recursive: true), Has.Member(File1));
    }

    [Test]
    public void Links_AreReported_AndNotFollowedByTheLinkFreeListing()
    {
        var fs = Seeded();
        var link = Path.Combine(Locked, "sub");
        fs.AddLink(link);
        Assert.That(fs.IsLink(link), Is.True);
        Assert.That(fs.IsLink(Locked), Is.False);
        Assert.That(fs.EnumerateFiles(Locked, "*", recursive: true), Has.Member(Nested), "EnumerateFiles follows it");
        Assert.That(fs.EnumerateFilesWithoutLinks(Locked, "*"), Is.EqualTo(new[] { File1 }), "EnumerateFilesWithoutLinks does not");
        Assert.That(fs.EnumerateFilesWithoutLinks(Root, "b.txt"), Is.Empty);
    }

    [Test]
    public async Task TryReadRegistryValue_ReportsDenied_ReadRegistryValue_LooksAbsent()
    {
        var platform = new FakePlatformServices();
        platform.Registry.Set(RegistryHive.CurrentUser, @"Software\Locked", "V", InstellaRegistryValueKind.String, "x");
        var ok = await platform.TryReadRegistryValueAsync(RegistryHive.CurrentUser, @"Software\Locked", "V", true, CancellationToken.None);
        Assert.That((ok.Failed, ok.Value!.Value), Is.EqualTo((false, (object)"x")));
        var missing = await platform.TryReadRegistryValueAsync(RegistryHive.CurrentUser, @"Software\Locked", "Nope", true, CancellationToken.None);
        Assert.That((missing.Failed, missing.Value), Is.EqualTo((false, (RegistryValueData?)null)));

        platform.DenyRegistryReads(RegistryHive.CurrentUser, @"Software\Locked");
        var denied = await platform.TryReadRegistryValueAsync(RegistryHive.CurrentUser, @"Software\Locked", "V", true, CancellationToken.None);
        Assert.That(denied.Failed, Is.True);
        Assert.That(denied.Error, Does.Contain("denied"));
    }

    [Test]
    public async Task DenyRegistryWrites_FailsWritesAndDeletes_AndKeepsReads()
    {
        var platform = new FakePlatformServices();
        platform.Registry.Set(RegistryHive.CurrentUser, @"Software\Locked\Sub", "V", InstellaRegistryValueKind.String, "x");
        platform.DenyRegistryWrites(RegistryHive.CurrentUser, @"Software\Locked");
        var ct = CancellationToken.None;

        var write = await platform.WriteRegistryValueAsync(RegistryHive.CurrentUser, @"Software\Locked\Sub", "V", InstellaRegistryValueKind.String, "y", true, ct);
        Assert.That(write.Success, Is.False);
        Assert.That(write.Error, Does.Contain("denied"));
        Assert.That((await platform.DeleteRegistryValueAsync(RegistryHive.CurrentUser, @"Software\Locked\Sub", "V", true, ct)).Success, Is.False);
        Assert.That((await platform.DeleteRegistryKeyAsync(RegistryHive.CurrentUser, @"Software\Locked", true, ct)).Success, Is.False);
        Assert.That((await platform.ReadRegistryValueAsync(RegistryHive.CurrentUser, @"Software\Locked\Sub", "V", true, ct))!.Value, Is.EqualTo("x"));
        Assert.That((await platform.WriteRegistryValueAsync(RegistryHive.LocalMachine, @"Software\Locked", "V", InstellaRegistryValueKind.String, "y", false, ct)).Success,
            Is.True, "another hive is unaffected");
        Assert.That((await platform.WriteRegistryValueAsync(RegistryHive.CurrentUser, @"Software\LockedOther", "V", InstellaRegistryValueKind.String, "y", true, ct)).Success,
            Is.True, "a sibling sharing the prefix is unaffected");
    }

    [Test]
    public async Task DenyRegistryReads_MakesValuesLookAbsent()
    {
        var platform = new FakePlatformServices();
        platform.Registry.Set(RegistryHive.CurrentUser, @"Software\Locked", "V", InstellaRegistryValueKind.String, "x");
        platform.DenyRegistryReads(RegistryHive.CurrentUser, @"Software\Locked");
        Assert.That(await platform.ReadRegistryValueAsync(RegistryHive.CurrentUser, @"Software\Locked", "V", true, CancellationToken.None), Is.Null);
        Assert.That((await platform.DeleteRegistryValueAsync(RegistryHive.CurrentUser, @"Software\Locked", "V", true, CancellationToken.None)).Success, Is.False);
    }
}
