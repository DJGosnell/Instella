using System.Reflection;
using NUnit.Framework;

namespace Instella.E2E.Tests;

/// <summary>
/// The one test a default <c>dotnet test</c> runs here: every other test in this assembly must be
/// <see cref="ExplicitAttribute"/> (on the test or its fixture), so a plain run never installs
/// software into the user profile or starts Docker. It also gives the default run a test to report.
/// </summary>
[TestFixture]
public sealed class ExplicitGuardTests
{
    [Test]
    public void EveryOtherTest_IsExplicit()
    {
        var notExplicit = typeof(ExplicitGuardTests).Assembly.GetTypes()
            .Where(t => t != typeof(ExplicitGuardTests) && t.GetCustomAttribute<ExplicitAttribute>(inherit: true) is null)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                .Where(m => m.GetCustomAttributes(inherit: true).Any(a => a is TestAttribute or TestCaseAttribute or TestCaseSourceAttribute))
                .Where(m => m.GetCustomAttribute<ExplicitAttribute>(inherit: true) is null)
                .Select(m => $"{t.FullName}.{m.Name}"))
            .ToArray();

        Assert.That(notExplicit, Is.Empty,
            "E2E and Docker tests touch the machine; mark them [Explicit] and run them through verify.ps1");
    }
}
