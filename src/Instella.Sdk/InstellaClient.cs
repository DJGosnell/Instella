using System.Diagnostics.CodeAnalysis;
using Instella.Core.Installation;
using Instella.Core.Update;
using Instella.Core.Wire;
using Instella.Sdk.Internal;

namespace Instella.Sdk;

/// <summary>
/// Main entry point for the Instella SDK: reads the current installation, checks for updates,
/// starts the updater, and reports whether the app was just updated.
/// </summary>
public static class InstellaClient
{
    private static readonly Lazy<(InstellaInfo? Info, string? Reason)> _currentInfo = new(
        () => (ManifestLoader.TryLoad(AppContext.BaseDirectory, out var reason), reason),
        LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<IUpdateClient> _updateClient = new(() => new HttpUpdateClient(), LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<PostUpdateInfo?> _postUpdate = new(
        () => TryGetCurrentInfo(out var info) ? PostUpdateReader.Read(info.InstallRoot, info.AppId, DateTimeOffset.UtcNow) : null,
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// The update this start follows, or null. The updater leaves a marker in the installation
    /// after each update; the first start of the app by each user reports it once (the SDK
    /// records per user which update it reported). Check it on startup for post-update tasks.
    /// </summary>
    public static PostUpdateInfo? PostUpdate => _postUpdate.Value;

    /// <summary>
    /// Whether the application was just updated (<see cref="PostUpdate"/> is not null). Check this
    /// on startup to perform post-update tasks.
    /// </summary>
    public static bool IsPostUpdate => _postUpdate.Value is not null;

    /// <summary>
    /// The version the application was updated from, when <see cref="IsPostUpdate"/> is true.
    /// Useful for data migrations between versions.
    /// </summary>
    public static Version? PreviousVersion => _postUpdate.Value?.FromVersion;

    /// <summary>
    /// <see cref="UpdateOptions.AdditionalArgs"/> of the update this start follows, or empty. They
    /// arrive here, not on the command line: the updater restarts the app without arguments.
    /// </summary>
    public static IReadOnlyList<string> PostUpdateArguments => _postUpdate.Value?.Arguments ?? [];

    /// <summary>
    /// The current installation, or false when the app was not installed by Instella (for
    /// example started from the IDE) — a normal state, not an error.
    /// </summary>
    public static bool TryGetCurrentInfo([NotNullWhen(true)] out InstellaInfo? info)
    {
        info = _currentInfo.Value.Info;
        return info is not null;
    }

    /// <summary>The current installation.</summary>
    /// <exception cref="InstellaNotInstalledException">The app was not installed by Instella.</exception>
    public static InstellaInfo GetCurrentInfo() =>
        _currentInfo.Value.Info ?? throw new InstellaNotInstalledException(_currentInfo.Value.Reason ?? "not installed by Instella");

    /// <summary>
    /// A download token (<c>idt_…</c>) to send with <see cref="CheckForUpdateAsync(string?, CancellationToken)"/>
    /// instead of the one the installer was built with; null uses the installed one.
    /// </summary>
    /// <remarks>
    /// It affects only the SDK's own update check. The updater downloads with the token stored at
    /// install time, so that token must also have access to the package; per-customer tokens need
    /// per-customer installers. A token the server does not accept looks like "no update".
    /// </remarks>
    public static string? DownloadTokenOverride { get; set; }

    /// <summary>Checks the installation's own channel for an update.</summary>
    public static Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken ct) =>
        CheckForUpdateAsync(channel: null, ct);

    /// <summary>
    /// Checks <paramref name="channel"/> (null: the installation's channel) for an update. An
    /// update is reported only when its signed release verifies against the installation's
    /// publisher keys; a release that does not verify is <see cref="UpdateCheckStatus.Failed"/>.
    /// </summary>
    public static async Task<UpdateCheckResult> CheckForUpdateAsync(string? channel = null, CancellationToken ct = default)
    {
        if (!TryGetCurrentInfo(out var info))
            return UpdateCheckResult.NotInstalled(_currentInfo.Value.Reason ?? "not installed by Instella");

        string? checkChannel = null;
        if (channel is not null && !ChannelNames.TryNormalize(channel, out checkChannel))
            return UpdateCheckResult.Failed($"Invalid channel '{channel}': {ChannelNames.Rule}.");

        try
        {
            if (DownloadTokenOverride is { } token)
                info = info with { DownloadToken = token.Trim() };
            return await _updateClient.Value.CheckAsync(info, checkChannel ?? info.Channel, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return UpdateCheckResult.Failed($"Failed to check for updates: {ex.Message}");
        }
    }

    /// <summary>
    /// Starts the updater and returns once it is running. The caller must then exit promptly:
    /// the updater waits for this process (by PID) before touching any file.
    /// <paramref name="ct"/> is only observed before the updater starts.
    /// </summary>
    /// <exception cref="InstellaNotInstalledException">The app was not installed by Instella.</exception>
    /// <exception cref="InvalidOperationException">
    /// The Instella stub is missing or cannot be started, or this process is one of the
    /// installation's Windows services (use <see cref="StartServiceUpdateAsync"/> there).
    /// </exception>
    public static async Task<UpdaterStartResult> StartUpdaterAsync(UpdateInfo update, UpdateOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        ct.ThrowIfCancellationRequested();
        return await UpdaterLauncher.StartAsync(update, options ?? new UpdateOptions(), GetCurrentInfo(), ServiceSystem, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// For apps without shutdown logic: <see cref="StartUpdaterAsync"/>, then
    /// <see cref="Environment.Exit(int)"/> with code 0. Inside a Windows service it throws
    /// before exiting, like <see cref="StartUpdaterAsync"/>.
    /// </summary>
    public static async Task LaunchUpdaterAndExitAsync(UpdateInfo update, UpdateOptions? options = null, CancellationToken ct = default)
    {
        await StartUpdaterAsync(update, options, ct).ConfigureAwait(false);
        Environment.Exit(0);
    }

    /// <summary>
    /// Starts the update from inside a Windows service the installation records. The updater stops
    /// the recorded services through the Service Control Manager, updates the files, and starts the
    /// services again; this process keeps running until its service is stopped, and it must not
    /// exit by itself. The update is refused, and nothing started, unless the service runs elevated
    /// (LocalSystem or an administrator account), can write to the installation and can stop and
    /// start every recorded service, and the installation was made by an installer with service
    /// support. <paramref name="ct"/> is only observed before the updater starts.
    /// </summary>
    /// <returns>The started updater's process id, or the refusal's reason (log it).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="update"/> is null.</exception>
    public static Task<ServiceUpdateStartResult> StartServiceUpdateAsync(UpdateInfo update, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        ct.ThrowIfCancellationRequested();
        if (!TryGetCurrentInfo(out var info))
            return Task.FromResult(ServiceUpdateStartResult.Refused(_currentInfo.Value.Reason ?? "not installed by Instella"));
        return ServiceUpdateLauncher.StartAsync(update, info, ServiceSystem, ct);
    }

    /// <summary>
    /// Whether this process is one of the installation's recorded Windows services: then update with
    /// <see cref="StartServiceUpdateAsync"/>, not <see cref="StartUpdaterAsync"/>. False when the app
    /// was not installed by Instella, or off Windows.
    /// </summary>
    public static Task<bool> IsRunningAsServiceAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return TryGetCurrentInfo(out var info)
            ? ServiceUpdateLauncher.IsRunningAsServiceAsync(info, ServiceSystem, ct)
            : Task.FromResult(false);
    }

    /// <summary>What the service checks and the detached start ask the OS; tests replace it.</summary>
    internal static IServiceUpdateSystem ServiceSystem { get; set; } = new WindowsServiceUpdateSystem();

    /// <summary>
    /// Reports whether the installation was left half-updated by a crash or power loss in the
    /// middle of an update's commit. Reads only the transaction journal, not the installed
    /// manifest (which may be one of the files in flux). Call it at startup; on
    /// <see cref="InstallationHealth.InterruptedUpdate"/>, call <see cref="StartRecoveryAsync"/> and exit.
    /// </summary>
    public static InstallationHealth GetInstallationHealth() =>
        TransactionJournal.HasInterruptedCommit(InstallRoot)
            ? InstallationHealth.InterruptedUpdate
            : InstallationHealth.Healthy;

    /// <summary>
    /// Runs <c>instella --recover</c>, which puts the previous version back, and returns its exit
    /// code: 0 when the installation was recovered, 51 when the administrator prompt a
    /// machine-wide installation needs was declined, 23 when the interrupted update cannot be
    /// read. Exit the app afterwards and start it again.
    /// </summary>
    /// <exception cref="InvalidOperationException">The Instella stub is missing or cannot be started.</exception>
    public static Task<int> StartRecoveryAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return UpdaterLauncher.RunRecoveryAsync(InstallRoot, ct);
    }

    /// <summary>
    /// The installation root when the app was installed by Instella, else its own directory
    /// (health checks work for a freshly crashed tree whose manifest is unreadable).
    /// </summary>
    private static string InstallRoot =>
        ManifestLoader.FindInstallRoot(AppContext.BaseDirectory) ?? AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
}
