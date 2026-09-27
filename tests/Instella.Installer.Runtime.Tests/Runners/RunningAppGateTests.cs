using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Instella.Core.FileSystem;
using Instella.Core.Installation;
using Instella.Core.Logging;
using Instella.Core.Platform.Windows;
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Runtime.Core.Processes;
using Instella.Installer.Runtime.Runners;
using Instella.Installer.Testing;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Runners;

/// <summary>
/// Locked files: uninstall, install and update close every program using the app's files
/// (found by Restart Manager, whatever the program is called) before touching them, and a
/// file that stays locked never makes the folder unusable for the next install.
/// </summary>
[TestFixture]
public class RunningAppGateTests
{
    private string _root = null!;
    private readonly List<Process> _blockers = [];

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "instella-gate-tests", Guid.NewGuid().ToString("N"), "QuickNotes");
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        foreach (var p in _blockers)
        {
            try { if (!p.HasExited) p.Kill(); p.WaitForExit(5000); } catch { /* best effort */ }
            p.Dispose();
        }
        _blockers.Clear();
        try { Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true); } catch { /* best effort */ }
    }

    [Test, Platform("Win"), SupportedOSPlatform("windows")]
    public void RestartManager_FindsTheProgramHoldingAFile_WhateverItIsCalled()
    {
        var file = Path.Combine(_root, "notes.log");
        var blocker = HoldOpen(file);

        var found = RestartManager.FindLockingProcesses([file]);

        Assert.That(found.Select(p => p.Id), Does.Contain(blocker.Id));
        Assert.That(found.Single(p => p.Id == blocker.Id).CanClose, Is.True);
    }

    [Test, Platform("Win"), SupportedOSPlatform("windows")]
    public async Task Uninstall_Silent_WhileAProgramHoldsAFile_RefusesAndDeletesNothing()
    {
        await WriteInstallation();
        var blocker = HoldOpen(Path.Combine(_root, "lib", "core.dll"));

        var exit = await Uninstall(forceClose: false);

        Assert.That(exit, Is.EqualTo(InstellaExitCode.UninstallFilesLocked));
        Assert.That(File.Exists(Path.Combine(_root, "QuickNotes.exe")), Is.True, "nothing is deleted while the app runs");
        Assert.That(File.Exists(Path.Combine(_root, InstellaOwnedPaths.InstalledManifest)), Is.True);
        Assert.That(blocker.HasExited, Is.False, "a silent uninstall does not close programs without --force-close");
    }

    [Test, Platform("Win"), SupportedOSPlatform("windows")]
    public async Task Uninstall_ForceClose_EndsTheProgram_AndRemovesTheInstallation()
    {
        await WriteInstallation();
        var blocker = HoldOpen(Path.Combine(_root, "lib", "core.dll"));

        var exit = await Uninstall(forceClose: true);

        Assert.That(exit, Is.EqualTo(InstellaExitCode.Success));
        Assert.That(blocker.WaitForExit(5000), Is.True, "the program holding the file was ended");
        Assert.That(Directory.Exists(_root), Is.False, "nothing is left of the install directory");
    }

    [Test]
    public async Task Prompt_GetsTheProgramNames_AndRetryChecksAgain()
    {
        var finder = new SequenceFinder([new LockingProcess(4242, "QuickNotes", CanClose: true)], []);
        var asked = new List<IReadOnlyList<string>>();
        var gate = new RunningAppGate(new FakePlatformServices(), new ListLog(), finder);

        var ok = await gate.EnsureClosedAsync("QuickNotes", _root, "QuickNotes.exe", forceClose: false,
            (_, programs) => { asked.Add(programs); return AppRunningChoice.Retry; }, CancellationToken.None);

        Assert.That(ok, Is.True, "the program was closed by the user before Try Again");
        Assert.That(asked.Single(), Is.EqualTo(new[] { "QuickNotes (PID 4242)" }));
    }

    [Test]
    public async Task ExplorerAndServices_AreNeverClosed_AndDoNotBlock()
    {
        var finder = new SequenceFinder([new LockingProcess(4, "Windows Explorer", CanClose: false)]);
        var gate = new RunningAppGate(new FakePlatformServices(), new ListLog(), finder);

        var ok = await gate.EnsureClosedAsync("QuickNotes", _root, "QuickNotes.exe", forceClose: false,
            (_, _) => throw new AssertionException("nothing to ask about"), CancellationToken.None);

        Assert.That(ok, Is.True);
    }

    [Test, Platform("Win"), SupportedOSPlatform("windows")]
    public async Task FileStillLocked_GoesToPendingDelete_AndDoesNotBlockTheNextInstall()
    {
        // A loaded DLL cannot be deleted, only renamed: what happened to Avalonia.Base.dll.
        // This test process loads it, and a program never counts as blocking itself.
        await WriteInstallation(dll: true);
        var handle = NativeLibrary.Load(Path.Combine(_root, "lib", "native.dll"));
        try
        {
            (string Path, string Token)? launched = null;
            var uninstall = new UninstallModeRunner(Config(), new ListLog(), new FakePlatformServices(), RealFileSystem.Instance)
            {
                CleanupLauncher = (path, token) => launched = (path, token),
            };
            var exit = await uninstall.RunAsync(new DispatchResult(DispatchKind.Mode, InstallerMode.Uninstall, _root, IsSilent: true), CancellationToken.None);

            Assert.That(exit, Is.EqualTo(InstellaExitCode.UninstallFilesLocked));
            Assert.That(File.Exists(Path.Combine(_root, "lib", "native.dll")), Is.False, "the locked DLL left the app folder");
            var remaining = CleanupModeRunner.ReadTombstone(_root)!.Remaining;
            Assert.That(remaining.Single(), Does.StartWith(InstellaOwnedPaths.PendingDeleteDirectory + "/").And.EndWith("-native.dll"));

            var log = new ListLog();
            await new CleanupModeRunner(log, [TimeSpan.Zero], TimeSpan.Zero).RunAsync(
                new DispatchResult(DispatchKind.Mode, InstallerMode.Cleanup, _root),
                ["--cleanup", "--path", _root, "--token", launched!.Value.Token], CancellationToken.None);
            Assert.That(log.Lines, Has.Some.Contains("still in use, could not delete"));
            Assert.That(log.Lines, Has.None.Contains("not installed by Instella"), "the leftover is Instella's, not the user's");
        }
        finally
        {
            NativeLibrary.Free(handle);
        }

        var (mode, existing) = await InstallModeResolver.ResolveAsync(Config(), RealFileSystem.Instance, _root,
            new DispatchResult(DispatchKind.Mode, InstallerMode.FirstInstall, _root, IsSilent: true), new ListLog(), CancellationToken.None);
        Assert.That(mode, Is.EqualTo(InstallerMode.FirstInstall), "the folder is not refused as 'not empty'");
        Assert.That(existing, Is.Null);
        Assert.That(Directory.Exists(Path.Combine(_root, InstellaOwnedPaths.StateDirectory)), Is.False, "the leftovers are deleted");
    }

    [Test]
    public async Task Cleanup_FileItCouldNotDelete_IsNotCalledAUserFile()
    {
        var file = Path.Combine(_root, "lib", "core.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "dll");
        WriteTombstone("tok", ["lib/core.dll"]);
        var log = new ListLog();

        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
            await new CleanupModeRunner(log, [TimeSpan.Zero], TimeSpan.Zero).RunAsync(
                new DispatchResult(DispatchKind.Mode, InstallerMode.Cleanup, _root),
                ["--cleanup", "--path", _root, "--token", "tok"], CancellationToken.None);

        Assert.That(log.Lines, Has.Some.Contains("could not delete: lib/core.dll"));
        Assert.That(log.Lines, Has.None.Contains("not installed by Instella"));
    }

    // ---- helpers ----

    /// <summary>
    /// Starts <c>cmd /c pause &gt; file</c>: one process, no window, not named like the app,
    /// holding <paramref name="file"/> open without delete sharing until it is ended.
    /// </summary>
    private Process HoldOpen(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true };
        foreach (var a in new[] { "/c", "pause", ">", file }) psi.ArgumentList.Add(a);
        var p = Process.Start(psi)!;
        _blockers.Add(p);
        for (var i = 0; i < 100; i++)
        {
            try { using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read)) { } }
            catch (FileNotFoundException) { Thread.Sleep(50); continue; }
            catch (IOException) { return p; }   // open for writing by the blocker: ready
            Thread.Sleep(50);
        }
        Assert.Fail("the blocker never opened its file");
        return p;
    }

    private async Task WriteInstallation(bool dll = false)
    {
        var files = new List<InstalledFile> { new("QuickNotes.exe", "", 3), new("lib/core.dll", "", 3) };
        File.WriteAllText(Path.Combine(_root, "QuickNotes.exe"), "app");
        Directory.CreateDirectory(Path.Combine(_root, "lib"));
        File.WriteAllText(Path.Combine(_root, "lib", "core.dll"), "dll");
        if (dll)
        {
            File.Copy(Path.Combine(Environment.SystemDirectory, "version.dll"), Path.Combine(_root, "lib", "native.dll"));
            files.Add(new InstalledFile("lib/native.dll", "", 1));
        }
        await new InstallManifestWriter(RealFileSystem.Instance).WriteAsync(_root, new InstalledManifest
        {
            AppName = "QuickNotes", AppId = "com.app", Version = new Version(1, 0), InstallDirectory = _root,
            ExecutableName = "QuickNotes.exe", InstalledAt = DateTime.UtcNow, InstalledPerUser = true, Files = files,
        }, CancellationToken.None);
    }

    [SupportedOSPlatform("windows")]
    private Task<InstellaExitCode> Uninstall(bool forceClose)
    {
        var uninstall = new UninstallModeRunner(Config(), new ListLog(), new WindowsPlatformServices(), RealFileSystem.Instance)
        {
            CleanupLauncher = (_, _) => Assert.Fail("nothing should be left for cleanup"),
        };
        return uninstall.RunAsync(
            new DispatchResult(DispatchKind.Mode, InstallerMode.Uninstall, _root, IsSilent: true, ForceClose: forceClose),
            CancellationToken.None);
    }

    private static FrozenConfig Config() =>
        ((InstellaInstallerImpl)InstellaInstaller.Create().WithApp("QuickNotes", "com.app", new Version(1, 0)).Build()).ConfigForTests;

    private void WriteTombstone(string nonce, IReadOnlyList<string> remaining)
    {
        var path = Path.Combine(_root, ".instella", "uninstall.tombstone");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(
            new UninstallTombstone("com.app", _root, nonce, DateTimeOffset.UtcNow, remaining),
            CleanupJsonContext.Default.UninstallTombstone));
    }

    /// <summary>Returns the given lists in turn, then the last one forever.</summary>
    private sealed class SequenceFinder(params IReadOnlyList<LockingProcess>[] answers) : ILockingProcessFinder
    {
        private int _call;
        public IReadOnlyList<LockingProcess> Find(IReadOnlyList<string> files) => answers[Math.Min(_call++, answers.Length - 1)];
    }

    private sealed class ListLog : IInstellaLogger, IDisposable
    {
        public List<string> Lines { get; } = [];
        public void Trace(string message) { }
        public void Debug(string message) { }
        public void Info(string message) => Lines.Add(message);
        public void Warn(string message) => Lines.Add(message);
        public void Error(string message, Exception? exception = null) => Lines.Add(message);
        public IDisposable Scope(string segment) => this;
        public bool IsEnabled(InstellaLogLevel level) => true;
        public void Dispose() { }
    }
}
