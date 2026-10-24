using System.Runtime.InteropServices;

namespace Instella.Installer.Build.Tests;

public class ServicePayloadCheckTests
{
    [NUnit.Framework.Test]
    public void Execute_aMainExeServiceWithAnExecutableNameWithoutExe_succeeds_WhenThePayloadHasTheExe()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "instella-probe-" + System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            var exe = System.IO.Path.Combine(dir, "installer.exe");
            System.IO.File.WriteAllBytes(exe, new byte[] { 0x4D, 0x5A, 0x90, 0x00 });
            var manifest = System.IO.Path.Combine(dir, "instella.json");
            System.IO.File.WriteAllText(manifest, "{\"appName\":\"A\",\"appId\":\"a\",\"executableName\":\"A\",\"services\":[{\"name\":\"A.Svc\"}]}");
            var src = System.IO.Path.Combine(dir, "A.exe");
            System.IO.File.WriteAllText(src, "app");
            var engineType = typeof(Instella.Installer.Build.Tests.AppendPayloadToSelfTaskTests).GetNestedType("StubBuildEngine", System.Reflection.BindingFlags.NonPublic)!;
            var engine = (Microsoft.Build.Framework.IBuildEngine)System.Activator.CreateInstance(engineType, nonPublic: true)!;
            var task = new Instella.Installer.Build.Tasks.AppendPayloadToSelf
            {
                BuildEngine = engine,
                InstallerExePath = exe,
                ManifestPath = manifest,
                PayloadFiles = new Microsoft.Build.Framework.ITaskItem[] { new Microsoft.Build.Utilities.TaskItem(src, new System.Collections.Generic.Dictionary<string, string> { ["TargetPath"] = "A.exe" }) },
                PayloadExecutableNames = new[] { "A.exe" },
                IntermediateArchivePath = System.IO.Path.Combine(dir, "payload.zip"),
            };
    
            var ok = task.Execute();
            var errors = (System.Collections.IEnumerable)engineType.GetProperty("Errors")!.GetValue(engine)!;
            var messages = string.Join("; ", System.Linq.Enumerable.Select(System.Linq.Enumerable.Cast<Microsoft.Build.Framework.BuildErrorEventArgs>(errors), e => e.Code + ": " + e.Message));
            NUnit.Framework.Assert.That(ok, NUnit.Framework.Is.True, messages);
        }
        finally
        {
            try { System.IO.Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
