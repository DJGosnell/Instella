using System.Diagnostics;
using System.Text.Json;
using Instella.Core.FileSystem;
using Instella.Core.Installation;
using Instella.Sdk.Internal;
using NUnit.Framework;

namespace Instella.Sdk.Tests;

/// <summary>
/// The post-update marker: the restarted app learns it was updated from the marker the
/// updater leaves, once per user, not from command-line flags (which cannot survive the
/// non-elevated relaunch through Explorer).
/// </summary>
[TestFixture]
[NonParallelizable]
public class PostUpdateTests
{
    private const string AppId = "com.example.quicknotes";
    private string _root = null!;
    private string _seen = null!;
    private Func<string> _savedSeen = null!;
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 14, 3, 11, TimeSpan.Zero);

    [SetUp]
    public void SetUp()
    {
        _root = Directory.CreateTempSubdirectory("instella-postupdate-").FullName;
        _seen = Directory.CreateTempSubdirectory("instella-seen-").FullName;
        _savedSeen = PostUpdateReader.SeenStoreRoot;
        PostUpdateReader.SeenStoreRoot = () => _seen;
    }

    [TearDown]
    public void TearDown()
    {
        PostUpdateReader.SeenStoreRoot = _savedSeen;
        foreach (var dir in new[] { _root, _seen })
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public void NoMarker_IsNotAPostUpdateStart() =>
        Assert.That(PostUpdateReader.Read(_root, AppId, Now), Is.Null);

    [Test]
    public async Task FreshMarker_IsReportedOnce_ThenNotAgain()
    {
        await Write(Marker(completedAt: Now.AddMinutes(-1), arguments: ["--open-last"]));

        var first = PostUpdateReader.Read(_root, AppId, Now);
        Assert.That(first, Is.Not.Null);
        Assert.That(first!.FromVersion, Is.EqualTo(new Version(1, 2, 0)));
        Assert.That(first.ToVersion, Is.EqualTo(new Version(1, 3, 0)));
        Assert.That(first.Channel, Is.EqualTo("stable"));
        Assert.That(first.Arguments, Is.EqualTo(new[] { "--open-last" }));

        Assert.That(PostUpdateReader.Read(_root, AppId, Now), Is.Null, "the next start (a new reader) has seen it");
    }

    [Test]
    public async Task AnotherUser_SeesItOnceToo()
    {
        await Write(Marker(completedAt: Now.AddHours(-3)));
        Assert.That(PostUpdateReader.Read(_root, AppId, Now), Is.Not.Null);

        var secondUser = Directory.CreateTempSubdirectory("instella-seen2-").FullName;
        PostUpdateReader.SeenStoreRoot = () => secondUser;
        try
        {
            Assert.That(PostUpdateReader.Read(_root, AppId, Now), Is.Not.Null);
            Assert.That(PostUpdateReader.Read(_root, AppId, Now), Is.Null);
        }
        finally
        {
            Directory.Delete(secondUser, recursive: true);
        }
    }

    [Test]
    public async Task MarkerOlderThan30Days_IsIgnored()
    {
        await Write(Marker(completedAt: Now.AddDays(-31)));
        Assert.That(PostUpdateReader.Read(_root, AppId, Now), Is.Null);
    }

    [Test]
    public async Task MarkerOfAnotherApp_IsIgnored()
    {
        // App ids contain dots: "com.example.quicknotes.beta" must not pass for "com.example.quicknotes".
        await Write(Marker(appId: AppId + ".beta"));
        Assert.That(PostUpdateReader.Read(_root, AppId, Now), Is.Null);
    }

    [Test]
    public async Task CorruptMarkerNextToAGoodOne_TheGoodOneWins()
    {
        await Write(Marker(completedAt: Now.AddMinutes(-5)));
        var folder = Path.Combine(_root, ".instella", "post-update");
        File.WriteAllText(Path.Combine(folder, $"{AppId}.{new string('f', 32)}.json"), "{ not json");
        File.WriteAllText(Path.Combine(folder, $"{AppId}.{new string('e', 32)}.json"), """{"markerVersion": 2}""");

        Assert.That(PostUpdateReader.Read(_root, AppId, Now)?.ToVersion, Is.EqualTo(new Version(1, 3, 0)));
    }

    [Test]
    public async Task ReadOnlySeenStore_StillReports()
    {
        await Write(Marker());
        var appFolder = Directory.CreateDirectory(Path.Combine(_seen, PostUpdateReader.SafeName(AppId))).FullName;
        // A directory where the record file should be: the write fails, the report still happens.
        Directory.CreateDirectory(Path.Combine(appFolder, "last-post-update"));

        Assert.That(PostUpdateReader.Read(_root, AppId, Now), Is.Not.Null);
    }

    [Test]
    public async Task Writer_KeepsOnlyTheNewestMarkerOfThisApp()
    {
        await Write(Marker(completedAt: Now.AddDays(-1)));
        var other = Marker(appId: "com.other");
        await Write(other);
        var newest = Marker(completedAt: Now);
        await Write(newest);

        var names = Directory.EnumerateFiles(Path.Combine(_root, ".instella", "post-update")).Select(Path.GetFileName).ToArray();
        Assert.That(names, Is.EquivalentTo(new[]
        {
            PostUpdateMarkers.FileName(AppId, newest.UpdateId), PostUpdateMarkers.FileName("com.other", other.UpdateId),
        }));
    }

    [Test]
    public async Task RunRecovery_ReturnsTheStubsExitCode()
    {
        // Any small program stands in for the stub: the SDK waits for it and returns its code.
        var program = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "where.exe")
            : "/bin/false";
        Assume.That(File.Exists(program), $"{program} not found");
        File.Copy(program, Path.Combine(_root, InstellaOwnedPaths.StubFileName));
        using var direct = Process.Start(new ProcessStartInfo(program)
        {
            ArgumentList = { "--recover", "--path", _root, "--silent" }, UseShellExecute = false,
            RedirectStandardError = true, RedirectStandardOutput = true,
        })!;
        await direct.WaitForExitAsync();

        var exit = await UpdaterLauncher.RunRecoveryAsync(_root, CancellationToken.None);

        Assert.That(exit, Is.EqualTo(direct.ExitCode));
        Assert.That(exit, Is.Not.EqualTo(0));
    }

    private PostUpdateMarker Marker(string appId = AppId, DateTimeOffset? completedAt = null, string[]? arguments = null) => new()
    {
        UpdateId = Guid.NewGuid().ToString("N"),
        AppId = appId,
        FromVersion = new Version(1, 2, 0),
        ToVersion = new Version(1, 3, 0),
        Channel = "stable",
        CompletedAt = completedAt ?? Now.AddMinutes(-1),
        Arguments = arguments ?? [],
    };

    private Task Write(PostUpdateMarker marker) =>
        PostUpdateMarkers.WriteAsync(RealFileSystem.Instance, _root, marker, CancellationToken.None);
}
