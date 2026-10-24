using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Diff;
using Instella.Core.FileSystem;
using Instella.Core.Installation;
using Instella.Core.Platform;
using Instella.Core.Update;
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Runtime.Core.Processes;
using Instella.Installer.Runtime.Core.Update;
using Instella.Installer.Runtime.Runners;
using Instella.Installer.Testing;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Transactions;

/// <summary>
/// The updater over an installation whose manifest records a Windows service: the service is
/// stopped through the service manager before the files change and started again afterwards; a
/// failed start rolls the update back; <c>--restart</c> starts the service instead of launching
/// its executable; <c>--recover</c> starts again what an interrupted update left stopped.
/// </summary>
public partial class UpdaterEngineTests
{
    private const string ServiceName = "App.Svc";

    [Test, Platform("Win")]
    public async Task Update_StopsTheRunningService_BeforeCommit_AndStartsItAgainAfter()
    {
        var scm = await InstallWithService(running: true);
        var server = new FakeDownloader(Release(V2, new() { ["App.exe"] = B("app-v2") }));
        var stateAtCommit = new List<FakeServiceState?>();
        var fs = new MoveHookFileSystem(_fs, onMove: (source, _) =>
        {
            if (IsStaged(source)) stateAtCommit.Add(scm.Find(ServiceName)?.State);
        });

        var result = await ServiceEngine(fs, server, scm).RunAsync(CancellationToken.None);

        Assert.That(result.Success, Is.True, result.Error);
        Assert.That(Text("App.exe"), Is.EqualTo("app-v2"));
        Assert.That(stateAtCommit, Is.Not.Empty.And.All.EqualTo(FakeServiceState.Stopped),
            "no file is swapped under the running service");
        Assert.That(scm.Find(ServiceName)!.State, Is.EqualTo(FakeServiceState.Running), "the service runs the new version");
        Assert.That(scm.Calls, Is.EqualTo(new[] { "create:" + ServiceName, "start:" + ServiceName, "stop:" + ServiceName, "start:" + ServiceName }));
    }

    [Test, Platform("Win")]
    public async Task Repair_StopsTheRunningService_AndStartsItAgainAfter()
    {
        var scm = await InstallWithService(running: true);
        Tamper("App.exe");
        var server = new FakeDownloader(Release(V1, new() { ["App.exe"] = B("app-v1") }));

        var result = await ServiceEngine(_fs, server, scm, to: V1, repair: true).RunAsync(CancellationToken.None);

        Assert.That(result.Success, Is.True, result.Error);
        Assert.That(Text("App.exe"), Is.EqualTo("app-v1"));
        Assert.That(scm.Calls.Skip(2), Is.EqualTo(new[] { "stop:" + ServiceName, "start:" + ServiceName }));
        Assert.That(scm.Find(ServiceName)!.State, Is.EqualTo(FakeServiceState.Running));
    }

    [Test, Platform("Win")]
    public async Task Update_ServiceThatWillNotStop_Exits26_BeforeStaging()
    {
        var scm = await InstallWithService(running: true);
        scm.SetStopBehavior(ServiceName, FakeServiceBehavior.Fail);
        var server = new FakeDownloader(Release(V2, new() { ["App.exe"] = B("app-v2") }));

        var result = await ServiceEngine(_fs, server, scm).RunAsync(CancellationToken.None);

        Assert.That((int)result.ExitCode, Is.EqualTo(26), "UpdateServiceFailed");
        Assert.That(result.Error, Does.Contain(ServiceName));
        Assert.That(Text("App.exe"), Is.EqualTo("app-v1"));
        Assert.That(server.Downloaded, Is.Empty, "nothing is staged once a service would not stop");
        Assert.That(_fs.DirectoryExists(Path.Combine(_root, ".instella", "txn")), Is.False);
        Assert.That(scm.Find(ServiceName)!.State, Is.EqualTo(FakeServiceState.Running));
    }

    [Test, Platform("Win")]
    public async Task Update_NewVersionFailsToStart_RollsBack_StartsThePreviousVersion_Exits26()
    {
        var scm = await InstallWithService(running: true);
        var server = new FakeDownloader(Release(V2, new() { ["App.exe"] = B("app-v2") }));
        var flaky = new FailFirstStartAfterStop(scm);

        var result = await ServiceEngine(_fs, server, flaky).RunAsync(CancellationToken.None);

        Assert.That((int)result.ExitCode, Is.EqualTo(26), "UpdateServiceFailed");
        Assert.That(result.Error, Does.Contain(ServiceName));
        Assert.That(Text("App.exe"), Is.EqualTo("app-v1"), "the files were rolled back");
        Assert.That((await ReadManifest()).Version, Is.EqualTo(V1));
        Assert.That(scm.Find(ServiceName)!.State, Is.EqualTo(FakeServiceState.Running), "the previous version runs again");
        Assert.That(scm.Calls.Skip(2), Is.EqualTo(new[] { "stop:" + ServiceName, "start:" + ServiceName, "start:" + ServiceName }));
        Assert.That(Markers(), Is.Empty);
    }

    [Test, Platform("Win")]
    public async Task Update_ServiceThatWasStopped_IsNotStarted()
    {
        var scm = await InstallWithService(running: false);
        var server = new FakeDownloader(Release(V2, new() { ["App.exe"] = B("app-v2") }));

        var result = await ServiceEngine(_fs, server, scm).RunAsync(CancellationToken.None);

        Assert.That(result.Success, Is.True, result.Error);
        Assert.That(scm.Find(ServiceName)!.State, Is.EqualTo(FakeServiceState.Stopped));
        Assert.That(scm.Calls.Where(c => c.StartsWith("start:", StringComparison.Ordinal)), Is.Empty);
    }

    [Test, Platform("Win")]
    public async Task Update_InterruptedWithTheServiceStopped_RecoverStartsItAgain()
    {
        var scm = await InstallWithService(running: true);
        var server = new FakeDownloader(Release(V2, new() { ["App.exe"] = B("app-v2") }));
        // Every staged file fails to move into place and every backup fails to move back: the
        // update ends as a crash would leave it, with the journal still "committing".
        var broken = new MoveHookFileSystem(_fs, fail: (source, _) => IsStaged(source) || IsBackup(source));

        var update = await ServiceEngine(broken, server, scm).RunAsync(CancellationToken.None);

        Assert.That(update.ExitCode, Is.EqualTo(InstellaExitCode.UpdateRollbackFailed), update.Error);
        Assert.That(scm.Find(ServiceName)!.State, Is.EqualTo(FakeServiceState.Stopped),
            "with the files half rolled back the service is left stopped for --recover");

        await using var harness = InstellaTestHarness.Create()
            .WithInstaller(InstellaInstaller.Create().WithApp("App", AppId, V1).WithExecutableName("App.exe")
                .WithElevation(Instella.Core.Manifest.ElevationMode.SystemWide).Build())
            .WithFileSystem(_fs)
            .WithPlatformServices(_platform)
            .WithElevationService(new FakeElevationService(isElevated: true))
            .Build();
        var exit = await harness.RunFullWithArgsAsync(["--recover", "--silent", "--path", _root]);

        Assert.That(exit, Is.EqualTo(0), string.Join("\n", harness.LogSink.Entries.Select(e => e.Message)));
        Assert.That(Text("App.exe"), Is.EqualTo("app-v1"));
        Assert.That(scm.Find(ServiceName)!.State, Is.EqualTo(FakeServiceState.Running), "recover starts again what was running");
    }

    [Test, Platform("Win")]
    public async Task Restart_WhenTheMainExecutableIsAService_StartsTheService_InsteadOfLaunchingTheExe()
    {
        // The user had stopped the service: the update leaves it alone, but --restart asks for the
        // app to run again, and the app is the service.
        var scm = await InstallWithService(running: false, unsigned: true);
        var launcher = new RecordingLauncher();
        var services = new InstallerServices(_platform, _fs,
            HttpHandler: new ZipHandler(Zip(new() { ["App.exe"] = B("app-v2") })),
            ProcessFinder: new NoProcesses(),
            AppLauncher: launcher,
            StubDirectory: () => _root,
            ServiceManager: scm);
        var config = ((InstellaInstallerImpl)InstellaInstaller.Create()
            .WithApp("App", AppId, V1)
            .WithExecutableName("App.exe")
            .WithElevation(Instella.Core.Manifest.ElevationMode.SystemWide)
            .OnWindows(w => w.AddService(ServiceName, _ => { }))
            .Build()).ConfigForTests;
        var args = (Args(V1, V2, usePatch: false, repair: false) with { Restart = true, Silent = true }).ToArgumentList().ToArray();
        var sink = new RecordingSink();

        var exit = await new UpdateModeRunner(config, new RecordingLogger(sink), _platform, _fs) { Services = services }
            .RunAsync(args, CancellationToken.None);

        Assert.That(exit, Is.EqualTo(InstellaExitCode.Success), string.Join("\n", sink.Entries.Select(e => e.Message)));
        Assert.That(Text("App.exe"), Is.EqualTo("app-v2"));
        Assert.That(launcher.Launched, Is.Empty, "a service executable is never started as a desktop program");
        Assert.That(scm.Find(ServiceName)!.State, Is.EqualTo(FakeServiceState.Running), "the service is started instead");
    }

    // ---- helpers ----

    private FakePlatformServices _platform = null!;

    /// <summary>
    /// A machine-wide installation of v1 whose manifest records <see cref="ServiceName"/>, running
    /// <c>App.exe</c>; the service exists in a fresh fake service manager, running or stopped.
    /// </summary>
    private async Task<FakeServiceManager> InstallWithService(bool running, bool unsigned = false)
    {
        Install(new() { ["App.exe"] = B("app-v1") }, trusted: !unsigned, allowUnsigned: unsigned, perUser: false);
        var path = Path.Combine(_root, InstellaOwnedPaths.InstalledManifest);
        var manifest = JsonSerializer.Deserialize(_fs.Snapshot()[Path.GetFullPath(path)], InstalledManifestJsonContext.Default.InstalledManifest)!;
        manifest = manifest with
        {
            ServerUrl = "https://updates.example.com",
            Services = [new ManifestService(ServiceName, "App.exe", AccountName: null, ManifestService.Automatic, CreatedByInstella: true)],
        };
        _fs.AddFile(path, JsonSerializer.SerializeToUtf8Bytes(manifest, InstalledManifestJsonContext.Default.InstalledManifest));

        _platform = new FakePlatformServices();
        var scm = _platform.ServiceManager;
        IServiceManager manager = scm;
        var created = await manager.CreateAsync(new ServiceConfiguration
        {
            Name = ServiceName,
            DisplayName = ServiceName,
            ExecutablePath = Path.Combine(_root, "App.exe"),
        }, null, CancellationToken.None);
        Assert.That(created.Outcome.Success, Is.True, created.Outcome.Error);
        if (running)
            Assert.That((await manager.StartAsync(ServiceName, TimeSpan.FromSeconds(1), CancellationToken.None)).Outcome.Success, Is.True);
        return scm;
    }

    private UpdaterEngine ServiceEngine(IFileSystem fs, FakeDownloader server, IServiceManager scm, Version? to = null, bool repair = false) =>
        new(Args(V1, to ?? V2, usePatch: false, repair), server, _platform, fs, BsDiffEngine.Instance, NoDelays)
        {
            ProcessFinder = new NoProcesses(),
            ServiceManager = scm,
        };

    private void Tamper(string rel) => _fs.AddFile(SafePath.Combine(_root, rel), B("tampered"));

    private static bool IsStaged(string path) => path.Contains($"{Path.DirectorySeparatorChar}stage{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

    private static bool IsBackup(string path) => path.Contains($"{Path.DirectorySeparatorChar}backup{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

    /// <summary>No program uses the app's files.</summary>
    private sealed class NoProcesses : ILockingProcessFinder
    {
        public IReadOnlyList<LockingProcess> Find(IReadOnlyList<string> files) => [];
    }

    private sealed class RecordingLauncher : IAppLauncher
    {
        public List<string> Launched { get; } = [];
        public void Launch(string exePath, string workingDirectory) => Launched.Add(exePath);
    }

    /// <summary>Answers every request with the full build (an unsigned installation downloads only that).</summary>
    private sealed class ZipHandler(byte[] zip) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) });
    }

    /// <summary>
    /// The fake service manager, except that the first start after a stop fails: the new version
    /// does not start, the previous one does.
    /// </summary>
    private sealed class FailFirstStartAfterStop(FakeServiceManager inner) : IServiceManager
    {
        private readonly IServiceManager _inner = inner;
        private bool _stopped;
        private bool _failed;

        public Task<ServiceResult> StartAsync(string name, TimeSpan timeout, CancellationToken ct)
        {
            if (_stopped && !_failed)
            {
                _failed = true;
                inner.SetStartBehavior(name, FakeServiceBehavior.Fail);
                var failed = _inner.StartAsync(name, timeout, ct);
                inner.SetStartBehavior(name, FakeServiceBehavior.Succeed);
                return failed;
            }
            return _inner.StartAsync(name, timeout, ct);
        }

        public Task<ServiceResult> StopAsync(string name, TimeSpan timeout, CancellationToken ct)
        {
            _stopped = true;
            return _inner.StopAsync(name, timeout, ct);
        }

        public Task<ServiceResult> CreateAsync(ServiceConfiguration configuration, char[]? password, CancellationToken ct) => _inner.CreateAsync(configuration, password, ct);
        public Task<ServiceResult> ChangeConfigurationAsync(ServiceConfiguration configuration, char[]? password, CancellationToken ct) => _inner.ChangeConfigurationAsync(configuration, password, ct);
        public Task<ServiceResult<ServiceConfigurationInfo>> QueryConfigurationAsync(string name, CancellationToken ct) => _inner.QueryConfigurationAsync(name, ct);
        public Task<ServiceResult<ServiceStatus>> QueryStatusAsync(string name, CancellationToken ct) => _inner.QueryStatusAsync(name, ct);
        public Task<ServiceResult<IReadOnlyList<string>>> QueryActiveDependentsAsync(string name, CancellationToken ct) => _inner.QueryActiveDependentsAsync(name, ct);
        public Task<ServiceResult> DeleteAsync(string name, CancellationToken ct) => _inner.DeleteAsync(name, ct);
    }

    /// <summary>
    /// Passes every call through; <paramref name="onMove"/> sees each rename first, and a rename
    /// <paramref name="fail"/> picks fails without touching anything.
    /// </summary>
    private sealed class MoveHookFileSystem(IFileSystem inner, Func<string, string, bool>? fail = null, Action<string, string>? onMove = null) : IFileSystem
    {
        public Task<FileSystemResult> MoveFileAsync(string source, string dest, bool overwrite, CancellationToken ct)
        {
            onMove?.Invoke(source, dest);
            return fail?.Invoke(source, dest) == true
                ? Task.FromResult(FileSystemResult.Fail(new FileSystemError(FileSystemErrorType.Unknown, "disk on fire")))
                : inner.MoveFileAsync(source, dest, overwrite, ct);
        }

        public Task<FileSystemResult> CopyFileAsync(string source, string dest, bool overwrite, CancellationToken ct) => inner.CopyFileAsync(source, dest, overwrite, ct);
        public Task<FileSystemResult> DeleteFileAsync(string path, CancellationToken ct) => inner.DeleteFileAsync(path, ct);
        public Task<FileSystemResult> CreateDirectoryAsync(string path, CancellationToken ct) => inner.CreateDirectoryAsync(path, ct);
        public Task<FileSystemResult> DeleteDirectoryAsync(string path, bool recursive, CancellationToken ct) => inner.DeleteDirectoryAsync(path, recursive, ct);
        public Task<FileSystemResult<byte[]>> ReadAllBytesAsync(string path, CancellationToken ct) => inner.ReadAllBytesAsync(path, ct);
        public Task<FileSystemResult> WriteAllBytesAsync(string path, byte[] data, CancellationToken ct) => inner.WriteAllBytesAsync(path, data, ct);
        public Task<FileSystemResult<Stream>> OpenReadAsync(string path, CancellationToken ct) => inner.OpenReadAsync(path, ct);
        public Task<FileSystemResult<Stream>> OpenWriteAsync(string path, CancellationToken ct) => inner.OpenWriteAsync(path, ct);
        public Task<FileSystemResult> SetUnixFileModeAsync(string path, UnixFileMode mode, CancellationToken ct) => inner.SetUnixFileModeAsync(path, mode, ct);
        public bool Exists(string path) => inner.Exists(path);
        public bool DirectoryExists(string path) => inner.DirectoryExists(path);
        public Task<string> ComputeSha256Async(string path, CancellationToken ct) => inner.ComputeSha256Async(path, ct);
        public IEnumerable<string> EnumerateFiles(string path, string searchPattern = "*", bool recursive = false) => inner.EnumerateFiles(path, searchPattern, recursive);
        public long GetFileSize(string path) => inner.GetFileSize(path);
    }
}
