using Instella.Core.Platform;
using Instella.Core.Trust;

namespace Instella.Sdk;

/// <summary>
/// The current installation, read from its installed manifest (<c>.instella-manifest.json</c>).
/// </summary>
public sealed record InstellaInfo
{
    /// <summary>Display name of the application.</summary>
    public required string AppName { get; init; }

    /// <summary>Unique identifier of the application (e.g. "com.example.myapp").</summary>
    public required string AppId { get; init; }

    /// <summary>Installed version.</summary>
    public required Version Version { get; init; }

    /// <summary>
    /// The installation root: the directory holding the installed manifest and the Instella
    /// stub. It differs from <see cref="AppContext.BaseDirectory"/> when the app lives in a
    /// payload subdirectory.
    /// </summary>
    public required string InstallRoot { get; init; }

    /// <summary>Release channel the installation follows (from the installed manifest).</summary>
    public string Channel { get; init; } = "stable";

    /// <summary>Architecture the build was installed as.</summary>
    public Architecture? Architecture { get; init; }

    /// <summary>Installed for the current user only (true) or for all users (false).</summary>
    public bool IsPerUser { get; init; }

    /// <summary>Update server, or null when the installation has none.</summary>
    public string? ServerUrl { get; init; }

    /// <summary>Main executable, relative to <see cref="InstallRoot"/>.</summary>
    internal string ExecutableName { get; init; } = "";

    /// <summary>OS the installation targets.</summary>
    internal TargetPlatform Platform { get; init; }

    /// <summary>Publisher keys update releases must be signed with.</summary>
    internal IReadOnlyList<PublisherKey> TrustedKeys { get; init; } = [];

    /// <summary>The installer was built with <c>AllowUnsignedUpdates()</c>.</summary>
    internal bool AllowUnsignedUpdates { get; init; }

    /// <summary>The installer was built with <c>AllowInsecureServer()</c>.</summary>
    internal bool AllowInsecureServer { get; init; }

    /// <summary>The download token check-update sends: the override, else the installed one.</summary>
    internal string? DownloadToken { get; init; }
}

/// <summary>
/// The running app has no installed manifest near it: it was not installed by Instella (for
/// example started from the IDE). Use <see cref="InstellaClient.TryGetCurrentInfo"/> to handle
/// that as a normal state.
/// </summary>
public sealed class InstellaNotInstalledException(string message) : InvalidOperationException(message);

/// <summary>The updater process started by <see cref="InstellaClient.StartUpdaterAsync"/>.</summary>
/// <param name="ProcessId">Its process id. The updater waits for the calling app to exit before touching files.</param>
public sealed record UpdaterStartResult(int ProcessId);
