using System;

namespace Instella.Core.Logging;

/// <summary>
/// Structured log record handed to an <see cref="IInstellaLogSink"/>. The
/// message has already been rendered to a string by the time a sink sees it,
/// so sinks do not need to know about message templates.
/// </summary>
public readonly record struct InstellaLogEntry(
    DateTimeOffset Timestamp,
    InstellaLogLevel Level,
    string Category,
    string Message,
    Exception? Exception);
