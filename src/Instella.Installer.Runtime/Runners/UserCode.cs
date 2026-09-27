using System;
using Instella.Core.Logging;

namespace Instella.Installer.Runtime.Runners;

/// <summary>
/// Runs a callback the installer's author wrote (<c>WithInstallPath</c>, page <c>When</c>,
/// <c>OnValidate</c>, <c>ContinueWhen</c>, widget <c>Visible</c> / <c>Enabled</c>): an
/// exception is logged and replaced by a safe answer, never allowed to end the process.
/// Inside a window procedure an escaping exception would terminate it at once.
/// </summary>
internal static class UserCode
{
    /// <summary>The latest failure on this thread, for the page to show; null when none.</summary>
    [ThreadStatic] private static string? t_lastError;

    /// <summary>The latest failure on this thread, for the page to show; null when none.</summary>
    public static string? LastError
    {
        get => t_lastError;
        set => t_lastError = value;
    }

    /// <summary>Returns <paramref name="callback"/>'s result, or <paramref name="onError"/> when it throws.</summary>
    /// <param name="callback">The author's code.</param>
    /// <param name="what">What it is, for the log and the message: "page 'licence' ContinueWhen".</param>
    /// <param name="log">Where to log a failure; null records it in <see cref="LastError"/> only.</param>
    /// <param name="onError">The answer when it throws.</param>
    public static T Run<T>(Func<T> callback, string what, IInstellaLogger? log, T onError)
    {
        try
        {
            return callback();
        }
        catch (Exception ex)
        {
            log?.Error($"{what} threw: {ex}");
            LastError = $"{what} failed: {ex.Message}";
            return onError;
        }
    }
}
