using System.Net;
using System.Security.Cryptography;
using Instella.Core.Installation;
using Instella.Core.Manifest;
using Instella.Core.Trust;
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Runtime.Core;
using Instella.Installer.Runtime.Installation;
using Instella.Installer.Runtime.Runners;
using Instella.Installer.Runtime.Tests.Installation;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests;

[TestFixture]
public class TrustBuilderTests
{
    private static string NewPublicKey()
    {
        using var key = ReleaseKeys.Generate();
        return ReleaseKeys.PublicKeyOf(key).PublicKey;
    }

    private static InstallerBuilder App() => InstellaInstaller.Create().WithApp("App", "com.example.app", new Version(1, 0, 0));

    [Test]
    public void WithServer_WithoutPublisherKey_FailsTheBuild()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => App().WithServer("https://updates.example.com").Build());
        Assert.That(ex!.Message, Does.Contain("WithPublisherKey"));
    }

    [Test]
    public void WithServer_WithPublisherKey_BuildsAndCarriesKeysIntoTheManifest()
    {
        var pub = NewPublicKey();
        var installer = (InstellaInstallerImpl)App().WithServer("https://updates.example.com").WithPublisherKey(pub).Build();
        var manifest = installer.ConfigForTests.ToManifest();
        Assert.That(manifest.PublisherKeys!.Single().PublicKey, Is.EqualTo(pub));
        Assert.That(manifest.PublisherKeys!.Single().KeyId, Has.Length.EqualTo(16));
    }

    [Test]
    public void AllowUnsignedUpdates_IsAnExplicitEscapeHatch()
    {
        var installer = (InstellaInstallerImpl)App().WithServer("https://updates.example.com").AllowUnsignedUpdates().Build();
        Assert.That(installer.ConfigForTests.ToManifest().AllowUnsignedUpdates, Is.True);
    }

    [Test]
    public void NoServer_NeedsNoKey()
    {
        Assert.DoesNotThrow(() => App().Build());
    }

    [TestCase("http://updates.example.com", false, false)]
    [TestCase("http://updates.example.com", true, true)]
    [TestCase("http://localhost:5000", false, true)]
    [TestCase("not a url", false, false)]
    public void ServerUrl_HttpsRule(string url, bool allowInsecure, bool builds)
    {
        var b = App().WithServer(url).WithPublisherKey(NewPublicKey());
        if (allowInsecure) b.AllowInsecureServer();
        if (builds) Assert.DoesNotThrow(() => b.Build());
        else Assert.Throws<InvalidOperationException>(() => b.Build());
    }

    [Test]
    public void WithPublisherKey_RejectsGarbage()
    {
        Assert.Throws<ArgumentException>(() => App().WithPublisherKey("definitely-not-a-key"));
    }

    [Test]
    public void Prerequisite_DownloadWithoutHash_FailsTheBuild()
    {
        Assert.Throws<InvalidOperationException>(() =>
            App().WithPrerequisite(p => p.WithName("Runtime").WithDownloadUrl("https://example.com/setup.exe")).Build());
    }

    [Test]
    public void Manifest_Validate_EnforcesTheHttpsRule()
    {
        var manifest = new InstellaManifest
        {
            AppName = "App", AppId = "com.example.app", Version = new Version(1, 0, 0), ServerUrl = "http://updates.example.com",
        };
        Assert.Throws<InvalidManifestException>(manifest.Validate);
        Assert.DoesNotThrow((manifest with { AllowInsecureServer = true }).Validate);
    }
}

[TestFixture]
public class PrerequisiteInstallerTests
{
    private static readonly byte[] InstallerBytes = "MZ fake installer"u8.ToArray();
    private static string InstallerSha => Convert.ToHexStringLower(SHA256.HashData(InstallerBytes));

    private static (PrerequisiteInstaller Installer, List<string> Ran) Create(Prerequisite p, byte[] served, int exitCode = 0)
    {
        var (installer, ran, _) = CreateWithElevation(p, served, exitCode, canElevate: false, allowPrompt: false);
        return (installer, ran);
    }

    private static (PrerequisiteInstaller Installer, List<string> Ran, List<bool> Elevated) CreateWithElevation(
        Prerequisite p, byte[] served, int exitCode, bool canElevate, bool allowPrompt)
    {
        var ran = new List<string>();
        var elevated = new List<bool>();
        var http = new HttpClient(new FixedHandler(served));
        var installer = new PrerequisiteInstaller([p], new StubFileSystem(), http,
            run: (file, args, elevate, ct) => { ran.Add(file); elevated.Add(elevate); return Task.FromResult(exitCode); },
            isInstalled: _ => false,
            canElevate: () => canElevate)
        {
            AllowElevationPrompt = allowPrompt,
        };
        return (installer, ran, elevated);
    }

    private static Prerequisite Prereq(string? sha = null, IReadOnlyList<int>? codes = null) => new()
    {
        Name = "Runtime",
        DownloadUrl = "https://example.com/dl/setup.exe",
        Sha256 = sha ?? InstallerSha,
        SuccessExitCodes = codes ?? [0, 3010],
    };

    [Test]
    public async Task MatchingHash_RunsTheInstaller()
    {
        var (installer, ran) = Create(Prereq(), InstallerBytes);
        var result = await installer.InstallAsync(Prereq(), null, CancellationToken.None);
        Assert.That(ran, Has.Count.EqualTo(1));
        Assert.That(Path.GetFileName(ran[0]), Is.EqualTo("setup.exe"));
        Assert.That(result.RebootRequired, Is.False);
    }

    [Test]
    public void WrongHash_IsNeverExecuted()
    {
        var tampered = InstallerBytes.Append((byte)0).ToArray();
        var (installer, ran) = Create(Prereq(), tampered);
        Assert.ThrowsAsync<PrerequisiteIntegrityException>(() => installer.InstallAsync(Prereq(), null, CancellationToken.None));
        Assert.That(ran, Is.Empty);
    }

    [Test]
    public void DownloadWithoutHash_IsRefused()
    {
        var p = new Prerequisite { Name = "Runtime", DownloadUrl = "https://example.com/setup.exe" };
        var (installer, ran) = Create(p, InstallerBytes);
        Assert.ThrowsAsync<InvalidOperationException>(() => installer.InstallAsync(p, null, CancellationToken.None));
        Assert.That(ran, Is.Empty);
    }

    [Test]
    public async Task Exit3010_MeansSuccessWithReboot()
    {
        var (installer, _) = Create(Prereq(), InstallerBytes, exitCode: 3010);
        var result = await installer.InstallAsync(Prereq(), null, CancellationToken.None);
        Assert.That(result.RebootRequired, Is.True);
    }

    [Test]
    public void UnlistedExitCode_Fails()
    {
        var (installer, _) = Create(Prereq(), InstallerBytes, exitCode: 1603);
        Assert.ThrowsAsync<InvalidOperationException>(() => installer.InstallAsync(Prereq(), null, CancellationToken.None));
    }

    [Test]
    public async Task RequiresElevation_Unelevated_RunsThroughAUacPrompt()
    {
        var (installer, _, elevated) = CreateWithElevation(Prereq(), InstallerBytes, 0, canElevate: true, allowPrompt: true);
        await installer.InstallAsync(Prereq(), null, CancellationToken.None);
        Assert.That(elevated, Is.EqualTo(new[] { true }));
    }

    [Test]
    public void RequiresElevation_Unelevated_Silent_IsRefusedWithoutRunning()
    {
        var (installer, ran, _) = CreateWithElevation(Prereq(), InstallerBytes, 0, canElevate: true, allowPrompt: false);
        var ex = Assert.ThrowsAsync<PrerequisiteElevationException>(() => installer.InstallAsync(Prereq(), null, CancellationToken.None));
        Assert.That(ex!.Message, Does.Contain("administrator rights"));
        Assert.That(ran, Is.Empty);
    }

    [Test]
    public async Task AlreadyElevated_OrNotRequired_RunsDirectly()
    {
        var (elevatedProcess, _, e1) = CreateWithElevation(Prereq(), InstallerBytes, 0, canElevate: false, allowPrompt: false);
        await elevatedProcess.InstallAsync(Prereq(), null, CancellationToken.None);
        var notRequired = new Prerequisite
        {
            Name = "Runtime", DownloadUrl = "https://example.com/dl/setup.exe", Sha256 = InstallerSha, RequiresElevation = false,
        };
        var (userLevel, _, e2) = CreateWithElevation(notRequired, InstallerBytes, 0, canElevate: true, allowPrompt: false);
        await userLevel.InstallAsync(notRequired, null, CancellationToken.None);
        Assert.That(e1.Concat(e2), Is.EqualTo(new[] { false, false }));
    }

    [Test]
    public void FailedPrerequisiteStep_ExitsInstallPrereqFailed()
    {
        var prereqFailed = new ExecutionResult(false, "prerequisite 'Runtime' failed",
            [new StepExecutionRecord("prerequisites", InstallStage.Prereqs, StepOutcome.Failed, "boom")], ["cleanup warning"]);
        var otherFailed = new ExecutionResult(false, "x",
            [new StepExecutionRecord("prerequisites", InstallStage.Prereqs, StepOutcome.Succeeded),
             new StepExecutionRecord("extract-payload", InstallStage.Extract, StepOutcome.Failed, "x")], []);
        var withWarnings = otherFailed with { Warnings = ["left a file"] };

        Assert.That(InstallFailureExit.For(prereqFailed), Is.EqualTo(InstellaExitCode.InstallPrereqFailed));
        Assert.That(InstallFailureExit.For(otherFailed), Is.EqualTo(InstellaExitCode.InstallGeneralFailure));
        Assert.That(InstallFailureExit.For(withWarnings), Is.EqualTo(InstellaExitCode.InstallRollbackCompletedWithWarnings));
    }

    [Test]
    public async Task CustomSuccessCodes_AreHonoured()
    {
        var p = Prereq(codes: [0, 1641]);
        var (installer, _) = Create(p, InstallerBytes, exitCode: 1641);
        Assert.That((await installer.InstallAsync(p, null, CancellationToken.None)).RebootRequired, Is.False);
    }

    private sealed class FixedHandler(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
    }
}
