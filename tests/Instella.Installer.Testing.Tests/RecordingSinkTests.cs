using Instella.Core.Logging;
using NUnit.Framework;

namespace Instella.Installer.Testing.Tests;

[TestFixture]
public class RecordingSinkTests
{
    [Test]
    public void Write_captures_entries_in_order()
    {
        var sink = new RecordingSink();
        sink.Write(new InstellaLogEntry(DateTimeOffset.UtcNow, InstellaLogLevel.Info, "cat", "first", null));
        sink.Write(new InstellaLogEntry(DateTimeOffset.UtcNow, InstellaLogLevel.Warn, "cat", "second", null));

        Assert.That(sink.Entries.Count, Is.EqualTo(2));
        Assert.That(sink.Entries[0].Message, Is.EqualTo("first"));
        Assert.That(sink.Entries[1].Level, Is.EqualTo(InstellaLogLevel.Warn));
    }

    [Test]
    public void Clear_drops_entries()
    {
        var sink = new RecordingSink();
        sink.Write(new InstellaLogEntry(DateTimeOffset.UtcNow, InstellaLogLevel.Info, "cat", "msg", null));
        sink.Clear();
        Assert.That(sink.Entries.Count, Is.EqualTo(0));
    }

    [Test]
    public void AtOrAbove_filters_by_level()
    {
        var sink = new RecordingSink();
        sink.Write(new InstellaLogEntry(DateTimeOffset.UtcNow, InstellaLogLevel.Debug, "cat", "d", null));
        sink.Write(new InstellaLogEntry(DateTimeOffset.UtcNow, InstellaLogLevel.Info, "cat", "i", null));
        sink.Write(new InstellaLogEntry(DateTimeOffset.UtcNow, InstellaLogLevel.Error, "cat", "e", null));

        var warnPlus = sink.AtOrAbove(InstellaLogLevel.Warn);
        Assert.That(warnPlus.Count, Is.EqualTo(1));
        Assert.That(warnPlus[0].Level, Is.EqualTo(InstellaLogLevel.Error));
    }

    [Test]
    public async Task FlushAsync_is_a_noop_that_completes()
    {
        var sink = new RecordingSink();
        await sink.FlushAsync(default);
        Assert.Pass();
    }
}
