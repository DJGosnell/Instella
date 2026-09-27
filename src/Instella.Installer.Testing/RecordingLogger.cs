using Instella.Core.Logging;

namespace Instella.Installer.Testing;

/// <summary>
/// Minimal <see cref="IInstellaLogger"/> implementation that forwards every
/// call straight to a <see cref="RecordingSink"/> — no Logsmith pipeline,
/// no file sink, no static state. Used by <see cref="InstellaTestHarness"/>
/// so harness tests don't pay the cost of full logging initialization.
/// </summary>
/// <remarks>
/// Scope behavior mirrors the real adapter's convention: nested segments
/// render as <c>[seg1/seg2] message</c>. Scope state is per-instance (no
/// <c>AsyncLocal</c>) since tests drive a single logger synchronously.
/// </remarks>
public sealed class RecordingLogger : IInstellaLogger
{
    private readonly RecordingSink _sink;
    private readonly string _category;
    private readonly Stack<string> _scopes = new();

    /// <summary>A logger that writes every entry to <paramref name="sink"/> under <paramref name="category"/>.</summary>
    public RecordingLogger(RecordingSink sink, string category = "Instella.Test")
    {
        ArgumentNullException.ThrowIfNull(sink);
        _sink = sink;
        _category = category;
    }

    /// <inheritdoc />
    public void Trace(string message) => Emit(InstellaLogLevel.Trace, message, null);

    /// <inheritdoc />
    public void Debug(string message) => Emit(InstellaLogLevel.Debug, message, null);

    /// <inheritdoc />
    public void Info(string message) => Emit(InstellaLogLevel.Info, message, null);

    /// <inheritdoc />
    public void Warn(string message) => Emit(InstellaLogLevel.Warn, message, null);

    /// <inheritdoc />
    public void Error(string message, Exception? exception = null) => Emit(InstellaLogLevel.Error, message, exception);

    /// <inheritdoc />
    public IDisposable Scope(string segment)
    {
        ArgumentException.ThrowIfNullOrEmpty(segment);
        _scopes.Push(segment);
        return new Popper(this, segment);
    }

    /// <inheritdoc />
    public bool IsEnabled(InstellaLogLevel level) => true;

    private void Emit(InstellaLogLevel level, string message, Exception? exception)
    {
        var rendered = _scopes.Count == 0 ? message : PrefixScopes(message);
        var entry = new InstellaLogEntry(DateTimeOffset.UtcNow, level, _category, rendered, exception);
        _sink.Write(in entry);
    }

    private string PrefixScopes(string message)
    {
        var segments = _scopes.ToArray();
        Array.Reverse(segments);
        return "[" + string.Join('/', segments) + "] " + message;
    }

    private sealed class Popper : IDisposable
    {
        private readonly RecordingLogger _owner;
        private readonly string _segment;
        private bool _disposed;

        public Popper(RecordingLogger owner, string segment)
        {
            _owner = owner;
            _segment = segment;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            while (_owner._scopes.Count > 0)
            {
                var top = _owner._scopes.Pop();
                if (ReferenceEquals(top, _segment) || top == _segment) break;
            }
        }
    }
}
