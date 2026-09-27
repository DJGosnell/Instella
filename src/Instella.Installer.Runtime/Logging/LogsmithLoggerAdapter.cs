using System;
using System.Collections.Generic;
using System.Threading;
using Instella.Core.Logging;
using Logsmith;
using InternalUserLog = Instella.Installer.Runtime.Logging.Internal.UserLog;

namespace Instella.Installer.Runtime.Logging;

/// <summary>
/// Adapts the user-facing <see cref="IInstellaLogger"/> surface onto Logsmith
/// source-generated log methods under the <c>Instella.User</c> category.
/// </summary>
/// <remarks>
/// <para>
/// Scope semantics: Logsmith 0.6.0's source-generated log methods pin the
/// logger context per-category at first call, so <c>Scoped()</c> on a logger
/// does not propagate into the static partials. Instead, the adapter keeps an
/// <see cref="AsyncLocal{T}"/> segment stack and prefixes emitted messages
/// with <c>"[seg1/seg2] "</c> while a scope is alive. The Logsmith category
/// stays <c>Instella.User</c>; scope information lives in the message body so
/// every sink sees it uniformly.
/// </para>
/// </remarks>
internal sealed class LogsmithLoggerAdapter : IInstellaLogger
{
    private static readonly AsyncLocal<Stack<string>?> s_scopeStack = new();

    public void Trace(string message) => InternalUserLog.UserTrace(ApplyScope(message));

    public void Debug(string message) => InternalUserLog.UserDebug(ApplyScope(message));

    public void Info(string message) => InternalUserLog.UserInfo(ApplyScope(message));

    public void Warn(string message) => InternalUserLog.UserWarn(ApplyScope(message));

    public void Error(string message, Exception? exception = null)
    {
        var scoped = ApplyScope(message);
        if (exception is null)
            InternalUserLog.UserError(scoped);
        else
            InternalUserLog.UserErrorWithException(scoped, exception);
    }

    public IDisposable Scope(string segment)
    {
        ArgumentException.ThrowIfNullOrEmpty(segment);
        var stack = s_scopeStack.Value ??= new Stack<string>();
        stack.Push(segment);
        return new ScopePopper(stack, segment);
    }

    public bool IsEnabled(InstellaLogLevel level)
        => LogManager.IsEnabled(MapToLogsmith(level), "Instella.User");

    internal static string ApplyScope(string message)
    {
        var stack = s_scopeStack.Value;
        if (stack is null || stack.Count == 0)
            return message;

        // Stack iterates top-first; reverse so outermost scope prints first.
        var segments = stack.ToArray();
        Array.Reverse(segments);
        return "[" + string.Join('/', segments) + "] " + message;
    }

    private sealed class ScopePopper : IDisposable
    {
        private readonly Stack<string> _stack;
        private readonly string _segment;
        private bool _disposed;

        public ScopePopper(Stack<string> stack, string segment)
        {
            _stack = stack;
            _segment = segment;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // Tolerate out-of-order disposal: pop everything above (and
            // including) this segment if present, rather than throwing.
            while (_stack.Count > 0)
            {
                var top = _stack.Pop();
                if (ReferenceEquals(top, _segment) || top == _segment)
                    break;
            }
        }
    }

    internal static LogLevel MapToLogsmith(InstellaLogLevel level) => level switch
    {
        InstellaLogLevel.Trace => LogLevel.Trace,
        InstellaLogLevel.Debug => LogLevel.Debug,
        InstellaLogLevel.Info => LogLevel.Information,
        InstellaLogLevel.Warn => LogLevel.Warning,
        InstellaLogLevel.Error => LogLevel.Error,
        InstellaLogLevel.Critical => LogLevel.Critical,
        _ => LogLevel.Information,
    };

    internal static InstellaLogLevel MapFromLogsmith(LogLevel level) => level switch
    {
        LogLevel.Trace => InstellaLogLevel.Trace,
        LogLevel.Debug => InstellaLogLevel.Debug,
        LogLevel.Information => InstellaLogLevel.Info,
        LogLevel.Warning => InstellaLogLevel.Warn,
        LogLevel.Error => InstellaLogLevel.Error,
        LogLevel.Critical => InstellaLogLevel.Critical,
        _ => InstellaLogLevel.Info,
    };
}
