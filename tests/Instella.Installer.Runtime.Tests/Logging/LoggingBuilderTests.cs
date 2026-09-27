using System;
using Instella.Core.Logging;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Logging;

/// <summary>
/// <see cref="LoggingBuilder"/> is a pure config object — no side effects, no
/// pipeline interaction — so these tests exercise its fluent surface directly.
/// </summary>
[TestFixture]
public class LoggingBuilderTests
{
    [Test]
    public void Default_State_Matches_Documented_Defaults()
    {
        var b = new LoggingBuilder();

        Assert.That(b.Level, Is.Null);
        Assert.That(b.EnvironmentVariableName, Is.EqualTo("INSTELLA_LOG_LEVEL"));
        Assert.That(b.FileEnabled, Is.True);
        Assert.That(b.FilePath, Is.Null);
        Assert.That(b.ConsoleEnabled, Is.False);
        Assert.That(b.InternalErrorHandler, Is.Null);
        Assert.That(b.Sinks, Is.Empty);
    }

    [Test]
    public void WithLevel_Stores_Explicit_Level()
    {
        var b = new LoggingBuilder().WithLevel(InstellaLogLevel.Warn);
        Assert.That(b.Level, Is.EqualTo(InstellaLogLevel.Warn));
    }

    [Test]
    public void LevelFromEnvironment_Sets_Custom_Variable_Name()
    {
        var b = new LoggingBuilder().LevelFromEnvironment("MY_APP_LOG");
        Assert.That(b.EnvironmentVariableName, Is.EqualTo("MY_APP_LOG"));
    }

    [Test]
    public void DisableEnvironmentLevel_Nulls_Variable_Name()
    {
        var b = new LoggingBuilder().DisableEnvironmentLevel();
        Assert.That(b.EnvironmentVariableName, Is.Null);
    }

    [Test]
    public void File_Sets_Path_And_Keeps_File_Enabled()
    {
        var b = new LoggingBuilder().NoFile().File(@"C:\tmp\x.log");
        Assert.That(b.FilePath, Is.EqualTo(@"C:\tmp\x.log"));
        Assert.That(b.FileEnabled, Is.True);
    }

    [Test]
    public void NoFile_Clears_Path_And_Disables()
    {
        var b = new LoggingBuilder().File(@"C:\tmp\x.log").NoFile();
        Assert.That(b.FileEnabled, Is.False);
        Assert.That(b.FilePath, Is.Null);
    }

    [Test]
    public void Console_Toggles_Flag()
    {
        var b = new LoggingBuilder().Console();
        Assert.That(b.ConsoleEnabled, Is.True);
        b.Console(false);
        Assert.That(b.ConsoleEnabled, Is.False);
    }

    [Test]
    public void AddSink_Accumulates_In_Order()
    {
        var s1 = new CapturingSink();
        var s2 = new CapturingSink();
        var b = new LoggingBuilder().AddSink(s1).AddSink(s2);
        Assert.That(b.Sinks, Is.EqualTo(new IInstellaLogSink[] { s1, s2 }));
    }

    [Test]
    public void AddSink_Null_Throws()
    {
        var b = new LoggingBuilder();
        Assert.Throws<ArgumentNullException>(() => b.AddSink(null!));
    }

    [Test]
    public void OnInternalError_Stores_Handler()
    {
        Action<Exception> handler = _ => { };
        var b = new LoggingBuilder().OnInternalError(handler);
        Assert.That(b.InternalErrorHandler, Is.SameAs(handler));
    }

    [Test]
    public void OnInternalError_Null_Throws()
    {
        var b = new LoggingBuilder();
        Assert.Throws<ArgumentNullException>(() => b.OnInternalError(null!));
    }

    private sealed class CapturingSink : IInstellaLogSink
    {
        public void Write(in InstellaLogEntry entry) { }
        public System.Threading.Tasks.ValueTask FlushAsync(System.Threading.CancellationToken cancellationToken) => default;
    }
}
