using System.Linq;
using Instella.Installer.Build.Tasks;
using NUnit.Framework;

namespace Instella.Installer.Build.Tests;

/// <summary>INSTELLA0105: each declared Windows service's executable must be in the payload.</summary>
public sealed partial class AppendPayloadToSelfTaskTests
{
    private AppendPayloadToSelf ServiceTask(string manifestJson, StubBuildEngine engine, params string[] payload)
    {
        var (exe, manifest, archive) = Fixture(manifestJson);
        var task = NewTask(exe, manifest, archive, engine, names: ["A.exe"]);
        task.PayloadFiles = payload.Select(p => MakePayloadFile(p, p)).ToArray();
        return task;
    }

    [Test]
    public void Execute_aServiceExecutableMissingFromThePayload_failsWithINSTELLA0105()
    {
        var engine = new StubBuildEngine();
        var task = ServiceTask("""{"appName":"A","appId":"a","executableName":"A.exe","services":[{"name":"A.Worker","executable":"bin/A.Worker.exe"}]}""",
            engine, "A.exe", "bin/other.dll");

        Assert.That(task.Execute(), Is.False);
        var error = engine.Errors.Single(e => e.Code == "INSTELLA0105");
        Assert.That(error.Message, Does.Contain("A.Worker").And.Contain("bin/A.Worker.exe"));
    }

    [Test]
    public void Execute_aServiceOnTheMainExecutable_failsWithINSTELLA0105_WhenThePayloadLacksIt()
    {
        var engine = new StubBuildEngine();
        var task = ServiceTask("""{"appName":"A","appId":"a","executableName":"A.exe","services":[{"name":"A.Svc"}]}""",
            engine, "lib/a.dll");

        Assert.That(task.Execute(), Is.False);
        Assert.That(engine.Errors.Select(e => e.Code), Does.Contain("INSTELLA0105"));
    }

    [Test]
    public void Execute_servicesWhoseExecutablesAreInThePayload_succeed()
    {
        var engine = new StubBuildEngine();
        var task = ServiceTask("""{"appName":"A","appId":"a","executableName":"A.exe","services":[{"name":"A.Svc"},{"name":"A.Worker","executable":"bin/A.Worker.exe"}]}""",
            engine, "A.exe", "bin/A.Worker.exe");

        Assert.That(task.Execute(), Is.True, string.Join("; ", engine.Errors.Select(e => e.Message)));
    }

    [Test]
    public void Execute_aMainExeServiceWithAnExecutableNameWithoutExe_succeeds_WhenThePayloadHasTheExe()
    {
        var engine = new StubBuildEngine();
        var task = ServiceTask("""{"appName":"A","appId":"a","executableName":"A","services":[{"name":"A.Svc"}]}""",
            engine, "A.exe");

        Assert.That(task.Execute(), Is.True, string.Join("; ", engine.Errors.Select(e => e.Code + ": " + e.Message)));
    }
}
