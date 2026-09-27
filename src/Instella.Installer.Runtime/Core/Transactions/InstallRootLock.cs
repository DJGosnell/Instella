using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Instella.Installer.Runtime.Runners;

namespace Instella.Installer.Runtime.Core.Transactions;

/// <summary>
/// One Instella process per install folder at a time: opening Manage or Uninstall while the
/// updater commits would otherwise run recovery, which rolls the live commit back halfway through
/// its renames.
/// </summary>
/// <remarks>
/// <para>A named system mutex, <c>Global\Instella-{sha256(lower(root))[..32]}</c>, visible across
/// sessions (an elevated child, an RDP user). A lock file inside the folder would make an empty
/// target non-empty, which breaks first installs.</para>
/// <para>A mutex is owned by a thread, and a hold spans <c>await</c>s, so each held lock has a
/// dedicated thread that creates the mutex, waits on it, parks, and releases it on that same
/// thread. <see cref="UnauthorizedAccessException"/> (a mutex an elevated process created) counts
/// as busy; <see cref="AbandonedMutexException"/> (the previous owner crashed) counts as acquired,
/// and recovery then cleans up after the crash.</para>
/// <para>Re-entrant within a process: Manage and the in-process updater it drives, or the
/// resolver and then the step executor, share one hold (reference counted).</para>
/// </remarks>
internal sealed class InstallRootLock : IAsyncDisposable
{
    private static readonly Dictionary<string, Holder> Held = new(StringComparer.Ordinal);
    private static readonly object Gate = new();

    /// <summary>Test seam: every acquire gets its own mutex hold, so one process can act as two owners.</summary>
    internal static bool ForceSeparateOwner { get; set; }

    private readonly Holder _holder;
    private readonly string _name;
    private int _disposed;

    private InstallRootLock(Holder holder, string name)
    {
        _holder = holder;
        _name = name;
    }

    /// <summary>How long a silent run waits for another Instella process to finish.</summary>
    public static readonly TimeSpan SilentTimeout = TimeSpan.FromSeconds(60);

    /// <summary>How long an interactive run waits before telling the user.</summary>
    public static readonly TimeSpan InteractiveTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The message shown or logged when the folder is busy.</summary>
    public static string BusyMessage(string appName) =>
        $"Another setup, update or uninstall of {appName} is running. Wait for it to finish, then try again.";

    /// <summary>
    /// Takes the lock with the default wait for the run (60 s silent, 10 s interactive, or
    /// <paramref name="timeout"/>). When the folder stays busy it logs <see cref="BusyMessage"/>
    /// and returns null: the caller exits 52.
    /// </summary>
    public static async Task<InstallRootLock?> AcquireOrReportAsync(
        string root, bool silent, TimeSpan? timeout, string appName, Instella.Core.Logging.IInstellaLogger log, CancellationToken ct)
    {
        var held = await TryAcquireAsync(root, timeout ?? (silent ? SilentTimeout : InteractiveTimeout), ct).ConfigureAwait(false);
        if (held is null) log.Error($"'{root}': {BusyMessage(appName)}");
        return held;
    }

    /// <summary>The mutex name for <paramref name="root"/>.</summary>
    internal static string MutexName(string root)
    {
        var normalized = InstallPaths.TryNormalize(root, requireRooted: false, out var n, out _) ? n : root;
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized.ToLowerInvariant())));
        return @"Global\Instella-" + hash[..32];
    }

    /// <summary>
    /// Takes the lock for <paramref name="root"/>, waiting up to <paramref name="timeout"/>.
    /// Null when another process holds it.
    /// </summary>
    public static async Task<InstallRootLock?> TryAcquireAsync(string root, TimeSpan timeout, CancellationToken ct)
    {
        var name = MutexName(root);
        if (!ForceSeparateOwner)
        {
            lock (Gate)
            {
                if (Held.TryGetValue(name, out var existing))
                {
                    existing.Count++;
                    return new InstallRootLock(existing, name);
                }
            }
        }

        var holder = new Holder(name);
        if (!await holder.AcquireAsync(timeout, ct).ConfigureAwait(false))
            return null;
        if (!ForceSeparateOwner)
        {
            lock (Gate)
                Held[name] = holder;
        }
        return new InstallRootLock(holder, name);
    }

    /// <summary>Decrements the hold; the owner thread releases the mutex at zero.</summary>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
        var release = false;
        lock (Gate)
        {
            if (--_holder.Count == 0)
            {
                release = true;
                if (Held.TryGetValue(_name, out var h) && ReferenceEquals(h, _holder))
                    Held.Remove(_name);
            }
        }
        if (release) _holder.Release();
        return ValueTask.CompletedTask;
    }

    /// <summary>One held mutex and the thread that owns it.</summary>
    private sealed class Holder(string name)
    {
        private readonly ManualResetEventSlim _release = new(false);
        private readonly TaskCompletionSource<bool> _acquired = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Count = 1;

        public async Task<bool> AcquireAsync(TimeSpan timeout, CancellationToken ct)
        {
            var thread = new Thread(() => Own(timeout, ct)) { IsBackground = true, Name = "Instella install-root lock" };
            thread.Start();
            return await _acquired.Task.ConfigureAwait(false);
        }

        public void Release() => _release.Set();

        private void Own(TimeSpan timeout, CancellationToken ct)
        {
            Mutex mutex;
            try
            {
                mutex = new Mutex(initiallyOwned: false, name);
            }
            catch (UnauthorizedAccessException)
            {
                // Created by a process this one cannot open (elevated): it is busy.
                _acquired.TrySetResult(false);
                return;
            }
            catch (Exception ex)
            {
                _acquired.TrySetException(ex);
                return;
            }

            using (mutex)
            {
                bool owned;
                try
                {
                    owned = WaitHandle.WaitAny([mutex, ct.WaitHandle], timeout) == 0;
                }
                catch (AbandonedMutexException)
                {
                    owned = true;   // the previous owner crashed; recovery cleans up after it
                }
                if (!owned)
                {
                    if (ct.IsCancellationRequested) _acquired.TrySetCanceled(ct);
                    else _acquired.TrySetResult(false);
                    return;
                }

                _acquired.TrySetResult(true);
                _release.Wait();
                mutex.ReleaseMutex();
            }
        }
    }
}
