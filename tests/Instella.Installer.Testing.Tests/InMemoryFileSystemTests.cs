using System.Text;
using Instella.Core.FileSystem;
using NUnit.Framework;

namespace Instella.Installer.Testing.Tests;

[TestFixture]
public class InMemoryFileSystemTests
{
    private static readonly string Root = OperatingSystem.IsWindows() ? "C:\\fake\\root" : "/fake/root";

    [Test]
    public void AddFile_then_Exists_roundtrips()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile(Path.Combine(Root, "a.txt"), "hello"u8.ToArray());

        Assert.That(fs.Exists(Path.Combine(Root, "a.txt")), Is.True);
        Assert.That(fs.Exists(Path.Combine(Root, "missing.txt")), Is.False);
    }

    [Test]
    public async Task WriteAllBytes_stores_bytes_readable_via_ReadAllBytes()
    {
        var fs = new InMemoryFileSystem();
        var path = Path.Combine(Root, "sub", "b.bin");
        var payload = new byte[] { 1, 2, 3, 4 };

        var write = await fs.WriteAllBytesAsync(path, payload, default);
        Assert.That(write.Success, Is.True);

        var read = await fs.ReadAllBytesAsync(path, default);
        Assert.That(read.Success, Is.True);
        Assert.That(read.Value, Is.EqualTo(payload));
    }

    [Test]
    public async Task WriteAllBytes_clones_input_so_caller_mutation_does_not_affect_store()
    {
        var fs = new InMemoryFileSystem();
        var path = Path.Combine(Root, "c.bin");
        var payload = new byte[] { 10, 20, 30 };

        await fs.WriteAllBytesAsync(path, payload, default);
        payload[0] = 99;

        var read = await fs.ReadAllBytesAsync(path, default);
        Assert.That(read.Value![0], Is.EqualTo(10));
    }

    [Test]
    public void DirectoryExists_true_for_implied_parents()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile(Path.Combine(Root, "nested", "deep", "file.txt"), "x"u8.ToArray());

        Assert.That(fs.DirectoryExists(Path.Combine(Root, "nested")), Is.True);
        Assert.That(fs.DirectoryExists(Path.Combine(Root, "nested", "deep")), Is.True);
        Assert.That(fs.DirectoryExists(Path.Combine(Root, "sibling")), Is.False);
    }

    [Test]
    public async Task DeleteDirectory_recursive_removes_children()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile(Path.Combine(Root, "tree", "a.txt"), "a"u8.ToArray());
        fs.AddFile(Path.Combine(Root, "tree", "sub", "b.txt"), "b"u8.ToArray());
        fs.AddFile(Path.Combine(Root, "other.txt"), "o"u8.ToArray());

        var result = await fs.DeleteDirectoryAsync(Path.Combine(Root, "tree"), recursive: true, default);
        Assert.That(result.Success, Is.True);
        Assert.That(fs.Exists(Path.Combine(Root, "tree", "a.txt")), Is.False);
        Assert.That(fs.Exists(Path.Combine(Root, "tree", "sub", "b.txt")), Is.False);
        Assert.That(fs.Exists(Path.Combine(Root, "other.txt")), Is.True);
    }

    [Test]
    public async Task CopyFile_requires_overwrite_when_destination_exists()
    {
        var fs = new InMemoryFileSystem();
        var src = Path.Combine(Root, "src.txt");
        var dst = Path.Combine(Root, "dst.txt");
        fs.AddFile(src, "source"u8.ToArray());
        fs.AddFile(dst, "existing"u8.ToArray());

        var noOverwrite = await fs.CopyFileAsync(src, dst, overwrite: false, default);
        Assert.That(noOverwrite.Success, Is.False);

        var withOverwrite = await fs.CopyFileAsync(src, dst, overwrite: true, default);
        Assert.That(withOverwrite.Success, Is.True);

        var read = await fs.ReadAllBytesAsync(dst, default);
        Assert.That(Encoding.UTF8.GetString(read.Value!), Is.EqualTo("source"));
    }

    [Test]
    public async Task MoveFile_removes_source()
    {
        var fs = new InMemoryFileSystem();
        var src = Path.Combine(Root, "mv-src.txt");
        var dst = Path.Combine(Root, "mv-dst.txt");
        fs.AddFile(src, "payload"u8.ToArray());

        var result = await fs.MoveFileAsync(src, dst, overwrite: false, default);
        Assert.That(result.Success, Is.True);
        Assert.That(fs.Exists(src), Is.False);
        Assert.That(fs.Exists(dst), Is.True);
    }

    [Test]
    public async Task ReadAllBytes_on_missing_file_returns_NotFound()
    {
        var fs = new InMemoryFileSystem();
        var result = await fs.ReadAllBytesAsync(Path.Combine(Root, "missing"), default);
        Assert.That(result.Success, Is.False);
        Assert.That(result.Error!.Type, Is.EqualTo(FileSystemErrorType.NotFound));
    }

    [Test]
    public async Task OpenWrite_then_dispose_commits_to_store()
    {
        var fs = new InMemoryFileSystem();
        var path = Path.Combine(Root, "stream.bin");

        var streamResult = await fs.OpenWriteAsync(path, default);
        Assert.That(streamResult.Success, Is.True);
        await using (var stream = streamResult.Value!)
        {
            await stream.WriteAsync(new byte[] { 7, 8, 9 }, default);
        }

        var read = await fs.ReadAllBytesAsync(path, default);
        Assert.That(read.Value, Is.EqualTo(new byte[] { 7, 8, 9 }));
    }

    [Test]
    public void EnumerateFiles_nonRecursive_filters_nested_paths()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile(Path.Combine(Root, "a.txt"), "a"u8.ToArray());
        fs.AddFile(Path.Combine(Root, "b.txt"), "b"u8.ToArray());
        fs.AddFile(Path.Combine(Root, "nested", "c.txt"), "c"u8.ToArray());

        var top = fs.EnumerateFiles(Root, "*", recursive: false).ToList();
        Assert.That(top.Count, Is.EqualTo(2));

        var all = fs.EnumerateFiles(Root, "*", recursive: true).ToList();
        Assert.That(all.Count, Is.EqualTo(3));
    }

    [Test]
    public void EnumerateFiles_honors_glob_pattern()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile(Path.Combine(Root, "a.txt"), "a"u8.ToArray());
        fs.AddFile(Path.Combine(Root, "b.log"), "b"u8.ToArray());
        fs.AddFile(Path.Combine(Root, "c.TXT"), "c"u8.ToArray());

        var matches = fs.EnumerateFiles(Root, "*.txt", recursive: false).ToList();
        if (OperatingSystem.IsWindows())
            Assert.That(matches.Count, Is.EqualTo(2));
        else
            Assert.That(matches.Count, Is.EqualTo(1));
    }

    [Test]
    public async Task ComputeSha256_is_stable_and_hex()
    {
        var fs = new InMemoryFileSystem();
        var path = Path.Combine(Root, "hash.bin");
        fs.AddFile(path, "hello"u8.ToArray());
        var hash = await fs.ComputeSha256Async(path, default);
        Assert.That(hash, Is.EqualTo("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824"));
    }

    [Test]
    public void Snapshot_returns_independent_copy()
    {
        var fs = new InMemoryFileSystem();
        var path = Path.Combine(Root, "snap.txt");
        fs.AddFile(path, "one"u8.ToArray());

        var first = fs.Snapshot();
        fs.AddFile(path, "two"u8.ToArray());

        Assert.That(first.ContainsKey(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))), Is.True);
        Assert.That(first.Count, Is.EqualTo(1));
        Assert.That(Encoding.UTF8.GetString(first.Values.First()), Is.EqualTo("one"));
    }

    [Test]
    public void GetFileSize_returns_minus_one_for_missing()
    {
        var fs = new InMemoryFileSystem();
        Assert.That(fs.GetFileSize(Path.Combine(Root, "none")), Is.EqualTo(-1));
        fs.AddFile(Path.Combine(Root, "x"), new byte[] { 0, 0, 0 });
        Assert.That(fs.GetFileSize(Path.Combine(Root, "x")), Is.EqualTo(3));
    }
}
