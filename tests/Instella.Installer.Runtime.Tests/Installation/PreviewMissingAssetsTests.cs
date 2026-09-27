using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Installation;
using Instella.Core.Logging;
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Runtime.Runners;
using Instella.Installer.Runtime.UI.Widgets;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Installation;

/// <summary>
/// Preview must warn (not crash) when a file-backed asset (icon,
/// file-association icon) points at a path that doesn't exist on the dev box.
/// </summary>
[TestFixture]
public sealed class PreviewMissingAssetsTests
{
    private sealed class CapturingLogger : IInstellaLogger
    {
        public readonly System.Collections.Generic.List<string> Warns = new();
        public bool IsEnabled(InstellaLogLevel level) => true;
        public void Trace(string m) { }
        public void Debug(string m) { }
        public void Info(string m) { }
        public void Warn(string m) => Warns.Add(m);
        public void Error(string m, Exception? e = null) { }
        public IDisposable Scope(string s) => NullScope.Instance;
        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    [Test]
    public async Task MissingIconFile_warns_butContinues()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}.png");

        var b = new InstallerBuilder();
        b.WithApp("Preview Test", "com.example.preview", new Version(1, 0, 0));
        b.WithIcon(ImageSource.FromFile(missingPath));
        b.EnablePreview();
        var installer = (InstellaInstallerImpl)b.Build();

        var log = new CapturingLogger();
        var stdout = new StringWriter();
        PreviewHostLauncher launcher = (t, pages, states, ip) => PreviewHostOutcome.Completed;
        var runner = new PreviewModeRunner(installer.ConfigForTests, log, stdout, launcher);

        var args = new PreviewCliArgs(true, InstallerMode.Manage, PreviewSpeed.Fast, null);
        var exit = await runner.RunAsync(args, CancellationToken.None);

        Assert.That(exit, Is.EqualTo(InstellaExitCode.Success));
        Assert.That(log.Warns, Has.Some.Contain(missingPath));
        Assert.That(log.Warns, Has.Some.Contain("not found"));
        Assert.That(stdout.ToString(), Does.Contain("not found"));
    }

    [Test]
    public async Task PresentIconFile_noWarn()
    {
        var tempIcon = Path.Combine(Path.GetTempPath(), $"preview-icon-{Guid.NewGuid():N}.ico");
        File.WriteAllBytes(tempIcon, new byte[] { 0x00 }); // content doesn't matter — file just has to exist

        try
        {
            var b = new InstallerBuilder();
            b.WithApp("Preview Test", "com.example.preview", new Version(1, 0, 0));
            b.WithIcon(ImageSource.FromFile(tempIcon));
            b.EnablePreview();
            var installer = (InstellaInstallerImpl)b.Build();

            var log = new CapturingLogger();
            var stdout = new StringWriter();
            PreviewHostLauncher launcher = (t, pages, states, ip) => PreviewHostOutcome.Completed;
            var runner = new PreviewModeRunner(installer.ConfigForTests, log, stdout, launcher);

            var args = new PreviewCliArgs(true, InstallerMode.Manage, PreviewSpeed.Fast, null);
            await runner.RunAsync(args, CancellationToken.None);

            Assert.That(log.Warns, Has.None.Contain(tempIcon));
        }
        finally
        {
            if (File.Exists(tempIcon)) File.Delete(tempIcon);
        }
    }
}
