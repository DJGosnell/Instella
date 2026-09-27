using Instella.Core.FileSystem;
using NUnit.Framework;

namespace Instella.Core.Tests;

[TestFixture]
public class SafePathTests
{
    /// <summary>Malicious archive entry paths, plus the other rejected shapes.</summary>
    public static readonly string[] Unsafe =
    [
        "../x",
        "..\\x",
        "a/../../x",
        "a/./b",
        "C:\\x",
        "C:x",
        "\\\\?\\C:\\x",
        "\\\\server\\share\\x",
        "/etc/passwd",
        "\\x",
        "CON",
        "con.txt",
        "a/NUL",
        "COM1",
        "lpt9.log",
        "a:b",
        "file.txt:stream",
        "a//b",
        "trailing.",
        "trailing ",
        "",
        " ",
        "a\0b",
        "a/\tb",
    ];

    [TestCaseSource(nameof(Unsafe))]
    public void Combine_RejectsUnsafePaths(string path)
    {
        var root = Path.Combine(Path.GetTempPath(), "instella-safepath-root");
        Assert.Throws<UnsafePathException>(() => SafePath.Combine(root, path));
        Assert.That(SafePath.TryNormalizeRelative(path, out _, out var reason), Is.False);
        Assert.That(reason, Is.Not.Empty);
    }

    [TestCase("a.txt", "a.txt")]
    [TestCase("dir/sub/a.dll", "dir/sub/a.dll")]
    [TestCase("dir\\sub\\a.dll", "dir/sub/a.dll")]
    [TestCase("CONSOLE.txt", "CONSOLE.txt")]
    [TestCase("com10", "com10")]
    [TestCase("..hidden", "..hidden")]
    [TestCase("My App/App Data.bin", "My App/App Data.bin")]
    public void TryNormalizeRelative_AcceptsAndCanonicalises(string path, string expected)
    {
        Assert.That(SafePath.TryNormalizeRelative(path, out var normalized, out _), Is.True);
        Assert.That(normalized, Is.EqualTo(expected));
    }

    [Test]
    public void Combine_ResolvesInsideRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "instella-safepath-root");
        var combined = SafePath.Combine(root, "dir/file.txt");
        Assert.That(combined, Is.EqualTo(Path.Combine(Path.GetFullPath(root), "dir", "file.txt")));
    }

    [Test]
    public void Combine_SiblingWithSharedPrefixIsNotInside()
    {
        // "root-evil" starts with "root" as a string; the separator check must still hold.
        var root = Path.Combine(Path.GetTempPath(), "root");
        Assert.That(SafePath.Combine(root, "x"), Does.StartWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar));
    }
}
