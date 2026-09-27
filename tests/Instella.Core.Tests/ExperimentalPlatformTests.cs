using Instella.Core.Platform;
using Instella.Core.Platform.Linux;
using NUnit.Framework;

namespace Instella.Core.Tests;

/// <summary>Correctness checks for the experimental platforms (Linux and macOS).</summary>
[TestFixture]
public class ExperimentalPlatformTests
{
    [TestCase("/opt/app/app", "\"/opt/app/app\"")]
    [TestCase("/opt/my app/app", "\"/opt/my app/app\"")]
    [TestCase("/opt/a\"b", "\"/opt/a\\\\\"b\"")]          // " -> \" -> (string escape) \\"
    [TestCase("/opt/$HOME/`x`", "\"/opt/\\\\$HOME/\\\\`x\\\\`\"")]
    [TestCase("/opt/back\\slash", "\"/opt/back\\\\\\\\slash\"")]   // one backslash becomes four
    [TestCase("/opt/100%/app", "\"/opt/100%%/app\"")]
    public void DesktopEntry_QuotesExecArguments(string path, string expected) =>
        Assert.That(DesktopEntry.QuoteArgument(path), Is.EqualTo(expected));

    [Test]
    public void DesktopEntry_Exec_AppendsArgumentsAndFieldCode() =>
        Assert.That(DesktopEntry.Exec("/opt/my app/app", "--start", "%f"), Is.EqualTo("\"/opt/my app/app\" --start %f"));

    [Test]
    public void DesktopEntry_EscapesNewlinesInValues() =>
        Assert.That(DesktopEntry.EscapeString("a\nb"), Is.EqualTo("a\\nb"));

    [Test]
    public async Task ExternalCommand_ZeroExit_Succeeds()
    {
        var result = await ExternalCommand.RunAsync("dotnet", ["--version"], CancellationToken.None);
        Assert.That(result.Success, Is.True, result.Error);
    }

    [Test]
    public async Task ExternalCommand_NonZeroExit_FailsWithTheToolsError()
    {
        var result = await ExternalCommand.RunAsync("dotnet", ["this-command-does-not-exist-instella"], CancellationToken.None);
        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Does.Contain("exited"));
    }

    [Test]
    public async Task ExternalCommand_MissingTool_FailsNamingIt()
    {
        var result = await ExternalCommand.RunAsync("instella-no-such-tool", [], CancellationToken.None);
        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Does.Contain("instella-no-such-tool"));
    }
}
