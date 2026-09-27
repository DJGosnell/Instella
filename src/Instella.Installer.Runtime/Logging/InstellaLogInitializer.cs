using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Logging;
using Logsmith;
using InternalLog = Instella.Installer.Runtime.Logging.Internal.Log;

namespace Instella.Installer.Runtime.Logging;

/// <summary>
/// One-shot startup bootstrap for Logsmith in Standalone mode. Resolves the
/// minimum level from (CLI &gt; env &gt; builder &gt; default), registers the
/// default per-run file sink under <c>%TEMP%</c>, and bridges any
/// user-supplied <see cref="IInstellaLogSink"/> instances.
/// </summary>
internal static class InstellaLogInitializer
{
    public const string DefaultEnvironmentVariable = "INSTELLA_LOG_LEVEL";

    /// <summary>The log file of this run, or null when there is none; error messages name it.</summary>
    public static string? CurrentFilePath { get; private set; }

    public static void Initialize(
        LoggingBuilder? builder,
        string[]? cliArgs,
        string appId,
        string modeName)
    {
        builder ??= new LoggingBuilder();
        CurrentFilePath = null;
        var resolvedLevel = ResolveLevel(builder, cliArgs);

        LogManager.Initialize(cfg =>
        {
            cfg.MinimumLevel = LogsmithLoggerAdapter.MapToLogsmith(resolvedLevel);
            cfg.InternalErrorHandler = builder.InternalErrorHandler ?? (static _ => { });

            if (builder.FileEnabled)
            {
                var path = builder.FilePath ?? DefaultFilePath(appId, modeName);
                try
                {
                    var dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir))
                        Directory.CreateDirectory(dir);
                    cfg.AddFileSink(path);
                    CurrentFilePath = path;
                }
                catch (Exception ex)
                {
                    (builder.InternalErrorHandler ?? (static _ => { }))(ex);
                }
            }

            if (builder.ConsoleEnabled)
                cfg.AddConsoleSink();

            foreach (var sink in builder.Sinks)
                cfg.AddSink(new InstellaSinkBridge(sink));
        });

        InternalLog.LoggingInitialized(resolvedLevel.ToString(), modeName, appId);
    }

    public static ValueTask FlushAsync(TimeSpan? timeout = null)
        => LogManager.FlushAsync(timeout);

    public static ValueTask ShutdownAsync(TimeSpan? timeout = null)
        => LogManager.ShutdownAsync(timeout);

    internal static InstellaLogLevel ResolveLevel(LoggingBuilder builder, string[]? cliArgs)
    {
        if (cliArgs is not null && TryParseCliLevel(cliArgs, out var fromCli))
            return fromCli;

        if (builder.EnvironmentVariableName is { Length: > 0 } envName
            && TryParseEnvironmentLevel(envName, out var fromEnv))
            return fromEnv;

        if (builder.Level is { } fromBuilder)
            return fromBuilder;

#if DEBUG
        return InstellaLogLevel.Debug;
#else
        return InstellaLogLevel.Info;
#endif
    }

    internal static bool TryParseCliLevel(string[] args, out InstellaLogLevel level)
    {
        level = default;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            string? raw = null;
            if (arg == "--log-level" && i + 1 < args.Length)
                raw = args[i + 1];
            else if (arg.StartsWith("--log-level=", StringComparison.Ordinal))
                raw = arg["--log-level=".Length..];

            if (raw is null)
                continue;

            return TryParseLevelName(raw, out level);
        }
        return false;
    }

    internal static bool TryParseEnvironmentLevel(string environmentVariableName, out InstellaLogLevel level)
    {
        level = default;
        var raw = Environment.GetEnvironmentVariable(environmentVariableName);
        return !string.IsNullOrWhiteSpace(raw) && TryParseLevelName(raw, out level);
    }

    internal static bool TryParseLevelName(string raw, out InstellaLogLevel level)
    {
        switch (raw.Trim().ToLowerInvariant())
        {
            case "trace":
                level = InstellaLogLevel.Trace; return true;
            case "debug":
                level = InstellaLogLevel.Debug; return true;
            case "info":
            case "information":
                level = InstellaLogLevel.Info; return true;
            case "warn":
            case "warning":
                level = InstellaLogLevel.Warn; return true;
            case "error":
                level = InstellaLogLevel.Error; return true;
            case "critical":
            case "fatal":
                level = InstellaLogLevel.Critical; return true;
            default:
                level = default;
                return false;
        }
    }

    internal static string DefaultFilePath(string appId, string modeName)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var safeAppId = new string(appId.Where(c => !invalidChars.Contains(c)).ToArray());
        var safeMode = new string(modeName.Where(c => !invalidChars.Contains(c)).ToArray());
        if (string.IsNullOrEmpty(safeAppId)) safeAppId = "app";
        if (string.IsNullOrEmpty(safeMode)) safeMode = "run";
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        return Path.Combine(Path.GetTempPath(), $"instella-{safeMode}-{safeAppId}-{stamp}.log");
    }
}
