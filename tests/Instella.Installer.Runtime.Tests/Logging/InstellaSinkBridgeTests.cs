using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Logging;
using Instella.Installer.Runtime.Logging;
using Logsmith;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Logging;

/// <summary>
/// Exercises the DispatchInfo → InstellaLogEntry conversion in isolation,
/// without touching <c>LogManager</c>. Because <c>DispatchInfo</c> is
/// Logsmith-internal (emitted as internal-to-this-assembly in Standalone
/// mode), these tests compile only with InternalsVisibleTo.
/// </summary>
[TestFixture]
public class InstellaSinkBridgeTests
{
    [Test]
    public void Write_Maps_Level_And_Category_And_Message()
    {
        var sink = new CapturingSink();
        var bridge = new InstellaSinkBridge(sink);

        var info = new DispatchInfo
        {
            Level = LogLevel.Warning,
            Category = "Instella.User.step:extract",
            TimestampTicks = new DateTime(2026, 4, 17, 12, 0, 0, DateTimeKind.Utc).Ticks,
            Utf8Message = "hello world"u8,
        };

        bridge.Write(in info);

        Assert.That(sink.Entries, Has.Count.EqualTo(1));
        var e = sink.Entries[0];
        Assert.That(e.Level, Is.EqualTo(InstellaLogLevel.Warn));
        Assert.That(e.Category, Is.EqualTo("Instella.User.step:extract"));
        Assert.That(e.Message, Is.EqualTo("hello world"));
        Assert.That(e.Timestamp.UtcDateTime, Is.EqualTo(new DateTime(2026, 4, 17, 12, 0, 0, DateTimeKind.Utc)));
        Assert.That(e.Exception, Is.Null);
    }

    [Test]
    public void Write_Propagates_Exception()
    {
        var sink = new CapturingSink();
        var bridge = new InstellaSinkBridge(sink);
        var boom = new InvalidOperationException("boom");

        var info = new DispatchInfo
        {
            Level = LogLevel.Error,
            Category = "Instella.User",
            Utf8Message = "failed"u8,
            Exception = boom,
        };

        bridge.Write(in info);

        Assert.That(sink.Entries[0].Exception, Is.SameAs(boom));
    }

    [Test]
    public void Write_Handles_Empty_Utf8_Message()
    {
        var sink = new CapturingSink();
        var bridge = new InstellaSinkBridge(sink);

        var info = new DispatchInfo
        {
            Level = LogLevel.Information,
            Category = "Instella",
            Utf8Message = default,
        };

        bridge.Write(in info);

        Assert.That(sink.Entries[0].Message, Is.EqualTo(string.Empty));
    }

    [Test]
    public void IsEnabled_Respects_Minimum_Level()
    {
        var sink = new CapturingSink();
        var bridge = new InstellaSinkBridge(sink, InstellaLogLevel.Warn);

        Assert.That(bridge.IsEnabled(LogLevel.Trace), Is.False);
        Assert.That(bridge.IsEnabled(LogLevel.Debug), Is.False);
        Assert.That(bridge.IsEnabled(LogLevel.Information), Is.False);
        Assert.That(bridge.IsEnabled(LogLevel.Warning), Is.True);
        Assert.That(bridge.IsEnabled(LogLevel.Error), Is.True);
        Assert.That(bridge.IsEnabled(LogLevel.Critical), Is.True);
    }

    [Test]
    public void Constructor_Rejects_Null_Sink()
    {
        Assert.Throws<ArgumentNullException>(() => new InstellaSinkBridge(null!));
    }

    private sealed class CapturingSink : IInstellaLogSink
    {
        public List<InstellaLogEntry> Entries { get; } = new();
        public void Write(in InstellaLogEntry entry) => Entries.Add(entry);
        public ValueTask FlushAsync(CancellationToken cancellationToken) => default;
    }
}
