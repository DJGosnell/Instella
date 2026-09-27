using System;
using Logsmith;

namespace Instella.Installer.Runtime.Logging.Internal;

/// <summary>
/// Source-generated log methods for user code routed through
/// <see cref="Instella.Core.Logging.IInstellaLogger"/>. The category
/// <c>Instella.User</c> separates user-originated lines from installer-internal
/// diagnostics (<see cref="Log"/>) in log files and filters.
/// </summary>
[LogCategory("Instella.User")]
internal static partial class UserLog
{
    [LogMessage(LogLevel.Trace, "{message}")]
    public static partial void UserTrace(string message);

    [LogMessage(LogLevel.Debug, "{message}")]
    public static partial void UserDebug(string message);

    [LogMessage(LogLevel.Information, "{message}")]
    public static partial void UserInfo(string message);

    [LogMessage(LogLevel.Warning, "{message}")]
    public static partial void UserWarn(string message);

    [LogMessage(LogLevel.Error, "{message}")]
    public static partial void UserError(string message);

    [LogMessage(LogLevel.Error, "{message}")]
    public static partial void UserErrorWithException(string message, Exception exception);
}
