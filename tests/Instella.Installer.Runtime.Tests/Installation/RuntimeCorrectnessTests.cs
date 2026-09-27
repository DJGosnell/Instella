using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Installation;
using Instella.Core.Logging;
using Instella.Core.Platform;
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Runtime.Installation;
using Instella.Installer.Runtime.Installation.Builders;
using Instella.Installer.Runtime.Runners;
using Instella.Installer.Runtime.UI.Widgets;
using Instella.Installer.Testing;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Installation;

/// <summary>Cancel rollback, CLI plumbing into steps, silent-mode semantics.</summary>
[TestFixture]
public class RuntimeCorrectnessTests
{
    // ---- 6.1 Rollback must not inherit a cancelled token ----

    [Test]
    public async Task Cancel_RollsBackWithALiveToken()
    {
        var platform = new FakePlatformServices();
        var shortcut = new ShortcutInfo("App", @"C:\App\App.exe", null, null, ShortcutLocation.Desktop);
        using var cts = new CancellationTokenSource();

        var register = StepBuilder.Create("register")
            .Execute(async (ctx, p, ct) =>
            {
                await ctx.Platform.CreateShortcutAsync(shortcut, ct);
                await ctx.Platform.RegisterUninstallEntryAsync(
                    new UninstallEntryInfo("com.app", "App", "1.0", "Pub", @"C:\App", "icon", "x", null, 0, true), ct);
                return StepResult.Ok;
            })
            .Rollback(async (ctx, ct) =>
            {
                // A platform call that honours its token (as Task.Run(work, ct) does) would
                // never start if rollback inherited the cancelled install token.
                ct.ThrowIfCancellationRequested();
                await ctx.Platform.RemoveShortcutAsync(shortcut, ct);
                await ctx.Platform.UnregisterUninstallEntryAsync("com.app", true, ct);
            })
            .Build();
        var cancel = StepBuilder.Create("cancel")
            .Execute((ctx, p, ct) =>
            {
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(StepResult.Ok);
            })
            .NoRollbackNeeded("cancels")
            .Build();

        var context = new TestContextBuilder().Build();
        var withFake = new InstallContext
        {
            AppName = context.AppName, AppId = context.AppId, AppVersion = context.AppVersion,
            InstallPath = context.InstallPath, Mode = context.Mode, Scope = context.Scope,
            Manifest = context.Manifest, Options = context.Options, Platform = platform,
            FileSystem = context.FileSystem, Log = context.Log,
        };

        var result = await new StepExecutor([register, cancel]).ExecuteAsync(withFake, null, cts.Token);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Is.EqualTo("Installation cancelled"));
        Assert.That(result.Warnings, Is.Empty, "rollback must not fail because the install token was cancelled");
        Assert.That(platform.ShortcutsRemoved, Does.Contain(shortcut));
        Assert.That(platform.UninstallEntriesRemoved, Does.Contain(("com.app", true)));
    }

    // ---- 6.2 CLI flags reach steps; MapCliFlag is validated at Build() ----

    [Test]
    public async Task UnknownFlag_Exits40_AndPrintsHelp()
    {
        var installer = InstellaInstaller.Create().WithApp("App", "com.app", new Version(1, 0)).Build();
        var (exit, output) = await CaptureConsoleAsync(() => installer.RunAsync(["--install", "--definitely-not-a-flag"]));
        Assert.That(exit, Is.EqualTo((int)InstellaExitCode.UsageInvalidArgs));
        Assert.That(output, Does.Contain("--definitely-not-a-flag"), "the error names the flag");
        Assert.That(output, Does.Contain("Usage:").And.Contain("--silent"), "followed by the help text");
    }

    [Test]
    public async Task CorruptedFooter_Exits12_BeforeAnyNetworkAccess()
    {
        // A damaged installer never "heals" itself from a server.
        var dir = Directory.CreateTempSubdirectory("instella-corrupt-").FullName;
        try
        {
            var exe = Path.Combine(dir, "installer.exe");
            var manifest = Path.Combine(dir, "instella.json");
            var archive = Path.Combine(dir, "payload.zip");
            File.WriteAllBytes(exe, new byte[256]);
            File.WriteAllText(manifest, """{"appName":"App","appId":"com.app","version":"1.0.0","serverUrl":"https://updates.example.com"}""");
            using (var zip = System.IO.Compression.ZipFile.Open(archive, System.IO.Compression.ZipArchiveMode.Create))
                zip.CreateEntry("app.exe");
            Instella.Installer.Build.Tasks.PayloadAppender.AppendOfflinePayload(exe, manifest, archive);
            var bytes = File.ReadAllBytes(exe);
            bytes[100] ^= 0xFF;   // inside the hashed range
            File.WriteAllBytes(exe, bytes);

            var handler = new CountingHandler();
            var installer = (InstellaInstallerImpl)InstellaInstaller.Create()
                .WithApp("App", "com.app", new Version(1, 0))
                .WithServer("https://updates.example.com")
                .AllowUnsignedUpdates()
                .Build();
            Instella.Installer.Runtime.Core.EmbeddedResources.PayloadPathOverride = exe;

            var exit = await installer.RunAsync(["--install", "--silent", "--path", Path.Combine(dir, "app")],
                new InstallerServices(new FakePlatformServices(), new InMemoryFileSystem(), handler), CancellationToken.None);

            Assert.That(exit, Is.EqualTo((int)InstellaExitCode.InstallIntegrityFailed));
            Assert.That(handler.Calls, Is.Zero, "no network access was attempted");
        }
        finally
        {
            Instella.Installer.Runtime.Core.EmbeddedResources.PayloadPathOverride = null;
            Directory.Delete(dir, recursive: true);
        }
    }

    private static async Task<(int Exit, string Output)> CaptureConsoleAsync(Func<Task<int>> run)
    {
        var (stdout, stderr) = (Console.Out, Console.Error);
        using var capture = new StringWriter();
        Console.SetOut(capture);
        Console.SetError(capture);
        try
        {
            return (await run(), capture.ToString());
        }
        finally
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
        }
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }

    [Test]
    public async Task WrongFlagType_Exits40()
    {
        var installer = InstellaInstaller.Create().WithApp("App", "com.app", new Version(1, 0))
            .AddCliFlag<int>("seats", 1).Build();
        var exit = await installer.RunAsync(["--install", "--seats", "many"]);
        Assert.That(exit, Is.EqualTo((int)InstellaExitCode.UsageInvalidArgs));
    }

    [Test]
    public void StrictParse_AcceptsReservedAndDeclaredFlags_AndStopsAtExtraArgs()
    {
        var flags = new[] { new CliFlagSpec("seats", typeof(int), 1, null, null) };
        var cli = CliArgParser.Parse(
            ["--update", "--app-path", @"C:\x", "--seats=3", "--silent", "--extra-args", "--anything-goes"], flags, strict: true);
        Assert.That(cli.Get<int>("seats"), Is.EqualTo(3));
        Assert.That(cli.IsSilent, Is.True);
    }

    [TestCase("undeclared", "welcome.agree", "not declared")]
    [TestCase("accept", "nosuchpage.agree", "no page 'nosuchpage'")]
    [TestCase("accept", "welcome.nosuchwidget", "has no widget")]
    [TestCase("accept", "name", "cannot fill a TextInput")]
    [TestCase("accept", "dup", "ambiguous")]
    public void MapCliFlag_IsValidatedAtBuild(string mappedFlag, string key, string expected)
    {
        var builder = InstellaInstaller.Create().WithApp("App", "com.app", new Version(1, 0))
            .AddCliFlag<bool>("accept", false)
            .AddPage("welcome", p => p.CheckBox("agree", "I agree").TextInput("name", "Name").CheckBox("dup", "d"))
            .AddPage("other", p => p.CheckBox("dup", "d"))
            .MapCliFlag(mappedFlag, key);

        var ex = Assert.Throws<InvalidOperationException>(() => builder.Build());
        Assert.That(ex!.Message, Does.Contain(expected));
    }

    [Test]
    public void MapCliFlag_UnqualifiedUniqueKey_ResolvesToPageAndWidget()
    {
        var config = Config(b => b.AddCliFlag<bool>("accept", false).MapCliFlag("accept", "agree")
            .AddPage("welcome", p => p.CheckBox("agree", "I agree")));
        Assert.That(config.DeclaredCliFlags.Single().MapsTo, Is.EqualTo("welcome.agree"));
    }

    [Test]
    public void MappedFlag_PrefillsPageState()
    {
        var config = Config(b => b.AddCliFlag<string>("edition", "").MapCliFlag("edition", "setup.edition")
            .AddPage("setup", p => p.Dropdown("edition", "Edition", "Home", "Pro")));
        var cli = CliArgParser.Parse(["--edition", "Pro"], config.DeclaredCliFlags, strict: true);
        var state = new PageState();

        PageFlow.InitializeState(config.Pages.Single(), state, cli, config.DeclaredCliFlags);

        Assert.That(state.Text("edition"), Is.EqualTo("Pro"));
    }

    // ---- 6.3 Silent mode answers pages or exits 14 ----

    [Test]
    public void Silent_LicenceGateWithoutFlag_Throws_NamingPageAndFlag()
    {
        var config = LicenceConfig();
        var ex = Assert.ThrowsAsync<SilentMissingStateException>(() => ResolveSilent(config, []));
        Assert.That(ex!.Message, Does.Contain("page 'license'"));
        Assert.That(ex.Message, Does.Contain("--accept-license"));
    }

    [Test]
    public async Task Silent_LicenceGateWithFlag_Passes_AndStepsSeeTheAnswer()
    {
        var config = LicenceConfig();
        var pages = await ResolveSilent(config, ["--accept-license"]);
        Assert.That(pages["license"].Bool("agree"), Is.True);
    }

    [Test]
    public void Silent_OnValidateFailure_Blocks()
    {
        var config = Config(b => b.AddCliFlag<string>("key", "").MapCliFlag("key", "reg.key")
            .AddPage("reg", p => p.TextInput("key", "Key")
                .OnValidate(s => s.Text("key").StartsWith("K-") ? ValidationResult.Ok : ValidationResult.Fail("key must start with K-"))));
        var ex = Assert.ThrowsAsync<SilentMissingStateException>(() => ResolveSilent(config, ["--key", "nope"]));
        Assert.That(ex!.Message, Does.Contain("key must start with K-"));
    }

    [Test]
    public async Task Silent_RunsOnEnterAndOnLeave_AndSkipsPagesForOtherModes()
    {
        var calls = new List<string>();
        var config = Config(b => b
            .AddPage("shown", p => p.Paragraph("hi")
                .OnEnter((ctx, ct) => { calls.Add("enter"); return Task.CompletedTask; })
                .OnLeave((ctx, ct) => { calls.Add("leave"); return Task.CompletedTask; }))
            .AddPage("upgrade-only", p => p.CheckBox("x", "x").ContinueWhen(s => s.Bool("x")).InModes(InstallerMode.Upgrade)));

        await ResolveSilent(config, []);

        Assert.That(calls, Is.EqualTo(new[] { "enter", "leave" }));
    }

    [Test]
    public async Task SilentInstall_WithoutLicenceFlag_Exits14()
    {
        var config = LicenceConfig();
        var runner = new InstallModeRunner(config, new NullLogger(), new FakePlatformServices(), new InMemoryFileSystem());
        var args = new[] { "--silent", "--path", Path.Combine(Path.GetTempPath(), "never-created", Guid.NewGuid().ToString("N")) };
        var dispatch = ModeDispatcher.Resolve(args, siblingManifestProbe: false) with
        {
            Cli = CliArgParser.Parse(args, config.DeclaredCliFlags, strict: true),
        };

        var exit = await runner.RunAsync(dispatch, CancellationToken.None);

        Assert.That(exit, Is.EqualTo(InstellaExitCode.InstallSilentMissingState));
    }

    // ---- helpers ----

    private static FrozenConfig LicenceConfig() => Config(b => b
        .AddCliFlag<bool>("accept-license", false, "Accept the licence")
        .MapCliFlag("accept-license", "license.agree")
        .AddPage("license", p => p.ScrollableText("terms").CheckBox("agree", "I accept").ContinueWhen(s => s.Bool("agree"))));

    private static FrozenConfig Config(Func<InstallerBuilder, InstallerBuilder> configure)
    {
        var b = InstellaInstaller.Create().WithApp("App", "com.app", new Version(1, 0));
        return ((InstellaInstallerImpl)configure(b).Build()).ConfigForTests;
    }

    private static async Task<Dictionary<string, PageState>> ResolveSilent(FrozenConfig config, string[] args)
    {
        var cli = CliArgParser.Parse(args, config.DeclaredCliFlags, strict: true);
        var pages = new Dictionary<string, PageState>();
        var context = new TestContextBuilder().Build();
        await PageFlow.ResolveSilentlyAsync(config, InstallerMode.FirstInstall, cli, context, pages, CancellationToken.None);
        return pages;
    }

    private sealed class NullLogger : IInstellaLogger
    {
        public void Trace(string message) { }
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
        public IDisposable Scope(string segment) => new MemoryStream();
        public bool IsEnabled(InstellaLogLevel level) => false;
    }
}

/// <summary>A harness run never reaches the host's platform services.</summary>
[TestFixture]
[NonParallelizable]
public class HarnessIsolationTests
{
    [Test]
    public async Task RunFullAsync_NeverCreatesRealServices()
    {
        var original = InstellaInstallerImpl.RealServicesFactory;
        var realRequests = 0;
        InstellaInstallerImpl.RealServicesFactory = () =>
        {
            realRequests++;
            throw new InvalidOperationException("the real platform services must not be used");
        };
        try
        {
            var installer = InstellaInstaller.Create().WithApp("App", "com.app", new Version(1, 0)).Build();
            await using var harness = InstellaTestHarness.Create()
                .WithInstaller(installer)
                .WithCliArgs("--install", "--silent", "--path", Path.Combine(Path.GetTempPath(), "never", Guid.NewGuid().ToString("N")))
                .Build();

            var exit = await harness.RunFullAsync();

            Assert.That(realRequests, Is.Zero);
            // No embedded payload and no server: the run fails cleanly on the fakes.
            Assert.That(exit, Is.EqualTo((int)InstellaExitCode.InstallGeneralFailure));
        }
        finally
        {
            InstellaInstallerImpl.RealServicesFactory = original;
        }
    }

    [Test]
    public async Task AMachineWideInteractiveRun_RelaunchesThroughTheFake_AndOpensNoWindow()
    {
        // A harness run must never reach WindowsElevationService, which would show a real UAC prompt.
        var original = InstellaInstallerImpl.RealServicesFactory;
        InstellaInstallerImpl.RealServicesFactory = () => throw new InvalidOperationException("the real services must not be used");
        try
        {
            var installer = InstellaInstaller.Create().WithApp("App", "com.app", new Version(1, 0))
                .WithElevation(Instella.Core.Manifest.ElevationMode.SystemWide).Build();
            await using var harness = InstellaTestHarness.Create()
                .WithInstaller(installer)
                .WithCliArgs("--path", Path.Combine(Path.GetTempPath(), "never", Guid.NewGuid().ToString("N")))
                .Build();

            var exit = await harness.RunFullAsync();

            Assert.That(harness.Elevation.RelaunchRequests, Has.Count.EqualTo(1), "one elevated relaunch, recorded by the fake");
            Assert.That(harness.Elevation.RelaunchRequests[0], Does.Contain("--elevated-child").And.Contain("--scope"));
            Assert.That(exit, Is.EqualTo(0), "the fake's elevated copy exited 0");
            Assert.That(harness.ShownMessages, Is.Empty);
        }
        finally
        {
            InstellaInstallerImpl.RealServicesFactory = original;
        }
    }

    [Test]
    public async Task AnInteractiveRunThatNeedsAWindow_FailsCleanly_InsteadOfOpeningOne()
    {
        var installer = InstellaInstaller.Create().WithApp("App", "com.app", new Version(1, 0))
            .WithElevation(Instella.Core.Manifest.ElevationMode.PerUser).Build();
        await using var harness = InstellaTestHarness.Create()
            .WithInstaller(installer)
            .WithCliArgs("--path", Path.Combine(Path.GetTempPath(), "never", Guid.NewGuid().ToString("N")))
            .Build();

        var exit = await harness.RunFullAsync();

        // The wizard's host factory throws: the catch-all reports it, and no window opens.
        Assert.That(exit, Is.EqualTo((int)InstellaExitCode.InstallGeneralFailure));
        Assert.That(harness.ShownMessages.Single(), Does.Contain("interactive UI requested in a harness run"));
    }
}
