using System;
using System.Text;
using Instella.Core.Logging;
using Logsmith;

namespace Instella.Installer.Runtime.Logging;

/// <summary>
/// Bridges a user-supplied <see cref="IInstellaLogSink"/> onto Logsmith's
/// <c>ILogSink</c> interface. Converts UTF-8 message spans into managed
/// strings so sinks never see Logsmith types. One bridge instance per
/// user sink.
/// </summary>
internal sealed class InstellaSinkBridge : ILogSink
{
    private readonly IInstellaLogSink _userSink;
    private readonly InstellaLogLevel _minimumLevel;

    public InstellaSinkBridge(IInstellaLogSink userSink, InstellaLogLevel minimumLevel = InstellaLogLevel.Trace)
    {
        ArgumentNullException.ThrowIfNull(userSink);
        _userSink = userSink;
        _minimumLevel = minimumLevel;
    }

    public bool IsEnabled(LogLevel level)
        => LogsmithLoggerAdapter.MapFromLogsmith(level) >= _minimumLevel;

    public void Write(in DispatchInfo info)
    {
        var message = info.Utf8Message.Length == 0
            ? string.Empty
            : Encoding.UTF8.GetString(info.Utf8Message);

        var entry = new InstellaLogEntry(
            Timestamp: new DateTimeOffset(info.TimestampTicks, TimeSpan.Zero),
            Level: LogsmithLoggerAdapter.MapFromLogsmith(info.Level),
            Category: info.Category ?? string.Empty,
            Message: message,
            Exception: info.Exception);

        _userSink.Write(in entry);
    }

    public void Dispose()
    {
        // The user-supplied sink owns its resources; we hold no handles to
        // release here. FlushAsync on the user sink is driven from
        // InstellaLogInitializer at shutdown, not Dispose.
    }
}
