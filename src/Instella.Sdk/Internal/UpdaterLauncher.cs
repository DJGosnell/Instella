using System.Diagnostics;
using Instella.Core.Installation;
using Instella.Core.Update;

namespace Instella.Sdk.Internal;

/// <summary>
/// Starts the Instella stub (<c>instella --update …</c>) for an update, and <c>--recover</c>
/// after an interrupted one. Arguments travel as an argument list built by the shared
/// <see cref="UpdaterArgs"/>, so there is no string escaping to get wrong.
/// </summary>
internal static class UpdaterLauncher
{
    /// <summary>The stub staged in the install root (same name the installer uses).</summary>
    internal static string FindUpdaterPath(string installRoot) =>
        Path.Combine(installRoot, InstellaOwnedPaths.StubFileName);

    /// <summary>The updater arguments for <paramref name="update"/> of <paramref name="info"/>.</summary>
    internal static UpdaterArgs BuildArgs(UpdateInfo update, UpdateOptions options, InstellaInfo info, int parentPid) => new()
    {
        AppPath = info.InstallRoot,
        AppExecutable = info.ExecutableName,
        FromVersion = info.Version,
        ToVersion = update.Version,
        Channel = update.Channel,
        UsePatch = update.PatchAvailable && options.PreferPatch,
        PatchSha256 = update.PatchAvailable && options.PreferPatch ? update.PatchSha256 : null,
        AllowForceClose = options.AllowForceClose,
        GracefulTimeout = options.GracefulCloseTimeout,
        Restart = options.RestartAfterUpdate,
        RestartCountdown = options.RestartCountdown < TimeSpan.Zero ? TimeSpan.Zero : options.RestartCountdown,
        Silent = options.Silent,
        ParentPid = parentPid,
        ExtraArgs = options.AdditionalArgs ?? [],
    };

    /// <summary>
    /// Starts the updater and returns once it is running. <paramref name="ct"/> is observed only
    /// before the process starts: after that the operation is complete. There is no delay; the
    /// updater waits for this process (by PID) to exit before touching files.
    /// </summary>
    public static UpdaterStartResult Start(UpdateInfo update, UpdateOptions options, InstellaInfo info, CancellationToken ct)
    {
        var stub = FindUpdaterPath(info.InstallRoot);
        if (!File.Exists(stub))
            throw new InvalidOperationException(
                $"Instella stub not found at expected location: {stub}. Ensure the application was installed via an Instella installer.");

        var startInfo = new ProcessStartInfo(stub)
        {
            UseShellExecute = false,
            CreateNoWindow = options.Silent,
            WorkingDirectory = info.InstallRoot,
        };
        foreach (var arg in BuildArgs(update, options, info, Environment.ProcessId).ToArgumentList())
            startInfo.ArgumentList.Add(arg);

        ct.ThrowIfCancellationRequested();
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the updater process");
        return new UpdaterStartResult(process.Id);
    }

    /// <summary>
    /// Runs <c>instella --recover --silent</c> for <paramref name="installRoot"/> and returns its
    /// exit code. Silent means no UI other than the UAC prompt a machine-wide installation needs:
    /// the app starts recovery, so a user is present to answer it.
    /// </summary>
    public static async Task<int> RunRecoveryAsync(string installRoot, CancellationToken ct)
    {
        var stub = FindUpdaterPath(installRoot);
        if (!File.Exists(stub))
            throw new InvalidOperationException($"Instella stub not found at expected location: {stub}.");

        var startInfo = new ProcessStartInfo(stub) { UseShellExecute = false, WorkingDirectory = installRoot };
        foreach (var a in new[] { "--recover", "--path", installRoot, "--silent" })
            startInfo.ArgumentList.Add(a);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the recovery process");
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        return process.ExitCode;
    }
}
