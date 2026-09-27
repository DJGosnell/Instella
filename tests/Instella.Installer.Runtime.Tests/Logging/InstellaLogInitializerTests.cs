using System;
using System.IO;
using Instella.Core.Logging;
using Instella.Installer.Runtime.Logging;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Logging;

/// <summary>
/// Pure-function coverage for level resolution. No <c>LogManager.Initialize</c>
/// is invoked here — global Logsmith state is untouched so these tests are
/// safe to run in parallel with any other fixture.
/// </summary>
[TestFixture]
public class InstellaLogInitializerLevelResolutionTests
{
    private const string TestEnvVar = "INSTELLA_TEST_LEVEL_VAR";

    [SetUp]
    public void SetUp() => Environment.SetEnvironmentVariable(TestEnvVar, null);

    [TearDown]
    public void TearDown() => Environment.SetEnvironmentVariable(TestEnvVar, null);

    [TestCase("trace", InstellaLogLevel.Trace)]
    [TestCase("DEBUG", InstellaLogLevel.Debug)]
    [TestCase("info", InstellaLogLevel.Info)]
    [TestCase("Information", InstellaLogLevel.Info)]
    [TestCase("warn", InstellaLogLevel.Warn)]
    [TestCase("Warning", InstellaLogLevel.Warn)]
    [TestCase("error", InstellaLogLevel.Error)]
    [TestCase("critical", InstellaLogLevel.Critical)]
    [TestCase("fatal", InstellaLogLevel.Critical)]
    [TestCase("  Info  ", InstellaLogLevel.Info)]
    public void TryParseLevelName_Accepts_Known_Names_Case_Insensitively(string input, InstellaLogLevel expected)
    {
        var ok = InstellaLogInitializer.TryParseLevelName(input, out var level);
        Assert.That(ok, Is.True);
        Assert.That(level, Is.EqualTo(expected));
    }

    [TestCase("")]
    [TestCase("verbose")]
    [TestCase("nope")]
    [TestCase("info,warn")]
    public void TryParseLevelName_Rejects_Unknown_Names(string input)
    {
        var ok = InstellaLogInitializer.TryParseLevelName(input, out _);
        Assert.That(ok, Is.False);
    }

    [Test]
    public void TryParseCliLevel_Handles_Space_Form()
    {
        var ok = InstellaLogInitializer.TryParseCliLevel(new[] { "--silent", "--log-level", "warn" }, out var level);
        Assert.That(ok, Is.True);
        Assert.That(level, Is.EqualTo(InstellaLogLevel.Warn));
    }

    [Test]
    public void TryParseCliLevel_Handles_Equals_Form()
    {
        var ok = InstellaLogInitializer.TryParseCliLevel(new[] { "--log-level=trace" }, out var level);
        Assert.That(ok, Is.True);
        Assert.That(level, Is.EqualTo(InstellaLogLevel.Trace));
    }

    [Test]
    public void TryParseCliLevel_Returns_False_When_Absent()
    {
        var ok = InstellaLogInitializer.TryParseCliLevel(new[] { "--silent" }, out _);
        Assert.That(ok, Is.False);
    }

    [Test]
    public void TryParseCliLevel_Returns_False_On_Unknown_Value()
    {
        var ok = InstellaLogInitializer.TryParseCliLevel(new[] { "--log-level=verbose" }, out _);
        Assert.That(ok, Is.False);
    }

    [Test]
    public void TryParseEnvironmentLevel_Reads_Set_Variable()
    {
        Environment.SetEnvironmentVariable(TestEnvVar, "debug");
        var ok = InstellaLogInitializer.TryParseEnvironmentLevel(TestEnvVar, out var level);
        Assert.That(ok, Is.True);
        Assert.That(level, Is.EqualTo(InstellaLogLevel.Debug));
    }

    [Test]
    public void TryParseEnvironmentLevel_Ignores_Whitespace_Only()
    {
        Environment.SetEnvironmentVariable(TestEnvVar, "   ");
        var ok = InstellaLogInitializer.TryParseEnvironmentLevel(TestEnvVar, out _);
        Assert.That(ok, Is.False);
    }

    [Test]
    public void ResolveLevel_Prefers_Cli_Over_Env_Over_Builder()
    {
        Environment.SetEnvironmentVariable(TestEnvVar, "error");
        var builder = new LoggingBuilder()
            .WithLevel(InstellaLogLevel.Critical)
            .LevelFromEnvironment(TestEnvVar);

        var resolved = InstellaLogInitializer.ResolveLevel(builder, new[] { "--log-level=debug" });

        Assert.That(resolved, Is.EqualTo(InstellaLogLevel.Debug));
    }

    [Test]
    public void ResolveLevel_Falls_Back_To_Env_When_Cli_Absent()
    {
        Environment.SetEnvironmentVariable(TestEnvVar, "warn");
        var builder = new LoggingBuilder()
            .WithLevel(InstellaLogLevel.Critical)
            .LevelFromEnvironment(TestEnvVar);

        var resolved = InstellaLogInitializer.ResolveLevel(builder, cliArgs: null);

        Assert.That(resolved, Is.EqualTo(InstellaLogLevel.Warn));
    }

    [Test]
    public void ResolveLevel_Falls_Back_To_Builder_When_Cli_And_Env_Absent()
    {
        var builder = new LoggingBuilder()
            .WithLevel(InstellaLogLevel.Error)
            .LevelFromEnvironment(TestEnvVar); // env var unset by SetUp

        var resolved = InstellaLogInitializer.ResolveLevel(builder, cliArgs: null);

        Assert.That(resolved, Is.EqualTo(InstellaLogLevel.Error));
    }

    [Test]
    public void ResolveLevel_Final_Default_Is_Info_Or_Debug()
    {
        var builder = new LoggingBuilder();
        var resolved = InstellaLogInitializer.ResolveLevel(builder, cliArgs: null);
        // Debug-only branch selects Debug; Release selects Info. Accept either
        // so the test is correct under both configurations.
        Assert.That(resolved, Is.AnyOf(InstellaLogLevel.Info, InstellaLogLevel.Debug));
    }

    [Test]
    public void DefaultFilePath_Lives_Under_Temp_With_Expected_Shape()
    {
        var path = InstellaLogInitializer.DefaultFilePath("com.example.app", "FirstInstall");
        var temp = Path.GetTempPath();

        Assert.That(path, Does.StartWith(temp));
        var name = Path.GetFileName(path);
        Assert.That(name, Does.StartWith("instella-FirstInstall-com.example.app-"));
        Assert.That(name, Does.EndWith(".log"));
    }

    [Test]
    public void DefaultFilePath_Sanitizes_Invalid_Characters()
    {
        // Forward slash is invalid on Windows filenames; a colon is invalid on
        // most platforms. Both should be stripped without throwing.
        var path = InstellaLogInitializer.DefaultFilePath("bad:id/slash", "mo\\de");
        Assert.DoesNotThrow(() => Path.GetFullPath(path));
    }
}
