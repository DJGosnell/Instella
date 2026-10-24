using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.Installation;
using Instella.Core.Manifest;
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Runtime.Runners;
using Instella.Installer.Runtime.UI.Widgets;
using Instella.Installer.Testing;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Services;

/// <summary>
/// The account surface around the built-in <c>service-account</c> page: the new
/// <see cref="ServiceAccount"/> kinds, the reserved flags, the masked <see cref="PasswordInput"/>
/// widget whose value never enters <see cref="PageState"/>, when the page is shown, and the check it
/// runs when the user presses Next.
/// </summary>
[TestFixture]
public class ServiceAccountPageTests
{
    private const string User = @"CORP\svc-app";
    private const string Password = "Pa55-w0rd-7f3c9e1d";

    private static InstallerBuilder App() => InstellaInstaller.Create()
        .WithApp("App", "com.example.app", new Version(1, 0))
        .WithExecutableName("App.exe")
        .WithElevation(ElevationMode.SystemWide);

    private static FrozenConfig Config(Action<WindowsBuilder> windows) =>
        ((InstellaInstallerImpl)App().OnWindows(windows).Build()).ConfigForTests;

    private static InstalledManifest Existing(string? account) => new()
    {
        AppName = "App",
        AppId = "com.example.app",
        Version = new Version(0, 9, 0),
        InstallDirectory = @"C:\App",
        ExecutableName = "App.exe",
        InstalledAt = DateTime.UtcNow,
        Files = [],
        Services = [new ManifestService("App.Svc", "App.exe", account, ManifestService.Automatic, true)],
    };

    // ---- ServiceAccount ----

    [Test]
    public void ManagedServiceAccount_KeepsTheName()
    {
        Assert.That(ServiceAccount.ManagedServiceAccount(@"CORP\app$").ToString(), Does.Contain(@"CORP\app$"));
    }

    [TestCase("")]
    [TestCase(@"CORP\app")]
    [TestCase("app$")]
    public void ManagedServiceAccount_RefusesANameThatIsNotDomainBackslashNameDollar(string name)
    {
        Assert.Throws<ArgumentException>(() => ServiceAccount.ManagedServiceAccount(name));
    }

    [Test]
    public void FromInstaller_AcceptsNoDefaultOrADefaultUser()
    {
        Assert.That(ServiceAccount.FromInstaller(), Is.Not.Null);
        Assert.That(ServiceAccount.FromInstaller(User), Is.Not.Null);
    }

    // ---- reserved flags ----

    [TestCase("service-account")]
    [TestCase("--service-account")]
    [TestCase("service-password-file")]
    [TestCase("--service-password-file")]
    public void TheServiceAccountFlags_AreReserved(string name)
    {
        var builder = App();
        builder.AddCliFlag<string>(name, defaultValue: "");

        var ex = Assert.Throws<InvalidOperationException>(() => builder.Build());
        Assert.That(ex!.Message, Does.Contain("reserved"));
    }

    [Test]
    public void APlainServicePasswordFlag_IsNotReserved_ItSimplyDoesNotExist()
    {
        Assert.That(ReservedCliFlags.All, Does.Contain("--service-account").And.Contain("--service-password-file"));
        Assert.That(ReservedCliFlags.All, Does.Not.Contain("--service-password"));
    }

    // ---- PasswordInput ----

    [Test]
    public void PageBuilder_PasswordInput_AddsAMaskedWidget()
    {
        var spec = new PageBuilder("p").PasswordInput("pw", "Password:").Build();

        var widget = spec.Widgets.OfType<PasswordInput>().Single();
        Assert.That(widget.Id, Is.EqualTo("pw"));
        Assert.That(widget.Label, Is.EqualTo("Password:"));
    }

    [Test]
    public void MapCliFlag_OntoAPasswordInput_IsRefusedByBuild()
    {
        var builder = App()
            .AddPage("creds", p => p.PasswordInput("pw", "Password:"))
            .AddCliFlag<string>("pw")
            .MapCliFlag("pw", "creds.pw");

        var ex = Assert.Throws<InvalidOperationException>(() => builder.Build());
        Assert.That(ex!.Message, Does.Contain("creds.pw").And.Contain("PasswordInput"));
    }

    [Test]
    public void SeedingAPage_PutsNothingInPageState_ForAPasswordInput()
    {
        var spec = new PageBuilder("p").TextInput("user", "User:", "alice").PasswordInput("pw", "Password:").Build();
        var state = new PageState();

        PreviewModeRunner.SeedWidgetDefaults(spec, state);

        Assert.That(state.Snapshot().Keys, Is.EquivalentTo(new[] { "user" }));
    }

    [Test]
    public void AnEnteredPassword_IsNotReadableThroughPageState_AndIsClearedAfterUse()
    {
        var state = new PageState();
        var buffer = Password.ToCharArray();

        state.SetSecret("pw", buffer);

        Assert.Multiple(() =>
        {
            Assert.That(state.Text("pw"), Is.Empty);
            Assert.That(state.Get<object?>("pw", null), Is.Null);
            Assert.That(state.TryGet<object?>("pw", out _), Is.False);
            Assert.That(state.Snapshot().Keys, Does.Not.Contain("pw"));
            Assert.That(string.Join("|", state.Snapshot().Values), Does.Not.Contain(Password));
        });
        string? seen = null;
        state.UsePassword("pw", span => seen = new string(span));
        Assert.That(seen, Is.EqualTo(Password), "the author reads it through UsePassword only");

        state.ClearSecrets();

        Assert.That(buffer, Has.All.EqualTo('\0'), "the buffer is zeroed");
        state.UsePassword("pw", span => seen = new string(span));
        Assert.That(seen, Is.Empty);
    }

    // ---- when the built-in page is shown ----

    [Test]
    public void ThePage_IsShownOnAFirstInstall_WhenAServiceUsesFromInstaller()
    {
        var config = Config(w => w.AddService("App.Svc", s => s.RunAs(ServiceAccount.FromInstaller())));

        Assert.That(ServiceAccountPage.IsShown(config, InstallerMode.FirstInstall, existing: null), Is.True);
    }

    [Test]
    public void ThePage_IsNeverShown_WithoutFromInstaller()
    {
        var config = Config(w => w.AddService("App.Svc", s => s.RunAs(ServiceAccount.ManagedServiceAccount(@"CORP\app$"))));

        Assert.That(ServiceAccountPage.IsShown(config, InstallerMode.FirstInstall, existing: null), Is.False);
    }

    [TestCase(InstallerMode.Upgrade)]
    [TestCase(InstallerMode.Repair)]
    public void ThePage_IsNotShown_OnUpgradeOrRepair_WhenTheInstallationRecordsACustomAccount(InstallerMode mode)
    {
        var config = Config(w => w.AddService("App.Svc", s => s.RunAs(ServiceAccount.FromInstaller())));

        Assert.That(ServiceAccountPage.IsShown(config, mode, Existing(User)), Is.False);
    }

    [TestCase(InstallerMode.Upgrade)]
    [TestCase(InstallerMode.Repair)]
    public void ThePage_IsShown_OnUpgradeOrRepair_WhenTheInstallationRecordsAnotherKindOfAccount(InstallerMode mode)
    {
        var config = Config(w => w.AddService("App.Svc", s => s.RunAs(ServiceAccount.FromInstaller())));

        Assert.That(ServiceAccountPage.IsShown(config, mode, Existing(account: null)), Is.True,
            "the service ran as LocalSystem before: the installer must ask for the new account");
    }

    // ---- the check run on Next ----

    private static Task<string?> Check(FakeCredentialChecker checker, RecordingLogger log, string user, string password, string confirm) =>
        ServiceAccountPage.CheckAsync(user, password.ToCharArray(), confirm.ToCharArray(), checker, log, CancellationToken.None);

    [Test]
    public async Task ThePage_Blocks_UntilBothPasswordsMatchAndAreNotEmpty_WithoutTryingALogon()
    {
        var checker = new FakeCredentialChecker();
        checker.AddUser(User, Password);
        var log = new RecordingLogger(new RecordingSink());

        Assert.That(await Check(checker, log, User, "", ""), Is.Not.Null);
        Assert.That(await Check(checker, log, User, Password, Password + "x"), Is.Not.Null.And.Contain("match"));
        Assert.That(await Check(checker, log, "", Password, Password), Is.Not.Null);
        Assert.That(checker.Checks, Is.EqualTo(0), "no logon is tried until the fields are complete");
    }

    [Test]
    public async Task ABadUserNameOrPassword_BlocksThePage_WithTheReason()
    {
        var checker = new FakeCredentialChecker();
        checker.AddUser(User, Password);
        var log = new RecordingLogger(new RecordingSink());

        var reason = await Check(checker, log, User, "wrong-one", "wrong-one");

        Assert.That(reason, Is.Not.Null.And.Contain("user name or password").IgnoreCase);
        Assert.That(checker.Checks, Is.EqualTo(1));
    }

    [Test]
    public async Task RightCredentials_LetThePageGo()
    {
        var checker = new FakeCredentialChecker();
        checker.AddUser(User, Password);

        Assert.That(await Check(checker, new RecordingLogger(new RecordingSink()), User, Password, Password), Is.Null);
    }

    [Test]
    public async Task ALogonTypeNotGranted_IsAccepted_AndLogged()
    {
        var checker = new FakeCredentialChecker();
        checker.AddUser(User, Password);
        checker.DenyLogonType(User);
        var sink = new RecordingSink();

        var reason = await Check(checker, new RecordingLogger(sink), User, Password, Password);

        Assert.That(reason, Is.Null, "a refusal other than a bad user name or password does not block");
        Assert.That(sink.Entries.Select(e => e.Message), Has.Some.Contain(User));
    }

    [Test]
    public async Task TheCheck_NeverLogsThePassword()
    {
        var checker = new FakeCredentialChecker();
        checker.AddUser(User, "something-else");
        var sink = new RecordingSink();
        var log = new RecordingLogger(sink);

        await Check(checker, log, User, Password, Password);
        checker.DenyLogonType(User);
        await Check(checker, log, User, Password, Password);

        Assert.That(string.Join("\n", sink.Entries.Select(e => e.Message + " " + e.Exception)), Does.Not.Contain(Password));
    }
}
