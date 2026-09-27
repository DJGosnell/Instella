namespace Instella.Core.Platform;

/// <summary>
/// Information needed to configure an application for auto-start on login.
/// </summary>
/// <param name="AppId">Unique identifier for the application.</param>
/// <param name="ExecutablePath">Full path to the executable to start.</param>
/// <param name="Arguments">Optional command-line arguments to pass on startup.</param>
/// <param name="PerUser">The current user's <c>Run</c> key (true) or the machine's (false).</param>
public sealed record AutoStartInfo(string AppId, string ExecutablePath, string? Arguments, bool PerUser = true);
