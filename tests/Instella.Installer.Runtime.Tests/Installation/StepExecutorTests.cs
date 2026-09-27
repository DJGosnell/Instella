using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.FileSystem;
using Instella.Core.Installation;
using Instella.Core.Logging;
using Instella.Core.Manifest;
using Instella.Core.Platform;
using Instella.Installer.Runtime.Core;
using Instella.Installer.Runtime.Installation;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Installation;

[TestFixture]
public class StepExecutorTests
{
    [Test]
    public async Task Runs_Steps_In_Declared_Order()
    {
        var log = new List<string>();
        var executor = new StepExecutor(new IInstallStepExecution[]
        {
            new RecordStep("a", InstallStage.Prereqs, log),
            new RecordStep("b", InstallStage.Extract, log),
            new RecordStep("c", InstallStage.Register, log),
        });

        var result = await executor.ExecuteAsync(MakeContext(), progress: null, CancellationToken.None);

        Assert.That(result.Success, Is.True);
        Assert.That(log, Is.EqualTo(new[] { "a:exec", "b:exec", "c:exec" }));
    }

    [Test]
    public async Task Failure_Rolls_Back_Completed_Steps_In_Reverse_Order()
    {
        var log = new List<string>();
        var executor = new StepExecutor(new IInstallStepExecution[]
        {
            new RecordStep("a", InstallStage.Prereqs, log),
            new RecordStep("b", InstallStage.Extract, log),
            new FailingStep("c", InstallStage.Register, "boom", log),
            new RecordStep("d", InstallStage.Finalize, log), // never reached
        });

        var result = await executor.ExecuteAsync(MakeContext(), progress: null, CancellationToken.None);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Is.EqualTo("boom"));
        Assert.That(log, Is.EqualTo(new[]
        {
            "a:exec", "b:exec", "c:exec-fail",
            "b:rollback", "a:rollback",
        }));
    }

    [Test]
    public async Task Exception_Is_Treated_As_Failure_And_Triggers_Rollback()
    {
        var log = new List<string>();
        var executor = new StepExecutor(new IInstallStepExecution[]
        {
            new RecordStep("a", InstallStage.Prereqs, log),
            new ThrowingStep("b", InstallStage.Extract, new InvalidOperationException("kaboom"), log),
        });

        var result = await executor.ExecuteAsync(MakeContext(), progress: null, CancellationToken.None);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Is.EqualTo("kaboom"));
        Assert.That(log, Contains.Item("a:rollback"));
    }

    [Test]
    public async Task Rollback_Exceptions_Become_Warnings_Not_Failures_In_The_Unwind()
    {
        var log = new List<string>();
        var executor = new StepExecutor(new IInstallStepExecution[]
        {
            new RollbackThrowsStep("a", InstallStage.Prereqs, new Exception("rollback-a-boom"), log),
            new FailingStep("b", InstallStage.Extract, "step-b-boom", log),
        });

        var result = await executor.ExecuteAsync(MakeContext(), progress: null, CancellationToken.None);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Is.EqualTo("step-b-boom"));
        Assert.That(result.Warnings, Has.Some.Contains("rollback-a-boom"));
    }

    [Test]
    public async Task Progress_Fraction_Grows_Monotonically_Weighted()
    {
        var fractions = new List<double>();
        var progress = new SynchronousProgress<OverallProgress>(p => fractions.Add(p.Fraction));

        var executor = new StepExecutor(new IInstallStepExecution[]
        {
            new HalfReportingStep("a", InstallStage.Prereqs, weight: 1),
            new HalfReportingStep("b", InstallStage.Extract, weight: 3),
        });

        await executor.ExecuteAsync(MakeContext(), progress, CancellationToken.None);

        // After step a (weight 1 of 4), fraction should hit ~0.25.
        // After step b mid-progress, fraction includes a's weight (1/4)
        // plus half of b's weight (1.5/4) = ~0.625. After full b = 1.0.
        Assert.That(fractions, Is.Not.Empty);
        double previous = -1;
        foreach (var f in fractions)
        {
            Assert.That(f, Is.GreaterThanOrEqualTo(previous).Within(1e-9));
            previous = f;
        }
        Assert.That(fractions[^1], Is.EqualTo(1.0).Within(1e-9));
    }

    [Test]
    public async Task RunningStep_IsReportedWhenItStarts()
    {
        // Hand test: the wizard said "Finalize — write-manifest" (the last step that had
        // finished) all through commit-transaction, which reported nothing until it was done.
        OverallProgress? last = null;
        OverallProgress? seenBySlow = null;
        var progress = new SynchronousProgress<OverallProgress>(p => last = p);
        var executor = new StepExecutor(new IInstallStepExecution[]
        {
            new RecordStep("a", InstallStage.Finalize, new List<string>()),
            new ObservingStep("slow", InstallStage.Finalize, () => seenBySlow = last),
        });

        await executor.ExecuteAsync(MakeContext(), progress, CancellationToken.None);

        Assert.That(seenBySlow?.StepName, Is.EqualTo("slow"));
        Assert.That(seenBySlow!.Value.Fraction, Is.EqualTo(0.5).Within(1e-9), "a is done, slow has not reported yet");
    }

    [Test]
    public async Task Commit_ReportsVerifyingThenMoving_BeforeItFinishes()
    {
        var fs = new Instella.Installer.Testing.InMemoryFileSystem();
        var root = Path.Combine(Path.GetTempPath(), "instella-commit-tests", Guid.NewGuid().ToString("N"));
        var txn = await Instella.Installer.Runtime.Core.Transactions.InstallTransaction.BeginAsync(
            fs, root, TxnKind.FirstInstall, null, new Version(1, 0, 0), CancellationToken.None);
        for (var i = 0; i < 4; i++)
            await txn.StageFileAsync($"f{i}.dll", new MemoryStream([(byte)i]), null, null, false, CancellationToken.None);
        var context = MakeContext();
        context.Transaction = txn;
        var reports = new List<(double Fraction, string? Status)>();

        var result = await new Instella.Installer.Runtime.Installation.BuiltIn.CommitTransactionStep()
            .ExecuteAsync(context, new RecordingStepProgress(reports), CancellationToken.None);

        Assert.That(result.Success, Is.True, result.Error);
        Assert.That(fs.Exists(Path.Combine(root, "f3.dll")), Is.True);
        var statuses = reports.Select(r => r.Status).Distinct().ToList();
        Assert.That(statuses, Is.EqualTo(new[] { "verifying files", "moving files into place", null }), "in this order");
        Assert.That(reports.Select(r => r.Fraction), Is.Ordered);
        Assert.That(reports.Count(r => r.Fraction < 1.0), Is.GreaterThanOrEqualTo(8), "a report per file, not one at the end");
    }

    [Test]
    public async Task Pre_Execution_Cancellation_Returns_Cancelled_And_Runs_No_Step()
    {
        var log = new List<string>();
        var executor = new StepExecutor(new IInstallStepExecution[]
        {
            new RecordStep("a", InstallStage.Prereqs, log),
        });
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await executor.ExecuteAsync(MakeContext(), progress: null, cts.Token);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Is.EqualTo("Installation cancelled"));
        Assert.That(log, Is.Empty);
    }

    [Test]
    public void Constructor_Rejects_Null_Steps()
    {
        Assert.Throws<ArgumentNullException>(() => new StepExecutor(null!));
    }

    private static InstallContext MakeContext() => new TestContextBuilder().Build();

    // ----- Fakes -----

    private sealed class ObservingStep(string name, InstallStage stage, Action onExecute) : IInstallStepExecution
    {
        public string Name => name;
        public InstallStage Stage => stage;
        public int Weight => 1;
        public Task<StepResult> ExecuteAsync(InstallContext ctx, IStepProgress p, CancellationToken ct)
        {
            onExecute();
            return Task.FromResult(StepResult.Ok);
        }
        public Task RollbackAsync(InstallContext ctx, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class RecordingStepProgress(List<(double, string?)> reports) : IStepProgress
    {
        public void Report(double fraction, string? status = null) => reports.Add((fraction, status));
    }

    private sealed class RecordStep(string name, InstallStage stage, List<string> log) : IInstallStepExecution
    {
        public string Name => name;
        public InstallStage Stage => stage;
        public int Weight => 1;
        public Task<StepResult> ExecuteAsync(InstallContext ctx, IStepProgress p, CancellationToken ct)
        {
            log.Add($"{Name}:exec");
            p.Report(1.0);
            return Task.FromResult(StepResult.Ok);
        }
        public Task RollbackAsync(InstallContext ctx, CancellationToken ct)
        {
            log.Add($"{Name}:rollback");
            return Task.CompletedTask;
        }
    }

    private sealed class FailingStep(string name, InstallStage stage, string error, List<string> log) : IInstallStepExecution
    {
        public string Name => name;
        public InstallStage Stage => stage;
        public Task<StepResult> ExecuteAsync(InstallContext ctx, IStepProgress p, CancellationToken ct)
        {
            log.Add($"{Name}:exec-fail");
            return Task.FromResult(StepResult.Fail(error));
        }
    }

    private sealed class ThrowingStep(string name, InstallStage stage, Exception ex, List<string> log) : IInstallStepExecution
    {
        public string Name => name;
        public InstallStage Stage => stage;
        public Task<StepResult> ExecuteAsync(InstallContext ctx, IStepProgress p, CancellationToken ct)
        {
            log.Add($"{Name}:exec-throw");
            throw ex;
        }
    }

    private sealed class RollbackThrowsStep(string name, InstallStage stage, Exception rollbackError, List<string> log) : IInstallStepExecution
    {
        public string Name => name;
        public InstallStage Stage => stage;
        public Task<StepResult> ExecuteAsync(InstallContext ctx, IStepProgress p, CancellationToken ct)
        {
            log.Add($"{Name}:exec");
            return Task.FromResult(StepResult.Ok);
        }
        public Task RollbackAsync(InstallContext ctx, CancellationToken ct) => throw rollbackError;
    }

    private sealed class SynchronousProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

    private sealed class HalfReportingStep(string name, InstallStage stage, int weight) : IInstallStepExecution
    {
        public string Name => name;
        public InstallStage Stage => stage;
        public int Weight { get; } = weight;
        public Task<StepResult> ExecuteAsync(InstallContext ctx, IStepProgress p, CancellationToken ct)
        {
            p.Report(0.5);
            p.Report(1.0);
            return Task.FromResult(StepResult.Ok);
        }
    }
}

// Shared test helpers extracted so both StepExecutorTests and downstream
// fixtures can build an InstallContext without re-declaring stub services.
internal sealed class TestContextBuilder
{
    public string InstallPath { get; init; } = Path.Combine(Path.GetTempPath(), $"instella-test-{Guid.NewGuid():N}");
    public StubFileSystem FileSystem { get; } = new();
    public StubPlatformServices Platform { get; } = new();
    public InstellaManifest Manifest { get; init; } = new()
    {
        AppName = "TestApp",
        AppId = "com.test.app",
        Version = new Version(1, 0, 0),
        ServerUrl = "https://example.com",
        ExecutableName = "TestApp",
    };
    public InstallOptions Options { get; init; } = new()
    {
        InstallPath = "",
        CreateDesktopShortcut = false,
        CreateStartMenuShortcut = false,
        AddToPath = false,
        ConfigureAutoStart = false,
        RegisterFileAssociations = false,
        Elevation = ElevationMode.UserChoice,
    };
    public Stream? PayloadArchive { get; set; }

    public InstallContext Build() => new()
    {
        AppName = Manifest.AppName,
        AppId = Manifest.AppId,
        AppVersion = Manifest.Version,
        InstallPath = InstallPath,
        Mode = InstallerMode.FirstInstall,
        Scope = InstallationScope.PerUser,
        Manifest = Manifest,
        Options = Options with { InstallPath = InstallPath },
        Platform = Platform,
        FileSystem = FileSystem,
        Log = new NullLogger(),
        PayloadArchive = PayloadArchive,
    };
}

internal sealed class NullLogger : IInstellaLogger
{
    public void Trace(string message) { }
    public void Debug(string message) { }
    public void Info(string message) { }
    public void Warn(string message) { }
    public void Error(string message, Exception? exception = null) { }
    public IDisposable Scope(string segment) => new EmptyScope();
    public bool IsEnabled(InstellaLogLevel level) => true;
    private sealed class EmptyScope : IDisposable { public void Dispose() { } }
}

internal sealed class StubPlatformServices : IPlatformServices
{
    public TargetPlatform Platform => TargetPlatform.Windows;
    public List<string> Calls { get; } = new();

    public string GetDefaultInstallPath(string appName, bool perUser)
        => System.IO.Path.Combine(System.IO.Path.GetTempPath(), appName);

    public Task<PlatformResult> CreateShortcutAsync(ShortcutInfo info, CancellationToken ct) { Calls.Add($"CreateShortcut:{info.Name}:{info.Location}"); return Task.FromResult(PlatformResult.Ok); }
    public Task<PlatformResult> RemoveShortcutAsync(ShortcutInfo info, CancellationToken ct) { Calls.Add($"RemoveShortcut:{info.Name}:{info.Location}"); return Task.FromResult(PlatformResult.Ok); }
    public Task<PlatformResult> RegisterFileAssociationAsync(FileAssociationInfo info, CancellationToken ct) { Calls.Add($"RegisterFileAssoc:{info.Extension}"); return Task.FromResult(PlatformResult.Ok); }
    public Task<PlatformResult> UnregisterFileAssociationAsync(string extension, string appId, CancellationToken ct) { Calls.Add($"UnregisterFileAssoc:{extension}"); return Task.FromResult(PlatformResult.Ok); }
    public Task<PlatformResult> AddToPathAsync(string directory, bool perUser, CancellationToken ct) { Calls.Add($"AddToPath:{directory}"); return Task.FromResult(PlatformResult.Ok); }
    public Task<PlatformResult> RemoveFromPathAsync(string directory, bool perUser, CancellationToken ct) { Calls.Add($"RemoveFromPath:{directory}"); return RemoveFromPathResult; }
    public Task<PlatformResult> RemoveFromPathResult { get; set; } = Task.FromResult(PlatformResult.Ok);
    public Task<PlatformResult> DeleteRegistryValueAsync(RegistryHive hive, string keyPath, string name, bool perUser, CancellationToken ct) { Calls.Add($"DeleteRegistryValue:{hive}:{keyPath}:{name}"); return DeleteRegistryValueResult; }
    public Task<PlatformResult> DeleteRegistryValueResult { get; set; } = Task.FromResult(PlatformResult.Ok);
    public Task<PlatformResult> DeleteRegistryKeyAsync(RegistryHive hive, string keyPath, bool perUser, CancellationToken ct) { Calls.Add($"DeleteRegistryKey:{hive}:{keyPath}"); return DeleteRegistryKeyResult; }
    public Task<PlatformResult> DeleteRegistryKeyResult { get; set; } = Task.FromResult(PlatformResult.Ok);
    public Task<PlatformResult> ConfigureAutoStartAsync(AutoStartInfo info, CancellationToken ct) { Calls.Add($"ConfigureAutoStart:{info.AppId}"); return Task.FromResult(PlatformResult.Ok); }
    public Task<PlatformResult> RemoveAutoStartAsync(string appId, CancellationToken ct) { Calls.Add($"RemoveAutoStart:{appId}"); return Task.FromResult(PlatformResult.Ok); }
    public System.Collections.Generic.IReadOnlyList<System.Diagnostics.Process> GetRunningProcesses(string processName, string? installPath) => Array.Empty<System.Diagnostics.Process>();
    public Task<PlatformResult> TerminateProcessAsync(System.Diagnostics.Process process, TimeSpan timeout, CancellationToken ct) => Task.FromResult(PlatformResult.Ok);
    public Task<PlatformResult> UpdateShortcutAsync(string oldTarget, string newTarget, string shortcutPath, CancellationToken ct) => Task.FromResult(PlatformResult.Ok);
    public Task<PlatformResult> RegisterUninstallEntryAsync(UninstallEntryInfo info, CancellationToken ct) { Calls.Add($"RegisterUninstallEntry:{info.AppId}"); return Task.FromResult(PlatformResult.Ok); }
    public Task<PlatformResult> UnregisterUninstallEntryAsync(string appId, bool perUser, CancellationToken ct) { Calls.Add($"UnregisterUninstallEntry:{appId}"); return Task.FromResult(PlatformResult.Ok); }
}

internal sealed class StubFileSystem : IFileSystem
{
    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Directories { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, UnixFileMode> Modes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<FileSystemResult> SetUnixFileModeAsync(string path, UnixFileMode mode, CancellationToken ct)
    {
        Modes[path] = mode;
        return Task.FromResult(FileSystemResult.Ok());
    }

    public Task<FileSystemResult> CopyFileAsync(string source, string dest, bool overwrite, CancellationToken ct)
    {
        if (Files.TryGetValue(source, out var data)) Files[dest] = data;
        return Task.FromResult(FileSystemResult.Ok());
    }
    public Task<FileSystemResult> MoveFileAsync(string source, string dest, bool overwrite, CancellationToken ct)
    {
        if (Files.TryGetValue(source, out var data)) { Files[dest] = data; Files.Remove(source); }
        // A rename keeps the file's mode, as on a real file system.
        if (Modes.Remove(source, out var mode)) Modes[dest] = mode;
        return Task.FromResult(FileSystemResult.Ok());
    }
    public Task<FileSystemResult> DeleteFileAsync(string path, CancellationToken ct)
    {
        Files.Remove(path);
        return Task.FromResult(FileSystemResult.Ok());
    }
    public Task<FileSystemResult> CreateDirectoryAsync(string path, CancellationToken ct)
    {
        Directories.Add(path);
        return Task.FromResult(FileSystemResult.Ok());
    }
    public Task<FileSystemResult> DeleteDirectoryAsync(string path, bool recursive, CancellationToken ct)
    {
        Directories.Remove(path);
        return Task.FromResult(FileSystemResult.Ok());
    }
    public Task<FileSystemResult<byte[]>> ReadAllBytesAsync(string path, CancellationToken ct)
    {
        return Files.TryGetValue(path, out var data)
            ? Task.FromResult(FileSystemResult<byte[]>.Ok(data))
            : Task.FromResult(FileSystemResult<byte[]>.Fail(new FileSystemError(FileSystemErrorType.NotFound, $"not found: {path}")));
    }
    public Task<FileSystemResult> WriteAllBytesAsync(string path, byte[] data, CancellationToken ct)
    {
        Files[path] = data;
        return Task.FromResult(FileSystemResult.Ok());
    }
    public Task<FileSystemResult<Stream>> OpenReadAsync(string path, CancellationToken ct)
    {
        return Files.TryGetValue(path, out var data)
            ? Task.FromResult(FileSystemResult<Stream>.Ok(new MemoryStream(data, writable: false)))
            : Task.FromResult(FileSystemResult<Stream>.Fail(new FileSystemError(FileSystemErrorType.NotFound, $"not found: {path}")));
    }
    public Task<FileSystemResult<Stream>> OpenWriteAsync(string path, CancellationToken ct)
    {
        var capture = new CaptureStream(bytes => Files[path] = bytes);
        return Task.FromResult(FileSystemResult<Stream>.Ok(capture));
    }
    public bool Exists(string path) => Files.ContainsKey(path);
    public bool DirectoryExists(string path) => Directories.Contains(path);
    public Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        if (!Files.TryGetValue(path, out var data)) return Task.FromResult("");
        return Task.FromResult(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant());
    }
    public IEnumerable<string> EnumerateFiles(string path, string searchPattern = "*", bool recursive = false)
    {
        foreach (var key in Files.Keys)
        {
            if (!recursive && System.IO.Path.GetDirectoryName(key) != System.IO.Path.TrimEndingDirectorySeparator(path)) continue;
            if (recursive && !key.StartsWith(path, StringComparison.OrdinalIgnoreCase)) continue;
            yield return key;
        }
    }
    public long GetFileSize(string path) => Files.TryGetValue(path, out var data) ? data.Length : -1;

    private sealed class CaptureStream : MemoryStream
    {
        private readonly Action<byte[]> _onClose;
        public CaptureStream(Action<byte[]> onClose) => _onClose = onClose;
        protected override void Dispose(bool disposing)
        {
            if (disposing) _onClose(ToArray());
            base.Dispose(disposing);
        }
    }
}
