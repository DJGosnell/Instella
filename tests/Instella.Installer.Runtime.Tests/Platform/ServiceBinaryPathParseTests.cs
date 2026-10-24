using System.Collections.Generic;
using Instella.Core.Platform;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Platform;

/// <summary>
/// <see cref="ServiceBinaryPath.Parse"/> splits an image path read back from the service manager
/// into what <see cref="ServiceBinaryPath.Build"/> was given, so a rollback can write a snapshot back.
/// </summary>
[TestFixture]
public sealed class ServiceBinaryPathParseTests
{
    private static IEnumerable<TestCaseData> RoundTrips()
    {
        yield return new TestCaseData(@"C:\Program Files\App\App.exe", new string[0]);
        yield return new TestCaseData(@"C:\App\App.exe", new[] { "--service" });
        yield return new TestCaseData(@"C:\App\App.exe", new[] { "--port", "8080", "with space", "" });
        yield return new TestCaseData(@"C:\App\App.exe", new[] { "quote\"inside", @"trailing\", @"C:\dir with space\" });
        yield return new TestCaseData(@"C:\App\App.exe", new[] { @"a\\b", "tab\there" });
    }

    [TestCaseSource(nameof(RoundTrips))]
    public void Parse_InvertsBuild(string exe, string[] args)
    {
        var (parsedExe, parsedArgs) = ServiceBinaryPath.Parse(ServiceBinaryPath.Build(exe, args));

        Assert.That(parsedExe, Is.EqualTo(exe));
        Assert.That(parsedArgs, Is.EqualTo(args));
    }

    [Test]
    public void AnUnquotedExecutable_EndsAtTheFirstSpace()
    {
        var (exe, args) = ServiceBinaryPath.Parse(@"C:\Windows\System32\svchost.exe -k netsvcs");

        Assert.That(exe, Is.EqualTo(@"C:\Windows\System32\svchost.exe"));
        Assert.That(args, Is.EqualTo(new[] { "-k", "netsvcs" }));
    }
}
