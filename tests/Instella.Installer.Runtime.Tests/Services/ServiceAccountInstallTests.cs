using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.FileSystem;
using Instella.Core.Installation;
using Instella.Core.Manifest;
using Instella.Core.Platform;
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Testing;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Services;

/// <summary>
/// Custom and group-managed service accounts (<c>ServiceAccount.FromInstaller</c>,
/// <c>ServiceAccount.ManagedServiceAccount</c>), installed silently through the harness: the reserved
/// <c>--service-account</c> / <c>--service-password-file</c> flags, the password environment variable,
/// exit 14 when the account or password is missing, the "Log on as a service" right, a wrong password
/// failing at <c>start-services</c> (1069, exit 16), an upgrade that keeps the account, and a password
/// that never reaches the log, stderr, the installed manifest or page state.
/// </summary>
[TestFixture, Platform("Win")]
[NonParallelizable]
public class ServiceAccountInstallTests
{
    private const string AppId = "com.example.app";
    private const string User = @"CORP\svc-app";
    private const string Password = "Pa55-w0rd-7f3c9e1d";
    private const string Gmsa = @"CORP\app-gmsa$";
    private InMemoryFileSystem _fs = null!;
    private FakePlatformServices _platform = null!;
    private readonly List<InstellaTestHarness> _harnesses = [];
    private string _root = null!;
    private string _installPath = null!;
    private string _passwordFile = null!;

    [SetUp]
    public void SetUp()
    {
        _fs = new InMemoryFileSystem();
        _platform = new FakePlatformServices();
        _root = Path.Combine(Path.GetTempPath(), "instella-service-accounts", Guid.NewGuid().ToString("N"));
        _installPath = Path.Combine(_root, "App");
        Directory.CreateDirectory(_root);
        _passwordFile = Path.Combine(_root, "password.txt");
    }

    [TearDown]
    public async Task TearDown()
    {
        foreach (var h in _harnesses) await h.DisposeAsync();
        _harnesses.Clear();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private FakeServiceManager Scm => _platform.ServiceManager;

    private static Dictionary<string, string> Files(string version) => new()
    {
        ["App.exe"] = $"app {version}",
        ["bin/App.Worker.exe"] = $"worker {version}",
    };

    private InstellaTestHarness Installer(string version, Action<WindowsBuilder> windows)
    {
        var builder = InstellaInstaller.Create()
            .WithApp("App", AppId, Version.Parse(version))
            .WithExecutableName("App.exe")
            .WithElevation(ElevationMode.SystemWide)
            .OnWindows(windows);
        var harness = InstellaTestHarness.Create()
            .WithInstaller(builder.Build())
            .WithFileSystem(_fs)
            .WithPlatformServices(_platform)
            .WithElevationService(new FakeElevationService(isElevated: true))
            .WithPayload(Files(version))
            .Build();
        _harnesses.Add(harness);
        return harness;
    }

    private static void CustomService(WindowsBuilder w) =>
        w.AddService("App.Svc", s => s.RunAs(ServiceAccount.FromInstaller()));

    private Task<int> Install(InstellaTestHarness harness, params string[] more) =>
        harness.RunFullWithArgsAsync(["--install", "--silent", "--path", _installPath, .. more]);

    private Task<int> InstallWithPasswordFile(InstellaTestHarness harness, string fileContent, string account = User)
    {
        File.WriteAllText(_passwordFile, fileContent);
        return Install(harness, "--service-account", account, "--service-password-file", _passwordFile);
    }

    private Task<InstalledManifest?> Manifest() => new InstallManifestWriter(_fs).ReadAsync(_installPath, CancellationToken.None);

    private string? ManifestText()
    {
        var path = Path.GetFullPath(Path.Combine(_installPath, ".instella-manifest.json"));
        return _fs.Snapshot().TryGetValue(path, out var bytes) ? Encoding.UTF8.GetString(bytes) : null;
    }

    private static string Log(InstellaTestHarness h) =>
        string.Join("\n", h.LogSink.Entries.Select(e => e.Message + " " + e.Exception));

    private static async Task<(T Result, string Stderr)> CaptureStderr<T>(Func<Task<T>> run)
    {
        var original = Console.Error;
        var writer = new StringWriter();
        Console.SetError(writer);
        try { return (await run(), writer.ToString()); }
        finally { Console.SetError(original); }
    }

    // ---- ServiceAccount.FromInstaller, silent ----

    [Test]
    public async Task FromInstaller_Silent_UsesTheServiceAccountFlag_AndThePasswordFile()
    {
        Scm.SetAccountPassword(User, Password);

        var exit = await InstallWithPasswordFile(Installer("1.0", CustomService), Password);

        Assert.That(exit, Is.EqualTo(0));
        var svc = Scm.Find("App.Svc")!;
        Assert.Multiple(() =>
        {
            Assert.That(svc.AccountName, Is.EqualTo(User));
            Assert.That(svc.PasswordSupplied, Is.True);
            Assert.That(svc.State, Is.EqualTo(FakeServiceState.Running), "the right password lets the service start");
            Assert.That(File.Exists(_passwordFile), Is.True, "the password file is left in place");
        });
        Assert.That((await Manifest())!.Services!.Single().AccountName, Is.EqualTo(User));
    }

    [Test]
    public async Task ThePasswordFile_HasOneTrailingNewlineTrimmed()
    {
        Scm.SetAccountPassword(User, Password);

        var exit = await InstallWithPasswordFile(Installer("1.0", CustomService), Password + "\r\n");

        Assert.That(exit, Is.EqualTo(0), "the trailing newline is not part of the password");
        Assert.That(Scm.Find("App.Svc")!.State, Is.EqualTo(FakeServiceState.Running));
    }

    [Test]
    public async Task EveryServiceUsingFromInstaller_SharesTheOneAccount()
    {
        Scm.SetAccountPassword(User, Password);

        var exit = await InstallWithPasswordFile(Installer("1.0", w => w
            .AddService("App.Svc", s => s.RunAs(ServiceAccount.FromInstaller()))
            .AddService("App.Worker", s => s.WithExecutable("bin/App.Worker.exe").RunAs(ServiceAccount.FromInstaller(@"CORP\other")))),
            Password);

        Assert.That(exit, Is.EqualTo(0));
        Assert.That(Scm.Find("App.Svc")!.AccountName, Is.EqualTo(User));
        Assert.That(Scm.Find("App.Worker")!.AccountName, Is.EqualTo(User));
        Assert.That(Scm.Find("App.Worker")!.PasswordSupplied, Is.True);
    }

    [Test]
    public async Task ALocalUserName_IsWrittenWithTheDotPrefix()
    {
        Scm.SetAccountPassword(@".\svc-app", Password);

        var exit = await InstallWithPasswordFile(Installer("1.0", CustomService), Password, account: "svc-app");

        Assert.That(exit, Is.EqualTo(0));
        Assert.That(Scm.Find("App.Svc")!.AccountName, Is.EqualTo(@".\svc-app"));
    }

    [Test]
    public async Task ThePassword_ComesFromTheEnvironmentVariable_NamedInTheBuilder()
    {
        var variable = "INSTELLA_TEST_PW_" + Guid.NewGuid().ToString("N");
        Scm.SetAccountPassword(User, Password);
        Environment.SetEnvironmentVariable(variable, Password);
        try
        {
            var harness = Installer("1.0", w => w
                .WithServicePasswordVariable(variable)
                .AddService("App.Svc", s => s.RunAs(ServiceAccount.FromInstaller())));

            var exit = await Install(harness, "--service-account", User);

            Assert.That(exit, Is.EqualTo(0));
            Assert.That(Scm.Find("App.Svc")!.State, Is.EqualTo(FakeServiceState.Running));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Test]
    public async Task ThePassword_ComesFromInstellaServicePassword_ByDefault()
    {
        Scm.SetAccountPassword(User, Password);
        var previous = Environment.GetEnvironmentVariable("INSTELLA_SERVICE_PASSWORD");
        Environment.SetEnvironmentVariable("INSTELLA_SERVICE_PASSWORD", Password);
        try
        {
            var exit = await Install(Installer("1.0", CustomService), "--service-account", User);

            Assert.That(exit, Is.EqualTo(0));
            Assert.That(Scm.Find("App.Svc")!.State, Is.EqualTo(FakeServiceState.Running));
        }
        finally
        {
            Environment.SetEnvironmentVariable("INSTELLA_SERVICE_PASSWORD", previous);
        }
    }

    [Test]
    public async Task Silent_WithoutAnAccount_Exits14_NamingTheFlag()
    {
        var harness = Installer("1.0", CustomService);

        var (exit, stderr) = await CaptureStderr(() => Install(harness));

        Assert.That(exit, Is.EqualTo((int)InstellaExitCode.InstallSilentMissingState));
        Assert.That(stderr, Does.Contain("--service-account"));
        Assert.That(Scm.Calls, Is.Empty, "nothing is registered");
    }

    [Test]
    public async Task Silent_WithAnAccountButNoPassword_Exits14_NamingTheFileFlagAndTheVariable()
    {
        var previous = Environment.GetEnvironmentVariable("INSTELLA_SERVICE_PASSWORD");
        Environment.SetEnvironmentVariable("INSTELLA_SERVICE_PASSWORD", null);
        try
        {
            var harness = Installer("1.0", CustomService);

            var (exit, stderr) = await CaptureStderr(() => Install(harness, "--service-account", User));

            Assert.That(exit, Is.EqualTo((int)InstellaExitCode.InstallSilentMissingState));
            Assert.That(stderr, Does.Contain("--service-password-file").And.Contain("INSTELLA_SERVICE_PASSWORD"));
            Assert.That(Scm.Calls, Is.Empty);
        }
        finally
        {
            Environment.SetEnvironmentVariable("INSTELLA_SERVICE_PASSWORD", previous);
        }
    }

    [Test]
    public async Task APlainServicePasswordFlag_DoesNotExist()
    {
        var exit = await Install(Installer("1.0", CustomService), "--service-account", User, "--service-password", Password);

        Assert.That(exit, Is.EqualTo((int)InstellaExitCode.UsageInvalidArgs));
        Assert.That(Scm.Calls, Is.Empty);
    }

    // ---- wrong password ----

    [Test]
    public async Task AWrongPassword_FailsAtStartServices_With1069InPlainWords_AndRollsBackWithExit16()
    {
        Scm.SetAccountPassword(User, Password);
        var harness = Installer("1.0", CustomService);

        var exit = await InstallWithPasswordFile(harness, "not-the-password");

        Assert.That(exit, Is.EqualTo((int)InstellaExitCode.InstallServiceFailed));
        Assert.That(Scm.Find("App.Svc"), Is.Null, "the service the install created is deleted");
        Assert.That(await Manifest(), Is.Null);
        Assert.That(Log(harness), Does.Contain("1069").And.Contain("user name or password").IgnoreCase);
        Assert.That(Log(harness), Does.Contain(@"the service 'App.Svc' could not log on as 'CORP\svc-app' (error 1069)"));
    }

    // ---- logon right ----

    [Test]
    public async Task ACustomAccount_IsGrantedLogOnAsAService_AndTheManifestRecordsIt()
    {
        Scm.SetAccountPassword(User, Password);

        Assert.That(await InstallWithPasswordFile(Installer("1.0", CustomService), Password), Is.EqualTo(0));

        Assert.That(_platform.LogonRights.Has(User), Is.True);
        Assert.That((await Manifest())!.GrantedLogonRights, Is.EqualTo(new[] { User }));
    }

    [Test]
    public async Task AnAccountThatAlreadyHasTheRight_IsNotGrantedIt_AndNothingIsRecorded()
    {
        Scm.SetAccountPassword(User, Password);
        _platform.LogonRights.Grant(User);

        Assert.That(await InstallWithPasswordFile(Installer("1.0", CustomService), Password), Is.EqualTo(0));

        Assert.That(_platform.LogonRights.Calls, Has.None.StartsWith("grant:"));
        Assert.That((await Manifest())!.GrantedLogonRights ?? [], Is.Empty);
    }

    [Test]
    public async Task ManagedServiceAccount_RunsWithoutAPassword_AndIsGrantedTheRight()
    {
        var exit = await Install(Installer("1.0", w => w.AddService("App.Svc", s => s.RunAs(ServiceAccount.ManagedServiceAccount(Gmsa)))));

        Assert.That(exit, Is.EqualTo(0));
        var svc = Scm.Find("App.Svc")!;
        Assert.That(svc.AccountName, Is.EqualTo(Gmsa));
        Assert.That(svc.PasswordSupplied, Is.False, "a gMSA has no password");
        Assert.That(_platform.LogonRights.Has(Gmsa), Is.True);
        Assert.That((await Manifest())!.GrantedLogonRights, Is.EqualTo(new[] { Gmsa }));
    }

    [Test]
    public async Task BuiltInAndVirtualAccounts_AreNeverGrantedTheRight()
    {
        var exit = await Install(Installer("1.0", w => w
            .AddService("App.Svc", s => s.RunAs(ServiceAccount.NetworkService))
            .AddService("App.Worker", s => s.WithExecutable("bin/App.Worker.exe").RunAs(ServiceAccount.VirtualAccount))));

        Assert.That(exit, Is.EqualTo(0));
        Assert.That(_platform.LogonRights.Calls, Is.Empty);
    }

    [Test]
    public async Task AFailedInstall_RevokesTheRightItGranted()
    {
        Scm.SetStartBehavior("App.Svc", FakeServiceBehavior.Fail);

        var exit = await Install(Installer("1.0", w => w.AddService("App.Svc", s => s.RunAs(ServiceAccount.ManagedServiceAccount(Gmsa)))));

        Assert.That(exit, Is.EqualTo((int)InstellaExitCode.InstallServiceFailed));
        Assert.That(_platform.LogonRights.Has(Gmsa), Is.False);
        Assert.That(_platform.LogonRights.Calls, Is.EqualTo(new[] { "grant:" + Gmsa, "revoke:" + Gmsa }));
    }

    [Test]
    public async Task AFailedInstall_LeavesARightTheAccountAlreadyHad()
    {
        Scm.SetStartBehavior("App.Svc", FakeServiceBehavior.Fail);
        _platform.LogonRights.Grant(Gmsa);

        var exit = await Install(Installer("1.0", w => w.AddService("App.Svc", s => s.RunAs(ServiceAccount.ManagedServiceAccount(Gmsa)))));

        Assert.That(exit, Is.EqualTo((int)InstellaExitCode.InstallServiceFailed));
        Assert.That(_platform.LogonRights.Has(Gmsa), Is.True);
        Assert.That(_platform.LogonRights.Calls, Has.None.StartsWith("revoke:"));
    }

    [Test]
    public async Task Uninstall_RevokesOnlyTheRightInstellaGranted()
    {
        var harness = Installer("1.0", w => w
            .AddService("App.Svc", s => s.RunAs(ServiceAccount.ManagedServiceAccount(Gmsa)))
            .AddService("App.Worker", s => s.WithExecutable("bin/App.Worker.exe").RunAs(ServiceAccount.ManagedServiceAccount(@"CORP\other$"))));
        _platform.LogonRights.Grant(@"CORP\other$");
        Assert.That(await Install(harness), Is.EqualTo(0));

        Assert.That(await harness.RunFullWithArgsAsync(["--uninstall", "--silent", "--path", _installPath]), Is.EqualTo(0));

        Assert.That(_platform.LogonRights.Has(Gmsa), Is.False, "the right Instella granted is revoked");
        Assert.That(_platform.LogonRights.Has(@"CORP\other$"), Is.True, "a right an administrator set up is kept");
    }

    // ---- upgrade / repair ----

    [Test]
    public async Task Upgrade_WithTheSameAccount_NeedsNoPassword_AndKeepsTheStoredSecret()
    {
        Scm.SetAccountPassword(User, Password);
        Assert.That(await InstallWithPasswordFile(Installer("1.0", CustomService), Password), Is.EqualTo(0));
        File.Delete(_passwordFile);
        var previous = Environment.GetEnvironmentVariable("INSTELLA_SERVICE_PASSWORD");
        Environment.SetEnvironmentVariable("INSTELLA_SERVICE_PASSWORD", null);
        try
        {
            var exit = await Install(Installer("2.0", CustomService));

            Assert.That(exit, Is.EqualTo(0), "no --service-account and no password are needed");
            var svc = Scm.Find("App.Svc")!;
            Assert.That(svc.AccountName, Is.EqualTo(User));
            Assert.That(svc.PasswordSupplied, Is.True, "the service manager keeps the stored secret");
            Assert.That(svc.State, Is.EqualTo(FakeServiceState.Running));
            Assert.That((await Manifest())!.GrantedLogonRights, Is.EqualTo(new[] { User }), "the grant stays recorded");
        }
        finally
        {
            Environment.SetEnvironmentVariable("INSTELLA_SERVICE_PASSWORD", previous);
        }
    }

    // ---- the password never leaks ----

    [Test]
    public async Task ThePassword_NeverAppearsInTheLog_Stderr_TheManifest_OrPageState()
    {
        Scm.SetAccountPassword(User, Password);
        var harness = Installer("1.0", CustomService);

        var (exit, stderr) = await CaptureStderr(() => InstallWithPasswordFile(harness, Password));

        Assert.That(exit, Is.EqualTo(0));
        Assert.Multiple(() =>
        {
            Assert.That(Log(harness), Does.Not.Contain(Password));
            Assert.That(stderr, Does.Not.Contain(Password));
            Assert.That(ManifestText(), Is.Not.Null.And.Not.Contain(Password));
            foreach (var (page, state) in harness.PageStates)
                Assert.That(string.Join("\n", state.Snapshot().Select(kv => kv.Key + "=" + kv.Value)), Does.Not.Contain(Password), page);
        });
    }

    [Test]
    public async Task AFailedInstall_NeverLogsThePassword_Either()
    {
        Scm.SetAccountPassword(User, "the-real-one");
        var harness = Installer("1.0", CustomService);

        var (exit, stderr) = await CaptureStderr(() => InstallWithPasswordFile(harness, Password));

        Assert.That(exit, Is.EqualTo((int)InstellaExitCode.InstallServiceFailed));
        Assert.That(Log(harness), Does.Not.Contain(Password));
        Assert.That(stderr, Does.Not.Contain(Password));
    }

    // ---- silent upgrade: the recorded account over the default user ----

    private const string DefaultUser = @"CORP\default-svc";

    private static void CustomServiceWithDefault(WindowsBuilder w) =>
        w.AddService("App.Svc", s => s.RunAs(ServiceAccount.FromInstaller(DefaultUser)));

    [Test]
    public async Task SilentUpgrade_KeepsTheRecordedAccount_OverTheDefaultUser_WithoutAPassword()
    {
        Scm.SetAccountPassword(User, Password);
        Assert.That(await InstallWithPasswordFile(Installer("1.0", CustomServiceWithDefault), Password), Is.EqualTo(0));
        var previous = Environment.GetEnvironmentVariable("INSTELLA_SERVICE_PASSWORD");
        Environment.SetEnvironmentVariable("INSTELLA_SERVICE_PASSWORD", null);
        try
        {
            var upgrade = await Install(Installer("2.0", CustomServiceWithDefault));

            Assert.That(upgrade, Is.EqualTo(0), "a silent upgrade that keeps the installation's account needs no password");
            Assert.That(Scm.Find("App.Svc")!.AccountName, Is.EqualTo(User));
        }
        finally
        {
            Environment.SetEnvironmentVariable("INSTELLA_SERVICE_PASSWORD", previous);
        }
    }

    [Test]
    public async Task SilentUpgrade_KeepsTheRecordedAccount_LikeTheWizard_WhenItDiffersFromTheDefaultUser()
    {
        // With the password variable set, the default user must not take over the service either.
        Scm.SetAccountPassword(User, Password);
        Scm.SetAccountPassword(DefaultUser, "default-pa55");
        Assert.That(await InstallWithPasswordFile(Installer("1.0", CustomServiceWithDefault), Password), Is.EqualTo(0));
        var previous = Environment.GetEnvironmentVariable("INSTELLA_SERVICE_PASSWORD");
        Environment.SetEnvironmentVariable("INSTELLA_SERVICE_PASSWORD", "default-pa55");
        try
        {
            var exit = await Install(Installer("2.0", CustomServiceWithDefault));

            Assert.That(exit, Is.EqualTo(0), "a silent upgrade keeps the recorded account and needs no password, as the wizard does");
            var svc = Scm.Find("App.Svc")!;
            Assert.That(svc.AccountName, Is.EqualTo(User));
            Assert.That(svc.State, Is.EqualTo(FakeServiceState.Running), "the stored secret is kept");
        }
        finally
        {
            Environment.SetEnvironmentVariable("INSTELLA_SERVICE_PASSWORD", previous);
        }
    }

    // ---- the password file ----

    [Test]
    public async Task AnEmptyPasswordFile_OnAnUpgrade_Exits14_NamingTheFile()
    {
        Scm.SetAccountPassword(User, Password);
        Assert.That(await InstallWithPasswordFile(Installer("1.0", CustomService), Password), Is.EqualTo(0));
        File.WriteAllText(_passwordFile, "");
        var calls = Scm.Calls.Count;

        var (exit, stderr) = await CaptureStderr(() => Install(Installer("2.0", CustomService), "--service-password-file", _passwordFile));

        Assert.That(exit, Is.EqualTo((int)InstellaExitCode.InstallSilentMissingState));
        Assert.That(stderr, Does.Contain(_passwordFile).And.Contain("is empty"));
        Assert.That(Scm.Calls.Count, Is.EqualTo(calls), "nothing is changed before the check");
        Assert.That((await Manifest())!.Version, Is.EqualTo(Version.Parse("1.0.0")));
    }

    [Test]
    public async Task AnUnreadablePasswordFile_Exits14_NamingThePath_BeforeAnythingIsWritten()
    {
        var missing = Path.Combine(_root, "missing.txt");

        var (exit, stderr) = await CaptureStderr(() =>
            Install(Installer("1.0", CustomService), "--service-account", User, "--service-password-file", missing));

        Assert.That(exit, Is.EqualTo((int)InstellaExitCode.InstallSilentMissingState));
        Assert.That(stderr, Does.Contain(missing).And.Contain("could not be read"));
        Assert.That(Scm.Calls, Is.Empty);
        Assert.That(await Manifest(), Is.Null);
    }

    // ---- an upgrade that changes the account ----

    [Test]
    public async Task AnUpgradeThatChangesTheAccount_AndFailsToStart_SaysThePreviousPasswordCannotBeRestored()
    {
        const string other = @"CORP\svc-other";
        Scm.SetAccountPassword(User, Password);
        Scm.SetAccountPassword(other, "other-pa55");
        Assert.That(await InstallWithPasswordFile(Installer("1.0", CustomService), Password), Is.EqualTo(0));
        var harness = Installer("2.0", CustomService);

        var exit = await InstallWithPasswordFile(harness, "not-the-password", other);

        Assert.That(exit, Is.EqualTo((int)InstellaExitCode.InstallServiceFailed));
        Assert.That(Scm.Find("App.Svc")!.AccountName, Is.EqualTo(User), "rollback puts the previous account back");
        Assert.That(Log(harness), Does.Contain($"'App.Svc' runs as '{User}' again").And.Contain("cannot be restored"));
    }
}
