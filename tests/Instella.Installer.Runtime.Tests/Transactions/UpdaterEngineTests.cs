using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Instella.Core.Diff;
using Instella.Core.FileSystem;
using Instella.Core.Installation;
using Instella.Core.Platform;
using Instella.Core.Trust;
using Instella.Core.Update;
using Instella.Core.Wire;
using Instella.Installer.Runtime.Core;
using Instella.Installer.Runtime.Core.Processes;
using Instella.Installer.Runtime.Core.Update;
using Instella.Installer.Testing;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Transactions;

[TestFixture]
public partial class UpdaterEngineTests
{
    private static readonly TimeSpan[] NoDelays = [TimeSpan.Zero];
    private static readonly Version V1 = new(1, 0, 0);
    private static readonly Version V2 = new(2, 0, 0);
    private const string AppId = "com.test.app";

    private ECDsa _keyA = null!;
    private ECDsa _keyB = null!;
    private string _root = null!;
    private InMemoryFileSystem _fs = null!;

    [SetUp]
    public void SetUp()
    {
        _keyA = ReleaseKeys.Generate();
        _keyB = ReleaseKeys.Generate();
        _root = Path.Combine(Path.GetTempPath(), "instella-upd-tests", Guid.NewGuid().ToString("N"));
        _fs = new InMemoryFileSystem();
    }

    [TearDown]
    public void TearDown()
    {
        _keyA.Dispose();
        _keyB.Dispose();
    }

    [Test]
    public async Task Update_DownloadsChangedAndNew_DeletesRemoved_SkipsUnchanged()
    {
        Install(new() { ["App.exe"] = B("app-v1"), ["old.dll"] = B("old"), ["same.dll"] = B("same") });
        var server = new FakeDownloader(Release(V2, new() { ["App.exe"] = B("app-v2"), ["same.dll"] = B("same"), ["lib/new.dll"] = B("new") }));

        var result = await Run(server);

        Assert.That(result.Success, Is.True, result.Error);
        Assert.That(Tree().Keys, Is.EquivalentTo(new[] { "App.exe", "same.dll", "lib/new.dll", "instella.exe", InstellaOwnedPaths.InstalledManifest }));
        Assert.That(Text("App.exe"), Is.EqualTo("app-v2"));
        Assert.That(server.Downloaded, Is.EquivalentTo(new[] { "App.exe", "lib/new.dll" }), "unchanged files are not downloaded");

        var manifest = await ReadManifest();
        Assert.That(manifest.Version, Is.EqualTo(V2));
        Assert.That(manifest.Files.Select(f => f.RelativePath), Is.EquivalentTo(new[] { "App.exe", "same.dll", "lib/new.dll" }));
        Assert.That(_fs.EnumerateFiles(Path.Combine(_root, ".instella"), "*", true)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}post-update{Path.DirectorySeparatorChar}")), Is.Empty,
            "no transaction leftovers; only the post-update marker");
    }

    [Test]
    public async Task Update_KeepsTheDownloadToken()
    {
        // The token compiled into the installer serves every later update.
        var token = "idt_" + new string('C', 43);
        Install(new() { ["App.exe"] = B("app-v1") }, downloadToken: token);
        var server = new FakeDownloader(Release(V2, new() { ["App.exe"] = B("app-v2") }));

        var result = await Run(server);

        Assert.That(result.Success, Is.True, result.Error);
        Assert.That((await ReadManifest()).DownloadToken, Is.EqualTo(token));
    }

    [Test]
    public async Task Update_KeepsInstalledManifestFieldsItDoesNotKnow()
    {
        // A stub is never replaced, so a 1.0 updater rewrites manifests that later 1.x installers
        // wrote: it must not drop their fields (docs/compatibility.md, "Rules for 1.x").
        Install(new() { ["App.exe"] = B("app-v1") });
        var path = Path.Combine(_root, InstellaOwnedPaths.InstalledManifest);
        var node = System.Text.Json.Nodes.JsonNode.Parse(_fs.Snapshot()[Path.GetFullPath(path)])!.AsObject();
        node["futureField"] = System.Text.Json.Nodes.JsonNode.Parse("""{"a": 1}""");
        _fs.AddFile(path, Encoding.UTF8.GetBytes(node.ToJsonString()));
        var server = new FakeDownloader(Release(V2, new() { ["App.exe"] = B("app-v2") }));

        var result = await Run(server);

        Assert.That(result.Success, Is.True, result.Error);
        var after = System.Text.Json.Nodes.JsonNode.Parse(_fs.Snapshot()[Path.GetFullPath(path)])!.AsObject();
        Assert.That(after["version"]!.GetValue<string>(), Is.EqualTo("2.0.0"));
        Assert.That(after["futureField"]?.ToJsonString(), Is.EqualTo("""{"a":1}"""));
    }

    // ---- a bad patch manifest falls back to a full download ----

    [Test]
    public async Task PatchManifestInvalidJson_FallsBackToFullDownload()
    {
        Install(new() { ["App.exe"] = B("app-v1") });
        var server = new FakeDownloader(Release(V2, new() { ["App.exe"] = B("app-v2") }))
        {
            PatchManifestError = new JsonException("'<' is an invalid start of a value"),
        };
        server.AddPatch("App.exe", B("app-v1"), B("app-v2"));

        var result = await Run(server, usePatch: true);

        Assert.That(result.Success, Is.True, result.Error);
        Assert.That(Text("App.exe"), Is.EqualTo("app-v2"));
        Assert.That(server.Downloaded, Is.EqualTo(new[] { "App.exe" }));
    }

    [Test]
    public async Task PatchManifestFutureFormat_FallsBack()
    {
        // A patch recipe in a format this updater can't read is ignored, not guessed at.
        Install(new() { ["App.exe"] = B("app-v1") });
        var server = new FakeDownloader(Release(V2, new() { ["App.exe"] = B("app-v2") })) { PatchFormatVersion = 2 };
        server.AddPatch("App.exe", B("app-v1"), B("app-v2"));

        var result = await Run(server, usePatch: true);

        Assert.That(result.Success, Is.True, result.Error);
        Assert.That(Text("App.exe"), Is.EqualTo("app-v2"));
        Assert.That(server.Downloaded, Is.EqualTo(new[] { "App.exe" }), "downloaded in full");
    }

    [Test]
    public async Task PatchManifestCurrentFormat_IsUsed()
    {
        Install(new() { ["App.exe"] = B("app-v1") });
        var server = new FakeDownloader(Release(V2, new() { ["App.exe"] = B("app-v2") }));
        server.AddPatch("App.exe", B("app-v1"), B("app-v2"));

        var result = await Run(server, usePatch: true);

        Assert.That(result.Success, Is.True, result.Error);
        Assert.That(server.Downloaded, Is.Empty, "patched, not downloaded");
    }

    // ---- post-update marker ----

    [Test]
    public async Task Update_LeavesExactlyOneMarker_WithTheUpdatesDetails()
    {
        Install(new() { ["App.exe"] = B("app-v1") });
        var server = new FakeDownloader(Release(V2, new() { ["App.exe"] = B("app-v2") }));

        var result = await new UpdaterEngine(Args(V1, V2, false, false) with { ExtraArgs = ["--open-last"] }, server,
            new FakePlatformServices(), _fs, BsDiffEngine.Instance, NoDelays).RunAsync(CancellationToken.None);

        Assert.That(result.Success, Is.True, result.Error);
        var marker = Markers().Single();
        Assert.That(marker.AppId, Is.EqualTo(AppId));
        Assert.That(marker.FromVersion, Is.EqualTo(V1));
        Assert.That(marker.ToVersion, Is.EqualTo(V2));
        Assert.That(marker.Channel, Is.EqualTo("stable"));
        Assert.That(marker.Arguments, Is.EqualTo(new[] { "--open-last" }));
        Assert.That(marker.UpdateId, Has.Length.EqualTo(32));
        Assert.That(marker.CompletedAt, Is.EqualTo(DateTimeOffset.UtcNow).Within(TimeSpan.FromMinutes(1)));
    }

    [Test]
    public async Task SecondUpdate_ReplacesTheMarker()
    {
        Install(new() { ["App.exe"] = B("app-v1") });
        Assert.That((await Run(new FakeDownloader(Release(V2, new() { ["App.exe"] = B("app-v2") })))).Success, Is.True);
        var v3 = new Version(3, 0, 0);

        var result = await Run(new FakeDownloader(Release(v3, new() { ["App.exe"] = B("app-v3") })), from: V2, to: v3);

        Assert.That(result.Success, Is.True, result.Error);
        Assert.That(Markers().Single().ToVersion, Is.EqualTo(v3));
    }

    [Test]
    public async Task RepairAndFailedUpdate_WriteNoMarker()
    {
        Install(new() { ["App.exe"] = B("app-v1") });
        var repair = await Run(new FakeDownloader(Release(V1, new() { ["App.exe"] = B("app-v1") })), to: V1, repair: true);
        var failed = await Run(new FakeDownloader(Release(V2, new() { ["App.exe"] = B("app-v2") })) { Override = { ["App.exe"] = B("evil") } });

        Assert.That(repair.Success, Is.True, repair.Error);
        Assert.That(failed.Success, Is.False);
        Assert.That(Markers(), Is.Empty);
    }

    [Test]
    public async Task UnwritableMarkerFolder_StillSucceeds_WithAWarning()
    {
        Install(new() { ["App.exe"] = B("app-v1") });
        var fs = new FailWritesUnder(_fs, Path.Combine(_root, ".instella", "post-update"));
        var engine = new UpdaterEngine(Args(V1, V2, false, false), new FakeDownloader(Release(V2, new() { ["App.exe"] = B("app-v2") })),
            new FakePlatformServices(), fs, BsDiffEngine.Instance, NoDelays);
        var log = new List<string>();
        engine.LogMessage += (_, m) => log.Add(m);

        var result = await engine.RunAsync(CancellationToken.None);

        Assert.That(result.Success, Is.True, result.Error);
        Assert.That(Text("App.exe"), Is.EqualTo("app-v2"));
        Assert.That(log, Has.Some.Contains("could not write the post-update marker"));
    }

    [Test]
    public async Task Update_FromAnotherChannel_RecordsThatChannel()
    {
        Install(new() { ["App.exe"] = B("app-v1") });
        var server = new FakeDownloader(Release(V2, new() { ["App.exe"] = B("app-v2") }, channel: "beta"));

        var result = await new UpdaterEngine(Args(V1, V2, false, false) with { Channel = "beta" }, server,
            new FakePlatformServices(), _fs, BsDiffEngine.Instance, NoDelays).RunAsync(CancellationToken.None);

        Assert.That(result.Success, Is.True, result.Error);
        Assert.That((await ReadManifest()).Channel, Is.EqualTo("beta"));
        Assert.That(Markers().Single().Channel, Is.EqualTo("beta"));
    }

    [Test]
    public async Task Update_NeverStartsTheAppItself_AndReportsItsPath()
    {
        Install(new() { ["App.exe"] = B("app-v1") });
        var result = await new UpdaterEngine(Args(V1, V2, false, false) with { Restart = true },
            new FakeDownloader(Release(V2, new() { ["App.exe"] = B("app-v2") })),
            new FakePlatformServices(), _fs, BsDiffEngine.Instance, NoDelays).RunAsync(CancellationToken.None);

        // The caller restarts it through IAppLauncher (never elevated); the engine has no process code for it.
        Assert.That(result.ExecutablePath, Is.EqualTo(SafePath.Combine(_root, "App.exe")));
    }

    private List<PostUpdateMarker> Markers()
    {
        var folder = Path.Combine(_root, ".instella", "post-update");
        return _fs.DirectoryExists(folder)
            ? _fs.EnumerateFiles(folder).Select(f => PostUpdateMarkers.TryParse(_fs.Snapshot()[Path.GetFullPath(f)])!).ToList()
            : [];
    }

    [Test]
    public async Task TamperedDownload_FailsBeforeCommit_AndLeavesInstallUntouched()
    {
        Install(new() { ["App.exe"] = B("app-v1") });
        var server = new FakeDownloader(Release(V2, new() { ["App.exe"] = B("app-v2") }))
        {
            Override = { ["App.exe"] = B("evil") },
        };

        var result = await Run(server);

        Assert.That(result.ExitCode, Is.EqualTo(InstellaExitCode.UpdateGeneralFailure));
        Assert.That(Text("App.exe"), Is.EqualTo("app-v1"));
        Assert.That((await ReadManifest()).Version, Is.EqualTo(V1));
    }

    [Test]
    public async Task ReplayOfOlderSignedRelease_IsRejected()
    {
        Install(new() { ["App.exe"] = B("app-v1") });
        var server = new FakeDownloader(Release(V1, new() { ["App.exe"] = B("app-v1-evil") }));

        var result = await Run(server, to: V1);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Does.Contain("not newer"));
        Assert.That(Text("App.exe"), Is.EqualTo("app-v1"));
    }

    [Test]
    public async Task ReleaseSignedByUntrustedKey_IsRejected()
    {
        Install(new() { ["App.exe"] = B("app-v1") });
        var server = new FakeDownloader(Release(V2, new() { ["App.exe"] = B("app-v2") }, signer: _keyB));

        var result = await Run(server);

        Assert.That(result.Error, Does.Contain("unknown key"));
        Assert.That(Text("App.exe"), Is.EqualTo("app-v1"));
    }

    [Test]
    public async Task ReleaseOnAnotherChannel_IsRejected()
    {
        Install(new() { ["App.exe"] = B("app-v1") });
        var server = new FakeDownloader(Release(V2, new() { ["App.exe"] = B("app-v2-beta") }, channel: "beta"));

        var result = await Run(server); // UpdaterArgs.Channel defaults to stable

        Assert.That(result.Error, Does.Contain("channel mismatch"));
        Assert.That(Text("App.exe"), Is.EqualTo("app-v1"));
    }

    [Test]
    public async Task StaleLaunch_ExitsWithoutTouchingAnything()
    {
        Install(new() { ["App.exe"] = B("app-v1") });
        var server = new FakeDownloader(Release(V2, new() { ["App.exe"] = B("app-v2") }));

        var result = await Run(server, from: new Version(0, 9));

        Assert.That(result.ExitCode, Is.EqualTo(InstellaExitCode.UpdateGeneralFailure));
        Assert.That(result.Error, Does.Contain("installation changed"));
        Assert.That(server.ReleaseRequests, Is.Zero);
    }

    [Test]
    public async Task KeyRotation_IsRecordedInInstalledManifest()
    {
        Install(new() { ["App.exe"] = B("app-v1") });
        var pubA = ReleaseKeys.PublicKeyOf(_keyA);
        var pubB = ReleaseKeys.PublicKeyOf(_keyB);
        var server = new FakeDownloader(Release(V2, new() { ["App.exe"] = B("app-v2") }, rotateTo: [pubA, pubB]));

        Assert.That((await Run(server)).Success, Is.True);

        var manifest = await ReadManifest();
        Assert.That(manifest.TrustedKeys!.Select(k => k.KeyId), Is.EquivalentTo(new[] { pubA.KeyId, pubB.KeyId }));
    }

    [Test]
    public async Task Patch_IsAppliedAndVerified_AndServerDeletedFilesAreIgnored()
    {
        var v1 = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("hello world ", 200)));
        var v2 = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("hello there ", 200)));
        Install(new() { ["App.exe"] = v1, ["keep.dll"] = B("keep") });
        var server = new FakeDownloader(Release(V2, new() { ["App.exe"] = v2, ["keep.dll"] = B("keep") }));
        server.AddPatch("App.exe", v1, v2);
        server.ServerDeletedFiles.Add("keep.dll"); // a lying server must not make us delete it

        var result = await Run(server, usePatch: true);

        Assert.That(result.Success, Is.True, result.Error);
        Assert.That(Tree()["App.exe"], Is.EqualTo(v2));
        Assert.That(Tree().ContainsKey("keep.dll"), Is.True);
        Assert.That(server.Downloaded, Is.Empty, "the patch replaced the full download");
    }

    [Test]
    public async Task BadPatch_FallsBackToFullDownload()
    {
        var v1 = B("one");
        Install(new() { ["App.exe"] = v1 });
        var server = new FakeDownloader(Release(V2, new() { ["App.exe"] = B("two") }));
        server.AddPatch("App.exe", v1, B("something else entirely"));

        var result = await Run(server, usePatch: true);

        Assert.That(result.Success, Is.True, result.Error);
        Assert.That(Text("App.exe"), Is.EqualTo("two"));
        Assert.That(server.Downloaded, Is.EquivalentTo(new[] { "App.exe" }));
    }

    [Test]
    public async Task Repair_RestoresDeletedAndCorruptedFiles()
    {
        Install(new() { ["App.exe"] = B("app-v1"), ["lib.dll"] = B("lib") });
        await _fs.DeleteFileAsync(Path.Combine(_root, "lib.dll"), CancellationToken.None);
        await _fs.WriteAllBytesAsync(Path.Combine(_root, "App.exe"), B("corrupted"), CancellationToken.None);
        var server = new FakeDownloader(Release(V1, new() { ["App.exe"] = B("app-v1"), ["lib.dll"] = B("lib") }));

        var result = await Run(server, to: V1, repair: true);

        Assert.That(result.Success, Is.True, result.Error);
        Assert.That(Text("App.exe"), Is.EqualTo("app-v1"));
        Assert.That(Text("lib.dll"), Is.EqualTo("lib"));
    }

    [Test]
    public async Task CommitFailure_RollsBack_ExitCode22()
    {
        Install(new() { ["App.exe"] = B("app-v1"), ["b.dll"] = B("b1") });
        var server = new FakeDownloader(Release(V2, new() { ["App.exe"] = B("app-v2"), ["b.dll"] = B("b2") }));
        var failing = new FailMoveIntoFileSystem(_fs, Path.Combine(_root, "b.dll"));

        var engine = new UpdaterEngine(Args(V1, V2, usePatch: false, repair: false), server, new FakePlatformServices(),
            failing, BsDiffEngine.Instance, NoDelays);
        var result = await engine.RunAsync(CancellationToken.None);

        Assert.That(result.ExitCode, Is.EqualTo(InstellaExitCode.UpdateRolledBack));
        Assert.That(Text("App.exe"), Is.EqualTo("app-v1"));
        Assert.That(Text("b.dll"), Is.EqualTo("b1"));
        Assert.That((await ReadManifest()).Version, Is.EqualTo(V1));
    }

    [Test]
    public async Task UnsignedInstall_WithoutAllowUnsigned_IsRefused()
    {
        Install(new() { ["App.exe"] = B("app-v1") }, trusted: false, allowUnsigned: false);
        var server = new FakeDownloader(Release(V2, new() { ["App.exe"] = B("app-v2") }));

        var result = await Run(server);

        Assert.That(result.Error, Does.Contain("trusts no publisher keys"));
    }

    [Test]
    public async Task UnsignedInstall_WithAllowUnsigned_UpdatesFromFullBuild()
    {
        Install(new() { ["App.exe"] = B("app-v1"), ["gone.dll"] = B("x") }, trusted: false, allowUnsigned: true);
        var server = new FakeDownloader(null) { FullBuild = Zip(new() { ["App.exe"] = B("app-v2") }) };

        var result = await Run(server);

        Assert.That(result.Success, Is.True, result.Error);
        Assert.That(Text("App.exe"), Is.EqualTo("app-v2"));
        Assert.That(Tree().ContainsKey("gone.dll"), Is.False);
    }

    [TestCase(true, RegistryHive.CurrentUser)]
    [TestCase(false, RegistryHive.LocalMachine)]
    public async Task Update_SetsTheInstalledAppsEntryToTheNewVersion(bool perUser, RegistryHive hive)
    {
        // Hand test: after an in-app update to 1.4.0, Settings > Apps still listed 1.2.0.
        Install(new() { ["App.exe"] = B("app-v1") }, uninstallEntry: true, perUser: perUser);
        var platform = new FakePlatformServices();
        var key = $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{AppId}";
        platform.Registry.Set(hive, key, "DisplayVersion", InstellaRegistryValueKind.String, "1.0.0");
        var server = new FakeDownloader(Release(V2, new() { ["App.exe"] = new byte[4096] }));

        var result = await new UpdaterEngine(Args(V1, V2, false, false), server, platform, _fs, BsDiffEngine.Instance, NoDelays)
            .RunAsync(CancellationToken.None);

        Assert.That(result.Success, Is.True, result.Error);
        Assert.That(platform.Registry.Get(hive, key, "DisplayVersion"), Is.EqualTo("2.0.0"));
        Assert.That(platform.Registry.Get(hive, key, "EstimatedSize"), Is.EqualTo(4));
    }

    [Test]
    public async Task Update_WithoutAnInstalledAppsEntry_DoesNotCreateOne()
    {
        Install(new() { ["App.exe"] = B("app-v1") });
        var platform = new FakePlatformServices();
        var server = new FakeDownloader(Release(V2, new() { ["App.exe"] = B("app-v2") }));

        var result = await new UpdaterEngine(Args(V1, V2, false, false), server, platform, _fs, BsDiffEngine.Instance, NoDelays)
            .RunAsync(CancellationToken.None);

        Assert.That(result.Success, Is.True, result.Error);
        Assert.That(platform.Registry.Snapshot(), Is.Empty);
    }

    // ---- Interactive update window ----

    [Test]
    public async Task UpdateWindow_OnSuccess_CountsDownThenRestarts()
    {
        Install(new() { ["App.exe"] = B("app-v1") });
        var server = new FakeDownloader(Release(V2, new() { ["App.exe"] = B("app-v2") }));
        WindowHost? host = null;
        var restarts = new List<UpdateResult>();

        var result = await Instella.Installer.Runtime.Runners.UpdateWindow.RunAsync(
            Engine(server), "App", V2, repair: false, (t, pages, states) => host = new WindowHost(states[0], pages[0]) { Action = HostAction.Wait },
            CancellationToken.None, restart: true, countdown: TimeSpan.FromMilliseconds(200), onRestart: restarts.Add);

        Assert.That(result.Success, Is.True, result.Error);
        Assert.That(host!.Closed, Is.True, "the countdown closes the window");
        Assert.That(host.Locked, Is.True, "Cancel is disabled once the commit starts");
        Assert.That(host.Statuses, Has.Some.StartsWith("Restarting in"));
        Assert.That(host.Label, Is.EqualTo("&Restart now"));
        Assert.That(restarts.Single().ExecutablePath, Is.EqualTo(SafePath.Combine(_root, "App.exe")));
    }

    [Test]
    public async Task UpdateWindow_RestartNow_RestartsImmediately()
    {
        Install(new() { ["App.exe"] = B("app-v1") });
        var server = new FakeDownloader(Release(V2, new() { ["App.exe"] = B("app-v2") }));
        var restarts = 0;
        var clock = Stopwatch.StartNew();

        var result = await Instella.Installer.Runtime.Runners.UpdateWindow.RunAsync(
            Engine(server), "App", V2, repair: false, (t, pages, states) => new WindowHost(states[0], pages[0]) { Action = HostAction.PressContinue },
            CancellationToken.None, restart: true, countdown: TimeSpan.FromSeconds(30), onRestart: _ => restarts++);

        Assert.That(result.Success, Is.True, result.Error);
        Assert.That(restarts, Is.EqualTo(1));
        Assert.That(clock.Elapsed, Is.LessThan(TimeSpan.FromSeconds(20)), "no waiting for the countdown");
    }

    [Test]
    public async Task UpdateWindow_RestartFalse_ShowsCloseAndDoesNotLaunch()
    {
        Install(new() { ["App.exe"] = B("app-v1") });
        var server = new FakeDownloader(Release(V2, new() { ["App.exe"] = B("app-v2") }));
        WindowHost? host = null;
        var restarts = 0;

        var result = await Instella.Installer.Runtime.Runners.UpdateWindow.RunAsync(
            Engine(server), "App", V2, repair: false, (t, pages, states) => host = new WindowHost(states[0], pages[0]),
            CancellationToken.None, restart: false, countdown: TimeSpan.FromSeconds(5), onRestart: _ => restarts++);

        Assert.That(result.Success, Is.True, result.Error);
        Assert.That(host!.Closed, Is.False, "the result stays on screen");
        Assert.That(host.State.Bool(Instella.Installer.Runtime.Runners.UpdateWindow.Succeeded), Is.True);
        Assert.That(host.Label, Is.EqualTo("&Close"));
        Assert.That(restarts, Is.Zero);
    }

    [Test]
    public async Task UpdateWindow_CountdownZero_ClosesAtOnce()
    {
        Install(new() { ["App.exe"] = B("app-v1") });
        var server = new FakeDownloader(Release(V2, new() { ["App.exe"] = B("app-v2") }));
        WindowHost? host = null;
        var restarts = 0;

        var result = await Instella.Installer.Runtime.Runners.UpdateWindow.RunAsync(
            Engine(server), "App", V2, repair: false, (t, pages, states) => host = new WindowHost(states[0], pages[0]) { Action = HostAction.Wait },
            CancellationToken.None, restart: true, countdown: TimeSpan.Zero, onRestart: _ => restarts++);

        Assert.That(result.Success, Is.True, result.Error);
        Assert.That(host!.Closed, Is.True);
        Assert.That(host.State.Bool(Instella.Installer.Runtime.Runners.UpdateWindow.Succeeded), Is.False, "no success page is shown");
        Assert.That(restarts, Is.EqualTo(1));
    }

    [Test]
    public async Task UpdateWindow_CloseButton_Restarts()
    {
        Install(new() { ["App.exe"] = B("app-v1") });
        var server = new FakeDownloader(Release(V2, new() { ["App.exe"] = B("app-v2") }));
        WindowHost? host = null;
        var restarts = 0;

        var result = await Instella.Installer.Runtime.Runners.UpdateWindow.RunAsync(
            Engine(server), "App", V2, repair: false, (t, pages, states) => host = new WindowHost(states[0], pages[0]) { Action = HostAction.PressClose },
            CancellationToken.None, restart: true, countdown: TimeSpan.FromSeconds(30), onRestart: _ => restarts++);

        Assert.That(result.Success, Is.True, result.Error);
        Assert.That(host!.Closed, Is.True, "closing is not intercepted once the result is shown");
        Assert.That(restarts, Is.EqualTo(1));
    }

    [Test]
    public async Task UpdateWindow_StaysOpenWithTheErrorOnFailure()
    {
        Install(new() { ["App.exe"] = B("app-v1") });
        var server = new FakeDownloader(Release(V2, new() { ["App.exe"] = B("app-v2") })) { Override = { ["App.exe"] = B("evil") } };
        WindowHost? host = null;
        var restarts = 0;

        var result = await Instella.Installer.Runtime.Runners.UpdateWindow.RunAsync(
            Engine(server), "App", V2, repair: false, (t, pages, states) => host = new WindowHost(states[0], pages[0]), CancellationToken.None,
            restart: true, countdown: TimeSpan.FromSeconds(5), onRestart: _ => restarts++);

        Assert.That(result.Success, Is.False);
        Assert.That(host!.Closed, Is.False);
        Assert.That(host.State.Bool(Instella.Installer.Runtime.Runners.InteractivePageStateKeys.CanFinish), Is.True);
        Assert.That(host.State.Text(Instella.Installer.Runtime.Runners.InteractivePageStateKeys.Status), Does.StartWith("Update failed"));
        Assert.That(restarts, Is.Zero);
    }

    private UpdaterEngine Engine(FakeDownloader server) =>
        new(Args(V1, V2, usePatch: false, repair: false), server, new FakePlatformServices(), _fs, BsDiffEngine.Instance, NoDelays);

    private enum HostAction { StopWhenFinishable, Wait, PressContinue, PressClose }

    /// <summary>Drains posted UI work; <see cref="Action"/> says what the "user" does once the result shows.</summary>
    private sealed class WindowHost(Instella.Installer.Runtime.UI.Widgets.PageState state, Instella.Installer.Runtime.Builders.PageSpec page)
        : Instella.Installer.Runtime.Runners.IInteractiveHost
    {
        private readonly Queue<Action> _posted = new();
        public Instella.Installer.Runtime.UI.Widgets.PageState State => state;
        public HostAction Action { get; init; } = HostAction.StopWhenFinishable;
        public List<string> Statuses { get; } = [];
        public string? Label => page.ContinueLabelFor?.Invoke(state) ?? page.ContinueLabel;
        public bool Closed;
        public bool Locked;
        public event Action<int>? PageChanged { add { } remove { } }
        public void PostToUiThread(Action action) { lock (_posted) _posted.Enqueue(action); }
        public void LockNavigation() => Locked = true;
        public void LockBackNavigation() { }
        public Func<bool>? CancelInterceptor { get; set; }
        public Func<int, string?>? BeforeLeave { get; set; }
        public void Close() => Closed = true;

        public Instella.Installer.Runtime.Runners.InteractiveHostOutcome Run()
        {
            const string finishable = Instella.Installer.Runtime.Runners.InteractivePageStateKeys.CanFinish;
            for (var i = 0; i < 2000 && !Closed; i++)
            {
                if (state.Bool(finishable))
                {
                    switch (Action)
                    {
                        case HostAction.StopWhenFinishable:
                            return Instella.Installer.Runtime.Runners.InteractiveHostOutcome.Cancelled;
                        case HostAction.PressContinue:
                            return Instella.Installer.Runtime.Runners.InteractiveHostOutcome.Completed;
                        case HostAction.PressClose:
                            if (CancelInterceptor?.Invoke() != true) Closed = true;
                            return Instella.Installer.Runtime.Runners.InteractiveHostOutcome.Cancelled;
                    }
                }
                Action? next = null;
                lock (_posted) if (_posted.Count > 0) next = _posted.Dequeue();
                if (next is null) Thread.Sleep(5);
                else
                {
                    next();
                    var status = state.Text(Instella.Installer.Runtime.Runners.InteractivePageStateKeys.Status);
                    if (!string.IsNullOrEmpty(status)) Statuses.Add(status);
                }
            }
            return Instella.Installer.Runtime.Runners.InteractiveHostOutcome.Cancelled;
        }

        public void Dispose() { }
    }

    [Test, Platform("Win"), SupportedOSPlatform("windows")]
    public async Task Update_ClosesAnyProgramUsingTheAppsFiles_NotOnlyTheAppExe()
    {
        Install(new() { ["App.exe"] = B("app-v1") });
        var server = new FakeDownloader(Release(V2, new() { ["App.exe"] = B("app-v2") }));
        using var helper = StartIdleProcess();

        var engine = new UpdaterEngine(Args(V1, V2, false, false) with { AllowForceClose = true }, server,
            new Instella.Core.Platform.Windows.WindowsPlatformServices(), _fs, BsDiffEngine.Instance, NoDelays)
        {
            ProcessFinder = new LiveFinder(helper),
        };
        var result = await engine.RunAsync(CancellationToken.None);

        Assert.That(result.Success, Is.True, result.Error);
        Assert.That(helper.WaitForExit(5000), Is.True, "a program not named like the app, but using its files, was closed");
    }

    [Test, Platform("Win"), SupportedOSPlatform("windows")]
    public async Task Update_Silent_ProgramThatWillNotClose_FailsWithoutForceClose()
    {
        Install(new() { ["App.exe"] = B("app-v1") });
        var server = new FakeDownloader(Release(V2, new() { ["App.exe"] = B("app-v2") }));
        using var helper = StartIdleProcess();

        var engine = new UpdaterEngine(Args(V1, V2, false, false), server,
            new Instella.Core.Platform.Windows.WindowsPlatformServices(), _fs, BsDiffEngine.Instance, NoDelays)
        {
            ProcessFinder = new LiveFinder(helper),
        };
        var result = await engine.RunAsync(CancellationToken.None);
        var stillRunning = !helper.HasExited;
        helper.Kill();

        Assert.That(result.ExitCode, Is.EqualTo(InstellaExitCode.UpdateAppCouldNotClose));
        Assert.That(stillRunning, Is.True, "without --allow-force-close nothing is ended");
        Assert.That(Text("App.exe"), Is.EqualTo("app-v1"));
    }

    /// <summary>A windowless process that runs until it is ended (<c>cmd /c pause</c> on a redirected stdin).</summary>
    private Process StartIdleProcess()
    {
        Directory.CreateDirectory(_root);   // the finder is only asked about folders that exist on disk
        var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add("pause");
        return Process.Start(psi)!;
    }

    /// <summary>Reports <paramref name="process"/> as using the app's files while it runs.</summary>
    private sealed class LiveFinder(Process process) : ILockingProcessFinder
    {
        public IReadOnlyList<LockingProcess> Find(IReadOnlyList<string> files) =>
            process.HasExited ? [] : [new LockingProcess(process.Id, "helper", CanClose: true)];
    }

    // ---- helpers ----

    private Task<UpdateResult> Run(FakeDownloader server, Version? from = null, Version? to = null, bool usePatch = false, bool repair = false)
    {
        var engine = new UpdaterEngine(Args(from ?? V1, to ?? V2, usePatch, repair), server, new FakePlatformServices(),
            _fs, BsDiffEngine.Instance, NoDelays);
        return engine.RunAsync(CancellationToken.None);
    }

    private UpdaterArgs Args(Version from, Version to, bool usePatch, bool repair) => new()
    {
        AppPath = _root,
        AppExecutable = "App.exe",
        FromVersion = from,
        ToVersion = to,
        UsePatch = usePatch,
        Repair = repair,
        Restart = false,
        GracefulTimeout = TimeSpan.Zero,
    };

    private void Install(Dictionary<string, byte[]> files, bool trusted = true, bool allowUnsigned = false,
        bool uninstallEntry = false, bool perUser = true, string? downloadToken = null)
    {
        foreach (var (path, bytes) in files)
            _fs.AddFile(SafePath.Combine(_root, path), bytes);
        _fs.AddFile(Path.Combine(_root, "instella.exe"), B("stub"));

        var manifest = new InstalledManifest
        {
            AppName = "App",
            AppId = AppId,
            Version = V1,
            InstallDirectory = _root,
            ExecutableName = "App.exe",
            InstalledAt = DateTime.UtcNow,
            Platform = TargetPlatform.Windows,
            Architecture = Architecture.X64,
            TrustedKeys = trusted ? [ReleaseKeys.PublicKeyOf(_keyA)] : null,
            AllowUnsignedUpdates = allowUnsigned,
            HasUninstallEntry = uninstallEntry,
            InstalledPerUser = perUser,
            DownloadToken = downloadToken,
            Files = files.Select(kv => new InstalledFile(kv.Key, Sha(kv.Value), kv.Value.Length)).ToList(),
        };
        _fs.AddFile(Path.Combine(_root, InstellaOwnedPaths.InstalledManifest),
            JsonSerializer.SerializeToUtf8Bytes(manifest, InstalledManifestJsonContext.Default.InstalledManifest));
    }

    private (SignedRelease Signed, Dictionary<string, byte[]> Files) Release(
        Version version, Dictionary<string, byte[]> files, ECDsa? signer = null, IReadOnlyList<PublisherKey>? rotateTo = null,
        string channel = "stable")
    {
        var manifest = new ReleaseManifest
        {
            FormatVersion = ReleaseManifest.CurrentFormatVersion,
            AppId = AppId,
            Version = version,
            Os = PlatformStrings.Os(TargetPlatform.Windows),
            Arch = PlatformStrings.Arch(Architecture.X64),
            Channel = channel,
            CreatedAt = DateTimeOffset.UtcNow,
            Files = files.Select(kv => new ReleaseFile(kv.Key, kv.Value.Length, Sha(kv.Value))).ToList(),
            TrustedKeys = rotateTo,
        };
        return (ReleaseSigner.Sign(manifest, signer ?? _keyA), files);
    }

    private Dictionary<string, byte[]> Tree()
    {
        var prefix = Path.GetFullPath(_root) + Path.DirectorySeparatorChar;
        return _fs.Snapshot()
            .Where(kv => kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(kv => (Rel: kv.Key[prefix.Length..].Replace(Path.DirectorySeparatorChar, '/'), kv.Value))
            .Where(x => !x.Rel.StartsWith(".instella/", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(x => x.Rel, x => x.Value, StringComparer.OrdinalIgnoreCase);
    }

    private string Text(string rel) => Encoding.UTF8.GetString(Tree()[rel]);

    private async Task<InstalledManifest> ReadManifest() =>
        (await new InstallManifestWriter(_fs).ReadAsync(_root, CancellationToken.None))!;

    private static byte[] Zip(Dictionary<string, byte[]> files)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, bytes) in files)
            {
                using var s = zip.CreateEntry(path).Open();
                s.Write(bytes);
            }
        }
        return ms.ToArray();
    }

    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    private static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    private sealed class FakeDownloader : IUpdateDownloader
    {
        private readonly SignedRelease? _release;
        private readonly Dictionary<string, byte[]> _files;
        private readonly Dictionary<string, byte[]> _patches = new();
        private readonly List<PatchedFile> _patched = [];

        public FakeDownloader((SignedRelease Signed, Dictionary<string, byte[]> Files)? release)
        {
            _release = release?.Signed;
            _files = release?.Files ?? [];
        }

        public Dictionary<string, byte[]> Override { get; } = new();
        public List<string> ServerDeletedFiles { get; } = [];
        public List<string> Downloaded { get; } = [];
        public int ReleaseRequests { get; private set; }
        public byte[]? FullBuild { get; init; }

        public void AddPatch(string path, byte[] from, byte[] to)
        {
            var patch = new MemoryStream();
            BsDiffEngine.Instance.CreatePatchAsync(new MemoryStream(from), new MemoryStream(to), patch).GetAwaiter().GetResult();
            var bytes = patch.ToArray();
            var sha = Sha(bytes);
            _patches[sha] = bytes;
            _patched.Add(new PatchedFile(path, sha, bytes.Length, Sha(to)));
        }

        public Task<SignedRelease?> GetReleaseAsync(CancellationToken ct)
        {
            ReleaseRequests++;
            return Task.FromResult(_release);
        }

        public Task<Stream> DownloadFileAsync(string relativePath, CancellationToken ct)
        {
            Downloaded.Add(relativePath);
            var bytes = Override.TryGetValue(relativePath, out var o) ? o : _files[relativePath];
            return Task.FromResult<Stream>(new MemoryStream(bytes));
        }

        public Exception? PatchManifestError { get; init; }
        public int PatchFormatVersion { get; init; } = PatchManifest.CurrentFormatVersion;

        public Task<PatchManifest?> DownloadPatchManifestAsync(CancellationToken ct) =>
            PatchManifestError is { } error ? Task.FromException<PatchManifest?>(error) :
            Task.FromResult<PatchManifest?>(new PatchManifest
            {
                FormatVersion = PatchFormatVersion,
                FromVersion = V1,
                ToVersion = V2,
                PatchedFiles = _patched,
                NewFiles = [],
                DeletedFiles = ServerDeletedFiles,
                VerificationList = [],
            });

        public Task<Stream> OpenPatchEntryAsync(string patchSha256, long maxBytes, CancellationToken ct) =>
            Task.FromResult<Stream>(new MemoryStream(_patches[patchSha256]));

        public Task<Stream> DownloadFullBuildAsync(CancellationToken ct) =>
            Task.FromResult<Stream>(new MemoryStream(FullBuild ?? throw new InvalidOperationException("no full build")));
    }

    /// <summary>Fails every rename whose destination is <paramref name="failDest"/>.</summary>
    private sealed class FailMoveIntoFileSystem(InMemoryFileSystem inner, string failDest) : IFileSystem
    {
        public Task<FileSystemResult> MoveFileAsync(string source, string dest, bool overwrite, CancellationToken ct) =>
            string.Equals(Path.GetFullPath(dest), Path.GetFullPath(failDest), StringComparison.OrdinalIgnoreCase)
            && source.Contains($"{Path.DirectorySeparatorChar}stage{Path.DirectorySeparatorChar}")
                ? Task.FromResult(FileSystemResult.Fail(new FileSystemError(FileSystemErrorType.Unknown, "disk on fire")))
                : inner.MoveFileAsync(source, dest, overwrite, ct);

        public Task<FileSystemResult> CopyFileAsync(string source, string dest, bool overwrite, CancellationToken ct) => inner.CopyFileAsync(source, dest, overwrite, ct);
        public Task<FileSystemResult> DeleteFileAsync(string path, CancellationToken ct) => inner.DeleteFileAsync(path, ct);
        public Task<FileSystemResult> CreateDirectoryAsync(string path, CancellationToken ct) => inner.CreateDirectoryAsync(path, ct);
        public Task<FileSystemResult> DeleteDirectoryAsync(string path, bool recursive, CancellationToken ct) => inner.DeleteDirectoryAsync(path, recursive, ct);
        public Task<FileSystemResult<byte[]>> ReadAllBytesAsync(string path, CancellationToken ct) => inner.ReadAllBytesAsync(path, ct);
        public Task<FileSystemResult> WriteAllBytesAsync(string path, byte[] data, CancellationToken ct) => inner.WriteAllBytesAsync(path, data, ct);
        public Task<FileSystemResult<Stream>> OpenReadAsync(string path, CancellationToken ct) => inner.OpenReadAsync(path, ct);
        public Task<FileSystemResult<Stream>> OpenWriteAsync(string path, CancellationToken ct) => inner.OpenWriteAsync(path, ct);
        public bool Exists(string path) => inner.Exists(path);
        public bool DirectoryExists(string path) => inner.DirectoryExists(path);
        public Task<string> ComputeSha256Async(string path, CancellationToken ct) => inner.ComputeSha256Async(path, ct);
        public IEnumerable<string> EnumerateFiles(string path, string searchPattern = "*", bool recursive = false) => inner.EnumerateFiles(path, searchPattern, recursive);
        public long GetFileSize(string path) => inner.GetFileSize(path);
    }

    /// <summary>Fails every write and directory creation under <paramref name="prefix"/>.</summary>
    private sealed class FailWritesUnder(InMemoryFileSystem inner, string prefix) : IFileSystem
    {
        private bool Under(string path) => Path.GetFullPath(path).StartsWith(Path.GetFullPath(prefix), StringComparison.OrdinalIgnoreCase);
        private static FileSystemError ReadOnly => new(FileSystemErrorType.Unknown, "read-only");

        public Task<FileSystemResult> MoveFileAsync(string source, string dest, bool overwrite, CancellationToken ct) =>
            Under(dest) ? Task.FromResult(FileSystemResult.Fail(ReadOnly)) : inner.MoveFileAsync(source, dest, overwrite, ct);
        public Task<FileSystemResult> CopyFileAsync(string source, string dest, bool overwrite, CancellationToken ct) => inner.CopyFileAsync(source, dest, overwrite, ct);
        public Task<FileSystemResult> DeleteFileAsync(string path, CancellationToken ct) => inner.DeleteFileAsync(path, ct);
        public Task<FileSystemResult> CreateDirectoryAsync(string path, CancellationToken ct) =>
            Under(path) ? Task.FromResult(FileSystemResult.Fail(ReadOnly)) : inner.CreateDirectoryAsync(path, ct);
        public Task<FileSystemResult> DeleteDirectoryAsync(string path, bool recursive, CancellationToken ct) => inner.DeleteDirectoryAsync(path, recursive, ct);
        public Task<FileSystemResult<byte[]>> ReadAllBytesAsync(string path, CancellationToken ct) => inner.ReadAllBytesAsync(path, ct);
        public Task<FileSystemResult> WriteAllBytesAsync(string path, byte[] data, CancellationToken ct) =>
            Under(path) ? Task.FromResult(FileSystemResult.Fail(ReadOnly)) : inner.WriteAllBytesAsync(path, data, ct);
        public Task<FileSystemResult<Stream>> OpenReadAsync(string path, CancellationToken ct) => inner.OpenReadAsync(path, ct);
        public Task<FileSystemResult<Stream>> OpenWriteAsync(string path, CancellationToken ct) =>
            Under(path) ? Task.FromResult(FileSystemResult<Stream>.Fail(ReadOnly)) : inner.OpenWriteAsync(path, ct);
        public bool Exists(string path) => inner.Exists(path);
        public bool DirectoryExists(string path) => inner.DirectoryExists(path);
        public Task<string> ComputeSha256Async(string path, CancellationToken ct) => inner.ComputeSha256Async(path, ct);
        public IEnumerable<string> EnumerateFiles(string path, string searchPattern = "*", bool recursive = false) => inner.EnumerateFiles(path, searchPattern, recursive);
        public long GetFileSize(string path) => inner.GetFileSize(path);
    }
}
