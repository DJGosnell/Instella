using Instella.Core.Logging;
using NUnit.Framework;

namespace Instella.Installer.Testing.Tests;

[TestFixture]
public class RecordingLoggerTests
{
    [Test]
    public void Info_writes_entry_with_level_and_category()
    {
        var sink = new RecordingSink();
        var logger = new RecordingLogger(sink, "Instella.Test.Cat");

        logger.Info("hello");

        Assert.That(sink.Entries.Count, Is.EqualTo(1));
        Assert.That(sink.Entries[0].Level, Is.EqualTo(InstellaLogLevel.Info));
        Assert.That(sink.Entries[0].Category, Is.EqualTo("Instella.Test.Cat"));
        Assert.That(sink.Entries[0].Message, Is.EqualTo("hello"));
    }

    [Test]
    public void Error_carries_exception()
    {
        var sink = new RecordingSink();
        var logger = new RecordingLogger(sink);
        var ex = new InvalidOperationException("boom");

        logger.Error("failed", ex);

        Assert.That(sink.Entries[0].Exception, Is.EqualTo(ex));
    }

    [Test]
    public void Scope_prefixes_messages_with_bracket_segments()
    {
        var sink = new RecordingSink();
        var logger = new RecordingLogger(sink);

        using (logger.Scope("outer"))
        using (logger.Scope("inner"))
        {
            logger.Info("msg");
        }

        Assert.That(sink.Entries[0].Message, Is.EqualTo("[outer/inner] msg"));
    }

    [Test]
    public void Scope_pop_restores_parent_prefix()
    {
        var sink = new RecordingSink();
        var logger = new RecordingLogger(sink);

        using (logger.Scope("outer"))
        {
            using (logger.Scope("inner"))
                logger.Info("nested");
            logger.Info("bare");
        }

        Assert.That(sink.Entries[0].Message, Is.EqualTo("[outer/inner] nested"));
        Assert.That(sink.Entries[1].Message, Is.EqualTo("[outer] bare"));
    }

    [Test]
    public void IsEnabled_always_true()
    {
        var logger = new RecordingLogger(new RecordingSink());
        Assert.That(logger.IsEnabled(InstellaLogLevel.Trace), Is.True);
        Assert.That(logger.IsEnabled(InstellaLogLevel.Critical), Is.True);
    }
}
