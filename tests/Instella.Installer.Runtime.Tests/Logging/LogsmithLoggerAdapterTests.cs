using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Logging;
using Instella.Installer.Runtime.Logging;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Logging;

/// <summary>
/// End-to-end test of <see cref="LogsmithLoggerAdapter"/> against a real
/// initialized Logsmith pipeline. <c>LogManager</c> is process-global, so the
/// fixture is marked non-parallelizable and owns initialization / teardown.
/// </summary>
[TestFixture]
[NonParallelizable]
public class LogsmithLoggerAdapterTests
{
    private CapturingSink _sink = null!;
    private LogsmithLoggerAdapter _adapter = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _sink = new CapturingSink();
        var builder = new LoggingBuilder()
            .WithLevel(InstellaLogLevel.Trace)
            .NoFile()
            .DisableEnvironmentLevel()
            .AddSink(_sink);
        InstellaLogInitializer.Initialize(builder, cliArgs: null, appId: "test.app", modeName: "Unit");
        _adapter = new LogsmithLoggerAdapter();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        await InstellaLogInitializer.ShutdownAsync();
    }

    [SetUp]
    public void ResetSink() => _sink.Clear();

    [Test]
    public void Info_Emits_Entry_Under_User_Category()
    {
        _adapter.Info("hello");

        var match = FindEntryEndingInMessage("hello");
        Assert.That(match.Level, Is.EqualTo(InstellaLogLevel.Info));
        Assert.That(match.Category, Does.StartWith("Instella.User"));
    }

    [Test]
    public void Error_With_Exception_Attaches_It()
    {
        var boom = new InvalidOperationException("nope");
        _adapter.Error("things fell apart", boom);

        var match = FindEntryEndingInMessage("things fell apart");
        Assert.That(match.Level, Is.EqualTo(InstellaLogLevel.Error));
        Assert.That(match.Exception, Is.SameAs(boom));
    }

    [Test]
    public void Scope_Prefixes_Message_While_Active()
    {
        using (_adapter.Scope("extract"))
        {
            _adapter.Info("inside-scope");
        }
        _adapter.Info("outside-scope");

        var inside = FindEntryEndingInMessage("[extract] inside-scope");
        var outside = FindEntryEndingInMessage("outside-scope");

        Assert.That(inside.Message, Is.EqualTo("[extract] inside-scope"));
        Assert.That(outside.Message, Is.EqualTo("outside-scope"));
    }

    [Test]
    public void Nested_Scopes_Join_With_Slash_In_Order()
    {
        using (_adapter.Scope("step:extract"))
        using (_adapter.Scope("file:payload.zip"))
        {
            _adapter.Warn("slow read");
        }

        var match = FindEntryEndingInMessage("[step:extract/file:payload.zip] slow read");
        Assert.That(match.Level, Is.EqualTo(InstellaLogLevel.Warn));
    }

    [Test]
    public void Scope_State_Is_Restored_After_Disposal()
    {
        _adapter.Info("pre");
        using (_adapter.Scope("inner"))
        {
            _adapter.Info("during");
        }
        _adapter.Info("post");

        Assert.That(FindEntryEndingInMessage("pre").Message, Is.EqualTo("pre"));
        Assert.That(FindEntryEndingInMessage("[inner] during").Message, Is.EqualTo("[inner] during"));
        Assert.That(FindEntryEndingInMessage("post").Message, Is.EqualTo("post"));
    }

    [Test]
    public void IsEnabled_Honors_MinimumLevel_Set_At_Init()
    {
        // OneTimeSetUp configured Trace; all levels must report enabled.
        Assert.That(_adapter.IsEnabled(InstellaLogLevel.Trace), Is.True);
        Assert.That(_adapter.IsEnabled(InstellaLogLevel.Critical), Is.True);
    }

    [Test]
    public void Scope_Rejects_Empty_Segment()
    {
        Assert.Throws<ArgumentException>(() => _adapter.Scope(string.Empty));
    }

    private InstellaLogEntry FindEntryEndingInMessage(string message)
    {
        var entries = _sink.Snapshot();
        var match = entries.LastOrDefault(e => e.Message == message);
        Assert.That(match.Message, Is.EqualTo(message), $"no entry with message '{message}' in {entries.Count} captured entries");
        return match;
    }

    private sealed class CapturingSink : IInstellaLogSink
    {
        private readonly List<InstellaLogEntry> _entries = new();
        private readonly Lock _gate = new();

        public void Write(in InstellaLogEntry entry)
        {
            lock (_gate) _entries.Add(entry);
        }

        public ValueTask FlushAsync(CancellationToken cancellationToken) => default;

        public void Clear()
        {
            lock (_gate) _entries.Clear();
        }

        public IReadOnlyList<InstellaLogEntry> Snapshot()
        {
            lock (_gate) return _entries.ToArray();
        }
    }
}
