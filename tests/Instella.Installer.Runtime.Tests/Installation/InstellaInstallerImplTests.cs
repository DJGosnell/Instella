using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Installation;
using Instella.Core.Logging;
using Instella.Core.Update;
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Runtime.Runners;
using Instella.Installer.Testing;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Installation;

/// <summary>
/// Integration-style tests that drive the full
/// <see cref="IInstellaInstaller.RunAsync"/> entry point. Tests redirect
/// <see cref="Console.Out"/> so the --help exercise does not litter the test
/// run's console output.
/// </summary>
[TestFixture]
public sealed class InstellaInstallerImplTests
{
    private static IInstellaInstaller BuildMinimal()
    {
        return InstellaInstaller.Create()
            .WithApp("TestApp", "com.test.app", new Version(1, 0, 0))
            .Build();
    }

    private static async Task<(int Exit, string Stdout)> RunAsyncCapturingStdout(IInstellaInstaller installer, string[] args, CancellationToken ct = default)
    {
        var originalOut = Console.Out;
        try
        {
            using var writer = new StringWriter();
            Console.SetOut(writer);
            var exit = await installer.RunAsync(args, ct);
            return (exit, writer.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [Test]
    public async Task RunAsync_helpFlag_returnsSuccess_and_printsHelp()
    {
        var installer = BuildMinimal();
        var (exit, stdout) = await RunAsyncCapturingStdout(installer, new[] { "--help" });

        Assert.That(exit, Is.EqualTo((int)InstellaExitCode.Success));
        Assert.That(stdout, Does.Contain("TestApp"));
        Assert.That(stdout, Does.Contain("Usage"));
        Assert.That(stdout, Does.Contain("--install"));
        Assert.That(stdout, Does.Contain("--uninstall"));
    }

    [Test]
    public async Task RunAsync_update_withoutRequiredArgs_returnsUsageInvalidArgs()
    {
        var installer = BuildMinimal();
        var (exit, _) = await RunAsyncCapturingStdout(installer, new[] { "--update" });
        Assert.That(exit, Is.EqualTo((int)InstellaExitCode.UsageInvalidArgs));
    }

    [Test]
    public async Task RunAsync_cleanup_withoutPath_returnsUsageInvalidArgs()
    {
        var installer = BuildMinimal();
        var (exit, _) = await RunAsyncCapturingStdout(installer, new[] { "--cleanup" });
        Assert.That(exit, Is.EqualTo((int)InstellaExitCode.UsageInvalidArgs));
    }

    [Test]
    public async Task RunAsync_cleanup_withoutTombstoneToken_refuses()
    {
        // Cleanup is started by uninstall with a tombstone token; by hand it refuses.
        var installer = BuildMinimal();
        var nonExistent = Path.Combine(Path.GetTempPath(), $"instella-cleanup-{Guid.NewGuid():N}");
        var (exit, _) = await RunAsyncCapturingStdout(installer, new[] { "--cleanup", "--path", nonExistent });
        Assert.That(exit, Is.EqualTo((int)InstellaExitCode.UsageInvalidArgs));
    }

    [Test]
    public async Task RunAsync_uninstall_missingManifest_returnsManifestMissing()
    {
        var installer = BuildMinimal();
        var emptyDir = Directory.CreateTempSubdirectory("instella-uninstall-test-");
        try
        {
            var (exit, _) = await RunAsyncCapturingStdout(installer, new[] { "--uninstall", "--path", emptyDir.FullName });
            Assert.That(exit, Is.EqualTo((int)InstellaExitCode.UninstallManifestMissing));
        }
        finally
        {
            try { emptyDir.Delete(recursive: true); } catch { }
        }
    }

    [Test]
    public async Task RunAsync_manage_noManifest_returnsSuccess()
    {
        var installer = BuildMinimal();
        var emptyDir = Directory.CreateTempSubdirectory("instella-manage-test-");
        try
        {
            var (exit, _) = await RunAsyncCapturingStdout(installer, new[] { "--manage", "--path", emptyDir.FullName });
            Assert.That(exit, Is.EqualTo((int)InstellaExitCode.Success));
        }
        finally
        {
            try { emptyDir.Delete(recursive: true); } catch { }
        }
    }

    // ---- the updater command line is lenient: a newer app may pass flags an older stub does not know ----

    [Test]
    public async Task Update_WithOptionsFromANewerSdk_RunsAndLogsThem()
    {
        var root = Path.Combine(Path.GetTempPath(), "instella-impl-tests", Guid.NewGuid().ToString("N"));
        var fs = new InMemoryFileSystem();
        var sink = new CapturingSink();
        var installer = InstellaInstaller.Create().WithApp("TestApp", "com.test.app", new Version(1, 0, 0))
            .ConfigureLogging(l => l.NoFile().AddSink(sink)).Build();
        var args = new UpdaterArgs
        {
            AppPath = root, AppExecutable = "app.exe", FromVersion = new Version(1, 0), ToVersion = new Version(1, 1), Silent = true,
        }.ToArgumentList().Concat(["--future-flag", "3", "--another"]).ToArray();

        var exit = await WithStubIn(root, () => ((InstellaInstallerImpl)installer).RunAsync(args,
            new InstallerServices(new FakePlatformServices(), fs), CancellationToken.None));

        // No manifest in the folder: the update itself fails (20), but not on the command line (40).
        Assert.That(exit, Is.EqualTo((int)InstellaExitCode.UpdateGeneralFailure));
        var ignored = sink.Entries.Single(e => e.Message.Contains("ignoring options"));
        Assert.That(ignored.Message, Does.Contain("--future-flag").And.Contain("--another"));
    }

    [Test]
    public async Task Recover_WithAnUnknownOption_Recovers()
    {
        var root = Path.Combine(Path.GetTempPath(), "instella-impl-tests", Guid.NewGuid().ToString("N"));
        var installer = BuildMinimal();

        var exit = await WithStubIn(root, () => ((InstellaInstallerImpl)installer).RunAsync(["--recover", "--silent", "--path", root, "--future"],
            new InstallerServices(new FakePlatformServices(), new InMemoryFileSystem()), CancellationToken.None));

        Assert.That(exit, Is.EqualTo((int)InstellaExitCode.Success));
    }

    [Test]
    public async Task FirstInstall_WithAnUnknownOption_StillExits40()
    {
        var (exit, _) = await RunAsyncCapturingStdout(BuildMinimal(), ["--frobnicate"]);
        Assert.That(exit, Is.EqualTo((int)InstellaExitCode.UsageInvalidArgs));
    }

    // ---- failures are visible, and never crash ---------------------------------------

    [Test]
    public async Task ARunnerThatThrows_Exits10_AndTellsTheUser()
    {
        var messages = new Runners.RecordingMessages();
        var platform = System.Reflection.DispatchProxy.Create<Instella.Core.Platform.IPlatformServices, ThrowingPlatform>();

        var exit = await ((InstellaInstallerImpl)BuildMinimal()).RunAsync(["--silent"],
            new InstallerServices(platform, new InMemoryFileSystem(), Messages: messages), CancellationToken.None);

        Assert.That(exit, Is.EqualTo((int)InstellaExitCode.InstallGeneralFailure));
        Assert.That(messages.Errors, Has.Count.EqualTo(1));
        Assert.That(messages.Errors[0].Text, Does.StartWith("Something went wrong: the platform broke"));
    }

    [Test]
    public async Task AnInvalidCommandLine_IsShown_WithTheHelpHint()
    {
        var messages = new Runners.RecordingMessages();

        var exit = await ((InstellaInstallerImpl)BuildMinimal()).RunAsync(["--frobnicate"],
            new InstallerServices(new FakePlatformServices(), new InMemoryFileSystem(), Messages: messages), CancellationToken.None);

        Assert.That(exit, Is.EqualTo((int)InstellaExitCode.UsageInvalidArgs));
        Assert.That(messages.Errors.Single().Text, Does.Contain("--frobnicate").And.EndWith("Run with --help for the options."));
    }

    [Test]
    public async Task UninstallOfAMissingInstallation_IsShown()
    {
        var root = Path.Combine(Path.GetTempPath(), "instella-impl-tests", Guid.NewGuid().ToString("N"));
        var messages = new Runners.RecordingMessages();

        var exit = await WithStubIn(root, () => ((InstellaInstallerImpl)BuildMinimal()).RunAsync(["--uninstall", "--path", root],
            new InstallerServices(new FakePlatformServices(), new InMemoryFileSystem(), Messages: messages), CancellationToken.None));

        Assert.That(exit, Is.EqualTo((int)InstellaExitCode.UninstallManifestMissing));
        Assert.That(messages.Errors.Single().Text, Does.StartWith($"No installation of TestApp was found in '{root}'."));
    }

    /// <summary>Every platform call throws.</summary>
    public class ThrowingPlatform : System.Reflection.DispatchProxy
    {
        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args) =>
            throw new InvalidOperationException("the platform broke");
    }

    private static async Task<int> WithStubIn(string folder, Func<Task<int>> run)
    {
        var saved = InstallPaths.StubDirectory;
        InstallPaths.StubDirectory = () => folder;
        try
        {
            return await run();
        }
        finally
        {
            InstallPaths.StubDirectory = saved;
        }
    }

    private sealed class CapturingSink : IInstellaLogSink
    {
        public List<InstellaLogEntry> Entries { get; } = new();
        public void Write(in InstellaLogEntry entry) { lock (Entries) Entries.Add(entry); }
        public ValueTask FlushAsync(CancellationToken cancellationToken) => default;
    }
}
