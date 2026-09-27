using System;

namespace Instella.Core.Logging;

/// <summary>
/// Logger surface handed to custom install steps and pages as
/// <c>ctx.Log</c>. Implementations forward to the installer's backing log
/// pipeline (file sink, user sinks, console). Never throws — logging failures
/// are swallowed by the internal error handler configured in
/// <see cref="LoggingBuilder.OnInternalError(Action{Exception})"/>.
/// </summary>
public interface IInstellaLogger
{
    /// <summary>Logs at <see cref="InstellaLogLevel.Trace"/>.</summary>
    void Trace(string message);
    /// <summary>Logs at <see cref="InstellaLogLevel.Debug"/>.</summary>
    void Debug(string message);
    /// <summary>Logs at <see cref="InstellaLogLevel.Info"/>.</summary>
    void Info(string message);
    /// <summary>Logs at <see cref="InstellaLogLevel.Warn"/>.</summary>
    void Warn(string message);
    /// <summary>Logs at <see cref="InstellaLogLevel.Error"/>, with the exception if there is one.</summary>
    void Error(string message, Exception? exception = null);

    /// <summary>
    /// Push a scope segment that is rendered as a bracketed prefix on log
    /// messages emitted while the returned disposable is alive
    /// (<c>using (log.Scope("extract")) { log.Info("N files"); }</c> →
    /// <c>"[extract] N files"</c>). Nested scopes join with <c>/</c>. Prefer
    /// short segments describing operations (<c>"extract"</c>,
    /// <c>"step:register-assocs"</c>, <c>"prereq:vcredist"</c>).
    /// </summary>
    IDisposable Scope(string segment);

    /// <summary>True when messages at <paramref name="level"/> are written; use it to skip building expensive messages.</summary>
    bool IsEnabled(InstellaLogLevel level);
}
