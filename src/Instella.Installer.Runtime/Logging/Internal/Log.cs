using Logsmith;

namespace Instella.Installer.Runtime.Logging.Internal;

/// <summary>
/// Source-generated log methods for installer-internal events under the
/// <c>Instella</c> category. Later phases add messages covering step
/// execution, rollback, payload extraction, registry writes, etc.
/// </summary>
[LogCategory("Instella")]
internal static partial class Log
{
    [LogMessage(LogLevel.Information, "Logging initialized at level {level} (mode={mode}, appId={appId})")]
    public static partial void LoggingInitialized(string level, string mode, string appId);
}
