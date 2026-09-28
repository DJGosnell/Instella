using Instella.Core.FileSystem;
using NUnit.Framework;

namespace Instella.Core.Tests;

/// <summary>The real file system's entry states and link handling, on a real temp folder.</summary>
[TestFixture]
public class RealFileSystemEntryTests
{
    private string _root = null!;
    private readonly RealFileSystem _fs = RealFileSystem.Instance;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "instella-fs-entry-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "dir", "sub"));
        File.WriteAllText(Path.Combine(_root, "dir", "a.txt"), "a");
        File.WriteAllText(Path.Combine(_root, "dir", "sub", "b.txt"), "b");
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Test]
    public void GetEntryState_FileDirectoryMissing()
    {
        Assert.That(_fs.GetEntryState(Path.Combine(_root, "dir", "a.txt")), Is.EqualTo(FileSystemEntryState.File));
        Assert.That(_fs.GetEntryState(Path.Combine(_root, "dir")), Is.EqualTo(FileSystemEntryState.Directory));
        Assert.That(_fs.GetEntryState(Path.Combine(_root, "none.txt")), Is.EqualTo(FileSystemEntryState.Missing));
        Assert.That(_fs.GetEntryState(Path.Combine(_root, "no-dir", "none.txt")), Is.EqualTo(FileSystemEntryState.Missing));
    }

    [Test]
    public void GetEntryState_AnInvalidPath_IsDenied_NotMissing()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("Windows path rules");
        Assert.That(_fs.GetEntryState(Path.Combine(_root, "bad|name")), Is.Not.EqualTo(FileSystemEntryState.File));
    }

    [Test]
    public void EnumerateFilesWithoutLinks_ListsEverythingWhenThereAreNoLinks()
    {
        Assert.That(_fs.EnumerateFilesWithoutLinks(_root, "*").Select(Path.GetFileName), Is.EquivalentTo(new[] { "a.txt", "b.txt" }));
        Assert.That(_fs.EnumerateFilesWithoutLinks(_root, "b.txt").Select(Path.GetFileName), Is.EqualTo(new[] { "b.txt" }));
        Assert.That(_fs.IsLink(Path.Combine(_root, "dir")), Is.False);
        Assert.That(_fs.IsLink(Path.Combine(_root, "missing")), Is.False);
    }

    [Test]
    public void ALink_IsReported_AndNotFollowed()
    {
        var link = Path.Combine(_root, "link");
        try
        {
            Directory.CreateSymbolicLink(link, Path.Combine(_root, "dir", "sub"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A junction needs no privilege on Windows.
            if (!OperatingSystem.IsWindows() || !TryCreateJunction(link, Path.Combine(_root, "dir", "sub")))
                Assert.Ignore($"cannot create a link here: {ex.Message}");
        }

        Assert.That(_fs.IsLink(link), Is.True);
        Assert.That(_fs.EnumerateFiles(_root, "b.txt", recursive: true).Count(), Is.EqualTo(2), "EnumerateFiles follows the link");
        Assert.That(_fs.EnumerateFilesWithoutLinks(_root, "b.txt").Count(), Is.EqualTo(1), "EnumerateFilesWithoutLinks does not");
    }

    private static bool TryCreateJunction(string link, string target)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var a in new[] { "/c", "mklink", "/J", link, target }) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.WaitForExit(10_000);
        return p.ExitCode == 0 && Directory.Exists(link);
    }
}
