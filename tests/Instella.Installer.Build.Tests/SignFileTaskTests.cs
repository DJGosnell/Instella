using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Instella.Installer.Build.Tasks;
using Microsoft.Build.Framework;
using NUnit.Framework;

namespace Instella.Installer.Build.Tests;

/// <summary>
/// The task that signs the online installer (<c>InstellaEnabled=false</c>). A fake sign
/// command writes a <c>.sig</c> file next to its target, as in the payload task's tests.
/// </summary>
[TestFixture]
public sealed class SignFileTaskTests
{
    private string _tempDir = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"instella-sign-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    [Test]
    public void Execute_withASignCommand_signsTheFile()
    {
        var exe = Exe();
        var task = new SignFile { BuildEngine = new Engine(), FilePath = exe, SignCommand = "echo signed> \"{0}.sig\"" };

        Assert.That(task.Execute(), Is.True);
        Assert.That(File.Exists(exe + ".sig"), Is.True);
    }

    [Test]
    public void Execute_withoutASignCommand_signsNothing()
    {
        var exe = Exe();
        var task = new SignFile { BuildEngine = new Engine(), FilePath = exe, SignCommand = "" };

        Assert.That(task.Execute(), Is.True);
        Assert.That(Directory.GetFiles(_tempDir), Is.EqualTo(new[] { exe }));
    }

    [TestCase("echo no placeholder")]
    [TestCase("exit 3 {0}")]
    public void Execute_aBadSignCommand_failsWithINSTELLA0204(string command)
    {
        var engine = new Engine();
        var task = new SignFile { BuildEngine = engine, FilePath = Exe(), SignCommand = command };

        Assert.That(task.Execute(), Is.False);
        Assert.That(engine.Errors.Select(e => e.Code), Does.Contain("INSTELLA0204"));
    }

    [Test]
    public void Execute_aMissingFile_fails()
    {
        var engine = new Engine();
        var task = new SignFile { BuildEngine = engine, FilePath = Path.Combine(_tempDir, "missing.exe"), SignCommand = "echo x> \"{0}.sig\"" };

        Assert.That(task.Execute(), Is.False);
        Assert.That(engine.Errors, Has.Count.EqualTo(1));
    }

    private string Exe()
    {
        var path = Path.Combine(_tempDir, "Installer.exe");
        File.WriteAllBytes(path, [0x4D, 0x5A, 0x90, 0x00]);
        return path;
    }

    private sealed class Engine : IBuildEngine
    {
        public List<BuildErrorEventArgs> Errors { get; } = new();
        public bool ContinueOnError => false;
        public int LineNumberOfTaskNode => 0;
        public int ColumnNumberOfTaskNode => 0;
        public string ProjectFileOfTaskNode => string.Empty;

        public bool BuildProjectFile(string projectFileName, string[] targetNames, System.Collections.IDictionary globalProperties, System.Collections.IDictionary targetOutputs) => true;
        public void LogCustomEvent(CustomBuildEventArgs e) { }
        public void LogErrorEvent(BuildErrorEventArgs e) => Errors.Add(e);
        public void LogMessageEvent(BuildMessageEventArgs e) { }
        public void LogWarningEvent(BuildWarningEventArgs e) { }
    }
}
