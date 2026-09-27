using Microsoft.Extensions.Logging;

namespace Instella.Server.Tests.Infrastructure;

/// <summary>
/// Test logger that collects log messages for assertions.
/// </summary>
public class TestLogger<T> : ILogger<T>
{
    private readonly List<LogEntry> _logs = new();
    private readonly object _lock = new();

    public IReadOnlyList<LogEntry> Logs
    {
        get
        {
            lock (_lock)
            {
                return _logs.ToList();
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_lock)
        {
            _logs.Add(new LogEntry(logLevel, formatter(state, exception), exception));
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _logs.Clear();
        }
    }

    public bool HasLoggedError() => Logs.Any(l => l.Level == LogLevel.Error);
    public bool HasLoggedWarning() => Logs.Any(l => l.Level == LogLevel.Warning);
    public bool HasLogged(LogLevel level, string messageContains) =>
        Logs.Any(l => l.Level == level && l.Message.Contains(messageContains, StringComparison.OrdinalIgnoreCase));
}

public record LogEntry(LogLevel Level, string Message, Exception? Exception);
