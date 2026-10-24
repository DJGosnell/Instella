using Instella.Core.Installation;
using Instella.Core.Update;

namespace Instella.Sdk.Internal;

/// <summary>
/// Starts the updater from inside a Windows service (<c>instella --update … --service-update</c>).
/// The updater stops the recorded services through the SCM, updates, and starts them again, so
/// it must outlive this process: it is started detached, without <c>--parent-pid</c>. Every
/// check runs first; the first one that fails is the refusal and nothing is started.
/// </summary>
internal static class ServiceUpdateLauncher
{
    /// <summary>Checks that a service update can run, then starts it; a refusal starts nothing.</summary>
    internal static async Task<ServiceUpdateStartResult> StartAsync(UpdateInfo update, InstellaInfo info, IServiceUpdateSystem system, CancellationToken ct)
    {
        if (info.ServiceNames is null)
            return ServiceUpdateStartResult.Refused(
                "the installation records no services field: it was made by an installer without Windows service support, " +
                "whose updater cannot stop the service; upgrade it with a newer installer first");
        if (info.ServiceNames.Count == 0)
            return ServiceUpdateStartResult.Refused("the installation records no Windows service");
        if (system.UnsupportedReason is { } unsupported)
            return ServiceUpdateStartResult.Refused(unsupported);
        if (!system.IsElevated)
            return ServiceUpdateStartResult.Refused(
                "the service is not elevated: a service updates itself only when it runs as LocalSystem or an administrator account");
        if (system.CheckCanWrite(info.InstallRoot) is { } writeError)
            return ServiceUpdateStartResult.Refused($"the service account cannot write to {info.InstallRoot}: {writeError}");

        string? serviceRefusal = null;
        foreach (var name in info.ServiceNames)
        {
            // Every service is checked; the first that fails is named.
            if (await system.CheckCanStopAndStartAsync(name, ct).ConfigureAwait(false) is { } error)
                serviceRefusal ??= $"the service account cannot stop and start service '{name}': {error}";
        }
        if (serviceRefusal is not null)
            return ServiceUpdateStartResult.Refused(serviceRefusal);

        var job = system.GetJobBreakaway();
        if (job == JobBreakaway.Forbidden)
            return ServiceUpdateStartResult.Refused(
                "the service runs in a job object that does not allow breakaway: the updater would end with the service");

        var stub = UpdaterLauncher.FindUpdaterPath(info.InstallRoot);
        if (!File.Exists(stub))
            return ServiceUpdateStartResult.Refused($"the Instella stub {InstellaOwnedPaths.StubFileName} is missing from {info.InstallRoot}");

        ct.ThrowIfCancellationRequested();
        try
        {
            var pid = system.StartDetached(stub, BuildArgs(update, info).ToArgumentList(), info.InstallRoot, job == JobBreakaway.Allowed);
            return ServiceUpdateStartResult.Started(pid);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ServiceUpdateStartResult.Refused($"the updater could not be started: {ex.Message}");
        }
    }

    /// <summary>
    /// The updater arguments for a service update: silent, no parent to wait for, no restart of the
    /// app (the updater starts the stopped services again) and no force-close from session 0.
    /// </summary>
    internal static UpdaterArgs BuildArgs(UpdateInfo update, InstellaInfo info) => new()
    {
        AppPath = info.InstallRoot,
        AppExecutable = info.ExecutableName,
        FromVersion = info.Version,
        ToVersion = update.Version,
        Channel = update.Channel,
        UsePatch = update.PatchAvailable,
        PatchSha256 = update.PatchAvailable ? update.PatchSha256 : null,
        AllowForceClose = false,
        Restart = false,
        Silent = true,
        ParentPid = null,
        ServiceUpdate = true,
    };

    /// <summary>Whether this process is the running process of one of the installation's recorded services.</summary>
    internal static async Task<bool> IsRunningAsServiceAsync(InstellaInfo info, IServiceUpdateSystem system, CancellationToken ct)
    {
        if (info.ServiceNames is not { Count: > 0 } names) return false;
        foreach (var name in names)
        {
            if (await system.GetServiceProcessIdAsync(name, ct).ConfigureAwait(false) == system.CurrentProcessId)
                return true;
        }
        return false;
    }
}
