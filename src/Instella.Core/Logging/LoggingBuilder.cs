using System;
using System.Collections.Generic;

namespace Instella.Core.Logging;

/// <summary>
/// Fluent configuration object for the installer's logging pipeline. The
/// installer's runtime reads this at startup — the builder itself has no
/// dependency on any logging implementation so that applications consuming
/// <c>Instella.Core</c> (the SDK path) pay no logging cost.
/// </summary>
/// <remarks>
/// Level resolution priority is fixed at runtime-init time:
/// <list type="number">
///   <item>CLI flag <c>--log-level &lt;value&gt;</c></item>
///   <item>Environment variable named by <see cref="LevelFromEnvironment(string)"/></item>
///   <item>Builder's <see cref="WithLevel(InstellaLogLevel)"/></item>
///   <item>Default (<see cref="InstellaLogLevel.Info"/> in Release,
///         <see cref="InstellaLogLevel.Debug"/> in Debug builds).</item>
/// </list>
/// </remarks>
public sealed class LoggingBuilder
{
    private readonly List<IInstellaLogSink> _sinks = new();

    /// <summary>
    /// Minimum level set explicitly on the builder. <c>null</c> means defer to
    /// CLI / env / compile-time default.
    /// </summary>
    public InstellaLogLevel? Level { get; private set; }

    /// <summary>
    /// Name of the environment variable to poll for a minimum level, or
    /// <c>null</c> to disable env-var resolution entirely.
    /// </summary>
    public string? EnvironmentVariableName { get; private set; } = "INSTELLA_LOG_LEVEL";

    /// <summary>
    /// When <c>true</c>, a rolling file sink is opened automatically at
    /// <see cref="FilePath"/> (or a default under <c>%TEMP%</c>) when the
    /// installer starts. Default: <c>true</c>.
    /// </summary>
    public bool FileEnabled { get; private set; } = true;

    /// <summary>
    /// Explicit file sink path, or <c>null</c> to let the runtime pick a
    /// per-run path under <c>%TEMP%</c>.
    /// </summary>
    public string? FilePath { get; private set; }

    /// <summary>
    /// When <c>true</c>, a console sink is registered. Off by default — the
    /// installer's wizard uses its own progress surface for user-visible
    /// status, and a console sink would noisily interleave.
    /// </summary>
    public bool ConsoleEnabled { get; private set; }

    /// <summary>
    /// Handler invoked when the logging pipeline itself throws (sink write
    /// failure, formatter crash, etc.). <c>null</c> means swallow silently —
    /// logging NEVER fails an install.
    /// </summary>
    public Action<Exception>? InternalErrorHandler { get; private set; }

    /// <summary>User-registered sinks.</summary>
    public IReadOnlyList<IInstellaLogSink> Sinks => _sinks;

    /// <summary>Sets the minimum level (overridden by <c>--log-level</c> and the environment variable).</summary>
    public LoggingBuilder WithLevel(InstellaLogLevel level)
    {
        Level = level;
        return this;
    }

    /// <summary>Reads the minimum level from the named environment variable (default <c>INSTELLA_LOG_LEVEL</c>).</summary>
    public LoggingBuilder LevelFromEnvironment(string environmentVariableName = "INSTELLA_LOG_LEVEL")
    {
        EnvironmentVariableName = environmentVariableName;
        return this;
    }

    /// <summary>Ignores the environment variable when resolving the level.</summary>
    public LoggingBuilder DisableEnvironmentLevel()
    {
        EnvironmentVariableName = null;
        return this;
    }

    /// <summary>Writes the log file to <paramref name="path"/> instead of the default under <c>%TEMP%</c>.</summary>
    public LoggingBuilder File(string path)
    {
        FilePath = path;
        FileEnabled = true;
        return this;
    }

    /// <summary>Writes no log file.</summary>
    public LoggingBuilder NoFile()
    {
        FileEnabled = false;
        FilePath = null;
        return this;
    }

    /// <summary>Also writes log entries to the console.</summary>
    public LoggingBuilder Console(bool enabled = true)
    {
        ConsoleEnabled = enabled;
        return this;
    }

    /// <summary>Adds a custom sink that receives every entry.</summary>
    public LoggingBuilder AddSink(IInstellaLogSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _sinks.Add(sink);
        return this;
    }

    /// <summary>Called when the logging pipeline itself fails; logging never fails an install.</summary>
    public LoggingBuilder OnInternalError(Action<Exception> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        InternalErrorHandler = handler;
        return this;
    }
}
