using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Instella.Core.Diff;
using Instella.Core.FileSystem;
using Instella.Core.Installation;
using Instella.Core.Platform;
using Instella.Core.Trust;
using Instella.Core.Update;
using Instella.Core.Utilities;
using Instella.Core.Wire;
using Instella.Installer.Runtime.Core.Processes;
using Instella.Installer.Runtime.Core.Transactions;

namespace Instella.Installer.Runtime.Core.Update;

/// <summary>
/// Updates (or repairs) an installation as one <see cref="InstallTransaction"/>.
/// The installed manifest is the source of truth for the current state and the verified,
/// signed release for the target state; the server is trusted only for availability.
/// </summary>
/// <remarks>
/// Pipeline: recover → load installed state → resolve and verify the release → plan per
/// file → close the app → stage (patch or download, every file hashed against the release)
/// → commit (installed manifest last) → complete and restart.
/// </remarks>
internal sealed class UpdaterEngine
{
    private readonly UpdaterArgs _args;
    private readonly IUpdateDownloader _downloader;
    private readonly IPlatformServices _platform;
    private readonly IFileSystem _fs;
    private readonly IDiffEngine _diff;
    private readonly IReadOnlyList<TimeSpan>? _retryDelays;

    private CancellationTokenSource? _cts;
    private UpdaterState _currentState = UpdaterState.Initializing;
    private volatile bool _committing;
    private ZipArchive? _fullBuild;

    public event EventHandler<UpdaterState>? StateChanged;
    public event EventHandler<UpdaterProgress>? ProgressChanged;
    public event EventHandler<ProcessInfo>? ProcessDetected;
    public event EventHandler<string>? LogMessage;

    public UpdaterState CurrentState => _currentState;

    public UpdaterEngine(
        UpdaterArgs args,
        IUpdateDownloader downloader,
        IPlatformServices platform,
        IFileSystem fs,
        IDiffEngine diff)
        : this(args, downloader, platform, fs, diff, retryDelays: null)
    {
    }

    public UpdaterEngine(
        UpdaterArgs args,
        IUpdateDownloader downloader,
        IPlatformServices platform,
        IFileSystem fs,
        IDiffEngine diff,
        IReadOnlyList<TimeSpan>? retryDelays)
    {
        _args = args;
        _downloader = downloader;
        _platform = platform;
        _fs = fs;
        _diff = diff;
        _retryDelays = retryDelays;
    }

    /// <summary>Runs the update. Never throws; the outcome and exit code are in the result.</summary>
    public async Task<UpdateResult> RunAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;
        InstallTransaction? txn = null;

        try
        {
            SetState(UpdaterState.Initializing);

            // 1. Recover: finish or undo anything a previous run left half-done.
            if (await InstallTransaction.RecoverAsync(_fs, _args.AppPath, token, retryDelays: _retryDelays))
                Log("Rolled back an interrupted update before starting");

            // 2. Load state.
            var installed = await new InstallManifestWriter(_fs).ReadAsync(_args.AppPath, token);
            if (installed is null)
                return Fail(UpdateFailure.BeforeCommit, "no readable installed manifest in the install directory");
            if (!AppVersions.Equal(installed.Version, _args.FromVersion))
                return Fail(UpdateFailure.BeforeCommit,
                    $"installation changed since the update was offered (installed {installed.Version}, expected {_args.FromVersion})");

            var target = _args.Repair ? installed.Version : _args.ToVersion;
            Log(_args.Repair ? $"Repairing v{target}" : $"Updating v{installed.Version} -> v{target}");

            // 3. Resolve and verify the target release.
            SetState(UpdaterState.Verifying);
            var release = await ResolveReleaseAsync(installed, target, token);

            // 4. Plan. Downloads are bounded by what the signed release allows.
            if (release.Zip is null)
                _downloader.SetReleaseSize(release.Files.Sum(f => f.Size), release.Files.Count);
            var plan = await PlanAsync(installed, release, token);
            Log($"Plan: {plan.Unchanged} unchanged, {plan.Patch.Count} patch, {plan.Download.Count} download, {plan.Delete.Count} delete");

            // 5. Close the app.
            if (!await CloseAppAsync(installed, token))
                return Fail(UpdateFailure.AppWouldNotClose, "the application would not close");

            // 6. Stage.
            txn = await InstallTransaction.BeginAsync(
                _fs, _args.AppPath, _args.Repair ? TxnKind.Repair : TxnKind.Update, installed.Version, target,
                token, retryDelays: _retryDelays);
            await StageAsync(txn, installed, release, plan, token);

            // 7. Commit: from here on the operation ignores cancellation.
            SetState(UpdaterState.Finalizing);
            _committing = true;
            try
            {
                await txn.CommitAsync();
            }
            catch (Exception commitEx)
            {
                Log($"Commit failed: {commitEx.Message}");
                SetState(UpdaterState.RollingBack);
                try
                {
                    await txn.RollbackAsync();
                    await txn.CompleteAsync();
                    SetState(UpdaterState.Failed);
                    return Fail(UpdateFailure.RolledBack, $"{commitEx.Message} (rolled back)");
                }
                catch (Exception rollbackEx)
                {
                    SetState(UpdaterState.Failed);
                    return Fail(UpdateFailure.RollbackFailed,
                        $"{commitEx.Message}. Rollback also failed: {rollbackEx.Message}. Run the installer with --recover.");
                }
            }

            // 8. Complete, then leave the post-update marker. The caller restarts the app (the
            // update window first shows the result), always without elevation.
            await txn.CompleteAsync();
            SetState(UpdaterState.Completed);
            Log(_args.Repair ? "Repair completed" : "Update completed");
            await UpdateInstalledAppsEntryAsync(installed, release);
            if (!_args.Repair && !AppVersions.Equal(installed.Version, release.Version))
                await WritePostUpdateMarkerAsync(installed, release);
            return UpdateResult.Succeeded() with { ExecutablePath = SafePath.Combine(_args.AppPath, installed.ExecutableName) };
        }
        catch (OperationCanceledException) when (!_committing)
        {
            await AbandonAsync(txn);
            SetState(UpdaterState.Cancelled);
            Log("Update cancelled");
            return UpdateResult.Cancelled();
        }
        catch (HttpRequestException ex)
        {
            await AbandonAsync(txn);
            return Fail(UpdateFailure.Unreachable, $"server unreachable: {ex.Message}");
        }
        catch (UnsupportedJournalException ex)
        {
            return Fail(UpdateFailure.RollbackFailed, ex.Message);   // exit 23; nothing was touched
        }
        catch (Exception ex)
        {
            await AbandonAsync(txn);
            return Fail(UpdateFailure.BeforeCommit, ex.Message);
        }
        finally
        {
            _fullBuild?.Dispose();
            _fullBuild = null;
        }
    }

    /// <summary>Cancels the update. Has no effect once commit has started.</summary>
    public void Cancel()
    {
        if (!_committing) _cts?.Cancel();
    }

    private async Task<TargetRelease> ResolveReleaseAsync(InstalledManifest installed, Version target, CancellationToken ct)
    {
        var os = PlatformStrings.Os(installed.Platform);
        var arch = PlatformStrings.Arch(installed.Architecture ?? ArchitectureExtensions.Current);

        if (installed.TrustedKeys is { Count: > 0 } keys)
        {
            var signed = await _downloader.GetReleaseAsync(ct)
                ?? throw new UpdateTrustException($"v{target} has no signed release on the server");
            var manifest = ReleaseVerifier.Verify(signed, new TrustPolicy(
                keys, installed.AppId, os, arch,
                MustBeNewerThan: _args.Repair ? null : installed.Version,
                MustEqual: target)
            {
                // A repair reinstalls the installed version whatever channel it came from.
                Channel = _args.Repair ? null : _args.Channel,
            });
            return new TargetRelease(manifest.Version, manifest.Files, ReleaseVerifier.KeysAfter(manifest, keys), Zip: null,
                manifest.Channel);
        }

        if (!installed.AllowUnsignedUpdates)
            throw new UpdateTrustException("the installation trusts no publisher keys, so an update cannot be verified");

        // Development escape hatch: no signature to check, so the file list comes from the
        // full build itself. Files are still hashed while staging, which catches corruption.
        Log("AllowUnsignedUpdates: the release is not verified against a publisher key");
        var zipStream = await _downloader.DownloadFullBuildAsync(ct);
        var zip = _fullBuild = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: false);
        var files = new List<ReleaseFile>();
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;
            if (!SafePath.TryNormalizeRelative(entry.FullName, out var path, out var why))
                throw new UnsafePathException(entry.FullName, why);
            await using var s = entry.Open();
            var sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(s, ct));
            files.Add(new ReleaseFile(path, entry.Length, sha, ((entry.ExternalAttributes >> 16) & 0b001_001_001) != 0));
        }
        return new TargetRelease(target, files, installed.TrustedKeys ?? [], zip, _args.Channel);
    }

    private async Task<UpdatePlan> PlanAsync(InstalledManifest installed, TargetRelease release, CancellationToken ct)
    {
        var recorded = installed.Files.ToDictionary(f => f.RelativePath.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase);
        var patches = new Dictionary<string, PatchedFile>(StringComparer.OrdinalIgnoreCase);
        if (_args.UsePatch && !_args.Repair && release.Zip is null)
        {
            try
            {
                var patchManifest = await _downloader.DownloadPatchManifestAsync(ct);
                if (patchManifest is { FormatVersion: > PatchManifest.CurrentFormatVersion })
                    throw new NotSupportedException(
                        $"patch manifest format {patchManifest.FormatVersion} is newer than this updater reads ({PatchManifest.CurrentFormatVersion})");
                foreach (var p in patchManifest?.PatchedFiles ?? [])
                    patches[p.RelativePath.Replace('\\', '/')] = p;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Patches are purely an optimisation: any problem with the recipe (unreachable,
                // not JSON, a format this version can't read) means downloading in full.
                Log($"Patch manifest unusable ({ex.GetType().Name}: {ex.Message}); downloading changed files in full");
                patches.Clear();
            }
        }

        var plan = new UpdatePlan();
        foreach (var file in release.Files)
        {
            ct.ThrowIfCancellationRequested();
            var live = SafePath.Combine(_args.AppPath, file.Path);
            var liveHash = _fs.Exists(live) ? await _fs.ComputeSha256Async(live, ct) : null;

            // "Unchanged" means the bytes on disk already are the release's: a file whose
            // on-disk hash drifted from its record is always replaced, which is what repair is.
            if (liveHash is not null && string.Equals(liveHash, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                plan.Unchanged++;
                continue;
            }

            // A patch applies only to the exact old bytes the installed manifest recorded.
            if (liveHash is not null && patches.TryGetValue(file.Path, out var patch)
                && recorded.TryGetValue(file.Path, out var old)
                && string.Equals(liveHash, old.Sha256, StringComparison.OrdinalIgnoreCase))
                plan.Patch.Add((file, patch));
            else
                plan.Download.Add(file);
        }

        // Deletions are computed locally, never taken from the server.
        var targetPaths = release.Files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in recorded.Keys)
        {
            if (!targetPaths.Contains(path) && !InstellaOwnedPaths.IsOwned(path) && SafePath.IsSafeRelative(path))
                plan.Delete.Add(path);
        }
        return plan;
    }

    private async Task StageAsync(
        InstallTransaction txn, InstalledManifest installed, TargetRelease release, UpdatePlan plan, CancellationToken ct)
    {
        var total = plan.Patch.Count + plan.Download.Count;
        var done = 0;

        SetState(UpdaterState.ApplyingPatch);
        foreach (var (file, patch) in plan.Patch)
        {
            ct.ThrowIfCancellationRequested();
            Report(UpdaterState.ApplyingPatch, ++done, total, file.Path);
            if (!await TryStagePatchAsync(txn, file, patch, ct))
                plan.Download.Add(file);
        }

        SetState(UpdaterState.Downloading);
        foreach (var file in plan.Download)
        {
            ct.ThrowIfCancellationRequested();
            Report(UpdaterState.Downloading, Math.Min(++done, total), total, file.Path);
            if (release.Zip is { } zip)
            {
                var entry = zip.Entries.First(e => SafePath.TryNormalizeRelative(e.FullName, out var p, out _) && p == file.Path);
                await using var s = entry.Open();
                await txn.StageFileAsync(file.Path, s, file.Sha256, file.Size, file.Executable, ct);
            }
            else
            {
                await using var s = await _downloader.DownloadFileAsync(file.Path, ct);
                await txn.StageFileAsync(file.Path, s, file.Sha256, file.Size, file.Executable, ct);
            }
        }

        foreach (var path in plan.Delete)
            txn.Delete(path);

        var updated = installed with
        {
            Version = release.Version,
            // The release lists payload files only; Instella-owned files the installer put
            // there (the app icon) stay recorded so uninstall still removes them.
            Files = release.Files.Select(f => new InstalledFile(f.Path, f.Sha256, f.Size))
                .Concat(installed.Files.Where(f => InstellaOwnedPaths.IsOwned(f.RelativePath)))
                .ToList(),
            TrustedKeys = release.TrustedKeys.Count > 0 ? release.TrustedKeys : installed.TrustedKeys,
            // An app that checks another channel on purpose and updates from it now follows that
            // channel (InstellaInfo.Channel, later checks). A repair keeps the recorded one.
            Channel = _args.Repair ? installed.Channel : release.Channel,
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(updated, InstalledManifestJsonContext.Default.InstalledManifest);
        await txn.StageOwnedFileAsync(InstellaOwnedPaths.InstalledManifest, new MemoryStream(json), executable: false, ct);

        SetState(UpdaterState.Verifying);
        await txn.VerifyAsync(ct);
    }

    /// <summary>
    /// Applies one patch into the stage. The patch archive is an unsigned recipe: the output is
    /// hashed against the signed release, and any failure falls back to a full download.
    /// </summary>
    private async Task<bool> TryStagePatchAsync(InstallTransaction txn, ReleaseFile file, PatchedFile patch, CancellationToken ct)
    {
        try
        {
            // A patch is never bigger than the file it produces plus some slack.
            await using var patchStream = await _downloader.OpenPatchEntryAsync(patch.PatchSha256, file.Size + 1024 * 1024, ct);
            var patchHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(patchStream, ct));
            if (!string.Equals(patchHash, patch.PatchSha256, StringComparison.OrdinalIgnoreCase))
                throw new UpdateTrustException("patch blob does not match its name");
            patchStream.Position = 0;

            var open = await _fs.OpenReadAsync(SafePath.Combine(_args.AppPath, file.Path), ct);
            if (!open.Success || open.Value is null)
                throw new IOException(open.Error?.Message ?? "cannot read the installed file");
            await using var oldFile = open.Value;

            await txn.StageFileAsync(file.Path,
                (output, token) => _diff.ApplyPatchAsync(oldFile, patchStream, output, ct: token, maxOutputSize: file.Size),
                file.Sha256, file.Size, file.Executable, ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log($"Patch for '{file.Path}' not usable ({ex.Message}); downloading it in full");
            return false;
        }
    }

    /// <summary>Test hook: finds processes using the app's files; null uses Restart Manager.</summary>
    internal ILockingProcessFinder? ProcessFinder { get; init; }

    /// <summary>
    /// Asks the user about programs that would not close; null (silent) fails the update
    /// unless <see cref="UpdaterArgs.AllowForceClose"/>.
    /// </summary>
    internal AppRunningPrompt? AppRunningPrompt { get; init; }

    /// <summary>
    /// Asks every program using the app's files to close (not only copies of the app's own
    /// executable); ends survivors only when allowed or when the user says so.
    /// </summary>
    private async Task<bool> CloseAppAsync(InstalledManifest installed, CancellationToken ct)
    {
        // The app that launched the updater exits on its own once the updater is running
        // (the SDK passes its PID); wait for that before asking anything else to close.
        if (_args.ParentPid is { } parentPid)
            await WaitForParentAsync(parentPid, ct);

        var gate = new RunningAppGate(_platform, new EngineLog(this), ProcessFinder, _fs) { CloseTimeout = TimeSpan.FromSeconds(2) };
        var running = Closable(gate, installed);
        if (running.Count == 0) return true;

        SetState(UpdaterState.WaitingForProcessClose);
        foreach (var b in running)
        {
            try
            {
                using var p = Process.GetProcessById(b.Id);
                ProcessDetected?.Invoke(this, new ProcessInfo { ProcessId = p.Id, ProcessName = p.ProcessName, WindowTitle = WindowTitleOf(p) });
                p.CloseMainWindow();
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { /* already exited */ }
        }

        var deadline = DateTime.UtcNow + _args.GracefulTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (Closable(gate, installed).Count == 0) return true;
            await Task.Delay(250, ct);
        }

        return await gate.EnsureClosedAsync(installed.AppName, _args.AppPath, installed.ExecutableName,
            _args.AllowForceClose, AppRunningPrompt, ct);
    }

    private List<LockingProcess> Closable(RunningAppGate gate, InstalledManifest installed) =>
        gate.FindBlockers(_args.AppPath, installed.ExecutableName).Where(b => b.CanClose).ToList();

    private sealed class EngineLog(UpdaterEngine engine) : Instella.Core.Logging.IInstellaLogger, IDisposable
    {
        public void Trace(string message) { }
        public void Debug(string message) { }
        public void Info(string message) => engine.Log(message);
        public void Warn(string message) => engine.Log(message);
        public void Error(string message, Exception? exception = null) => engine.Log(message);
        public IDisposable Scope(string segment) => this;
        public void Dispose() { }
        public bool IsEnabled(Instella.Core.Logging.InstellaLogLevel level) => level >= Instella.Core.Logging.InstellaLogLevel.Info;
    }

    private async Task WaitForParentAsync(int pid, CancellationToken ct)
    {
        try
        {
            using var parent = Process.GetProcessById(pid);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_args.GracefulTimeout);
            Log($"Waiting for the application (PID {pid}) to exit");
            await parent.WaitForExitAsync(timeout.Token);
        }
        catch (ArgumentException)
        {
            // Already exited.
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Still running after the grace period: handled like any other running copy.
        }
    }

    /// <summary>
    /// Settings &gt; Apps shows the installer's DisplayVersion and EstimatedSize: set them to the
    /// new version. Only an entry the install registered is touched; a failure is logged, since
    /// the update itself is complete.
    /// </summary>
    private async Task UpdateInstalledAppsEntryAsync(InstalledManifest installed, TargetRelease release)
    {
        if (!installed.HasUninstallEntry || installed.Platform != TargetPlatform.Windows) return;
        var hive = installed.InstalledPerUser ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;
        var key = UninstallEntryKeys.PathFor(installed.AppId);
        var sizeKb = (int)Math.Min(int.MaxValue, release.Files.Sum(f => f.Size) / 1024);
        var version = await _platform.WriteRegistryValueAsync(hive, key, "DisplayVersion", InstellaRegistryValueKind.String,
            release.Version.ToString(), installed.InstalledPerUser, CancellationToken.None);
        var size = await _platform.WriteRegistryValueAsync(hive, key, "EstimatedSize", InstellaRegistryValueKind.DWord,
            sizeKb, installed.InstalledPerUser, CancellationToken.None);
        if (!version.Success || !size.Success)
            Log($"Could not update the Installed Apps entry: {version.Error ?? size.Error}");
    }

    /// <summary>
    /// The marker the restarted app reads through the SDK (<see cref="PostUpdateMarker"/>). It
    /// carries <see cref="UpdaterArgs.ExtraArgs"/>, which never go on the relaunch command line.
    /// A failure is a warning: the update itself succeeded.
    /// </summary>
    private async Task WritePostUpdateMarkerAsync(InstalledManifest installed, TargetRelease release)
    {
        try
        {
            await PostUpdateMarkers.WriteAsync(_fs, _args.AppPath, new PostUpdateMarker
            {
                UpdateId = Guid.NewGuid().ToString("N"),
                AppId = installed.AppId,
                FromVersion = installed.Version,
                ToVersion = release.Version,
                Channel = release.Channel,
                CompletedAt = DateTimeOffset.UtcNow,
                Arguments = _args.ExtraArgs,
            }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log($"Warning: could not write the post-update marker: {ex.Message}");
        }
    }

    private async Task AbandonAsync(InstallTransaction? txn)
    {
        // A committed transaction is the new version; never undo it here.
        if (txn is null || txn.State == TxnState.Committed) return;
        try
        {
            await txn.RollbackAsync();
            await txn.CompleteAsync();
        }
        catch (Exception ex)
        {
            Log($"Could not clean up the staged update ({ex.Message}); it is removed on the next run");
        }
    }

    private UpdateResult Fail(UpdateFailure failure, string message)
    {
        Log($"Update failed: {message}");
        SetState(UpdaterState.Failed, message);
        return UpdateResult.Failed(message, failure);
    }

    private void Report(UpdaterState state, int done, int total, string file) =>
        ProgressChanged?.Invoke(this, new UpdaterProgress
        {
            State = state,
            Percentage = total == 0 ? 100 : done * 100.0 / total,
            StatusText = $"{GetDefaultStatusText(state)} ({done}/{total})",
            CurrentFile = file,
            FilesProcessed = done,
            TotalFiles = total,
        });

    private static string? WindowTitleOf(Process process)
    {
        try { return string.IsNullOrEmpty(process.MainWindowTitle) ? null : process.MainWindowTitle; }
        catch { return null; }
    }

    private void SetState(UpdaterState state, string? statusText = null)
    {
        _currentState = state;
        StateChanged?.Invoke(this, state);
        ProgressChanged?.Invoke(this, UpdaterProgress.ForState(state, statusText ?? GetDefaultStatusText(state)));
    }

    private void Log(string message) => LogMessage?.Invoke(this, message);

    private static string GetDefaultStatusText(UpdaterState state) => state switch
    {
        UpdaterState.Initializing => "Initializing...",
        UpdaterState.WaitingForProcessClose => "Waiting for application to close...",
        UpdaterState.BackingUp => "Creating backup...",
        UpdaterState.Downloading => "Downloading update...",
        UpdaterState.Verifying => "Verifying...",
        UpdaterState.ApplyingPatch => "Applying patches...",
        UpdaterState.Extracting => "Extracting files...",
        UpdaterState.Finalizing => "Installing...",
        UpdaterState.Completed => "Update complete!",
        UpdaterState.Failed => "Update failed",
        UpdaterState.Cancelled => "Update cancelled",
        UpdaterState.RollingBack => "Rolling back...",
        _ => ""
    };

    private sealed record TargetRelease(
        Version Version, IReadOnlyList<ReleaseFile> Files, IReadOnlyList<PublisherKey> TrustedKeys, ZipArchive? Zip,
        string Channel);

    private sealed class UpdatePlan
    {
        public int Unchanged;
        public List<(ReleaseFile File, PatchedFile Patch)> Patch { get; } = [];
        public List<ReleaseFile> Download { get; } = [];
        public List<string> Delete { get; } = [];
    }
}
