using Instella.Core.Installation;
using NUnit.Framework;

namespace Instella.Sdk.Tests;

/// <summary>
/// The SDK side of the launch contract: <see cref="InstellaUpgrade"/> parses exactly what the
/// installer and updater pass, writes progress lines they read, and maps every ending to the
/// reserved exit codes.
/// </summary>
[TestFixture]
public class InstellaUpgradeTests
{
    private const string Path = @"C:\Program Files\Example";

    private static AppUpgradeContext Context(AppUpgradeMode mode = AppUpgradeMode.Update, string? from = "1.0.0", string? to = "2.0.0") => new()
    {
        Mode = mode,
        FromVersion = from is null ? null : Version.Parse(from),
        ToVersion = to is null ? null : Version.Parse(to),
        Scope = AppUpgradeScope.Machine,
        InstallPath = Path,
        AppId = "com.example.app",
        ExtraArguments = ["--db", "data.db"],
    };

    private static async Task<(int Exit, string Output, string Error)> Run(
        IReadOnlyList<string> args,
        Func<AppUpgradeContext, IAppUpgradeProgress, CancellationToken, Task>? upgrade = null,
        Func<AppUpgradeContext, CancellationToken, Task>? uninstall = null)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await InstellaUpgrade.RunAsync(args.ToArray(), upgrade ?? ((_, _, _) => Task.CompletedTask), uninstall,
            output, error, CancellationToken.None);
        return (exit, output.ToString(), error.ToString());
    }

    [TestCase(AppUpgradeMode.FirstInstall, null, "1.0.0")]
    [TestCase(AppUpgradeMode.Upgrade, "1.0.0", "2.0.0")]
    [TestCase(AppUpgradeMode.Repair, "1.5.0", "1.5.0")]
    [TestCase(AppUpgradeMode.Downgrade, "2.0.0", "1.0.0")]
    [TestCase(AppUpgradeMode.Update, "1.0.0", "1.1.0")]
    [TestCase(AppUpgradeMode.Uninstall, "3.0.0", null)]
    public void ToArguments_RoundTripsThroughTryParse(AppUpgradeMode mode, string? from, string? to)
    {
        var context = Context(mode, from, to);
        Assert.That(InstellaUpgrade.TryParse(context.ToArguments(), out var parsed, out var error), Is.True, error);
        Assert.Multiple(() =>
        {
            Assert.That(parsed!.Mode, Is.EqualTo(mode));
            Assert.That(parsed.FromVersion, Is.EqualTo(context.FromVersion));
            Assert.That(parsed.ToVersion, Is.EqualTo(context.ToVersion));
            Assert.That(parsed.Scope, Is.EqualTo(AppUpgradeScope.Machine));
            Assert.That(parsed.InstallPath, Is.EqualTo(Path));
            Assert.That(parsed.AppId, Is.EqualTo("com.example.app"));
            Assert.That(parsed.ContractVersion, Is.EqualTo(1));
            Assert.That(parsed.ExtraArguments, Is.EqualTo(new[] { "--db", "data.db" }));
            Assert.That(parsed.IsDowngrade, Is.EqualTo(mode == AppUpgradeMode.Downgrade));
            Assert.That(parsed.IsRepair, Is.EqualTo(mode == AppUpgradeMode.Repair));
        });
    }

    [Test]
    public void WhatTheRuntimeBuilds_TheSdkParses()
    {
        // The Runtime starts the program with AppUpgradeContract.BuildArguments; the SDK must read every mode.
        foreach (var mode in Enum.GetValues<AppUpgradeLaunchMode>())
        {
            var args = AppUpgradeContract.BuildArguments(new AppUpgradeLaunch(mode,
                mode == AppUpgradeLaunchMode.FirstInstall ? null : new Version(1, 2),
                mode == AppUpgradeLaunchMode.Uninstall ? null : new Version(1, 3),
                InstallationScope.PerUser, "/opt/example", "com.example.app"), ["x"]);

            Assert.That(InstellaUpgrade.TryParse(args, out var parsed, out var error), Is.True, $"{mode}: {error}");
            Assert.That(parsed!.Mode.ToString(), Is.EqualTo(mode.ToString()), "the public and internal modes line up");
            Assert.That(parsed.Scope, Is.EqualTo(AppUpgradeScope.PerUser));
            Assert.That(parsed.ExtraArguments, Is.EqualTo(new[] { "x" }));
        }
    }

    [Test]
    public void ExtraArguments_MayRepeatContractNames()
    {
        var args = Context().ToArguments().Concat(["--mode", "fast"]).ToList();
        Assert.That(InstellaUpgrade.TryParse(args, out var parsed, out _), Is.True);
        Assert.That(parsed!.Mode, Is.EqualTo(AppUpgradeMode.Update));
        Assert.That(parsed.ExtraArguments, Is.EqualTo(new[] { "--db", "data.db", "--mode", "fast" }));
    }

    [TestCase(new[] { "--help" }, "first argument")]
    [TestCase(new[] { "--instella-upgrade", "--contract" }, "needs a value")]
    [TestCase(new[] { "--instella-upgrade", "--contract", "2", "--mode", "update" }, "newer than this Instella.Sdk supports")]
    [TestCase(new[] { "--instella-upgrade", "--contract", "x" }, "must be a contract version")]
    [TestCase(new[] { "--instella-upgrade", "--contract", "1", "--mode", "update" }, "--from is missing")]
    public void BadArguments_AreRefused(string[] args, string expected)
    {
        Assert.That(InstellaUpgrade.TryParse(args, out var context, out var error), Is.False);
        Assert.That(context, Is.Null);
        Assert.That(error, Does.Contain(expected));
    }

    [TestCase("--mode", "sideways", "unknown --mode")]
    [TestCase("--mode", "uninstall", "does not go with")]
    [TestCase("--from", "one", "must be versions")]
    [TestCase("--scope", "everyone", "--scope must be")]
    [TestCase("--app-id", "", "must not be empty")]
    public void ABadValue_IsRefused(string name, string value, string expected)
    {
        var args = Context().ToArguments().ToList();
        args[args.IndexOf(name) + 1] = value;
        Assert.That(InstellaUpgrade.TryParse(args, out _, out var error), Is.False);
        Assert.That(error, Does.Contain(expected));
    }

    [Test]
    public async Task AStartWithoutTheContract_ExitsThree_WithoutRunningTheHandler()
    {
        var ran = false;
        var (exit, _, error) = await Run([], (_, _, _) => { ran = true; return Task.CompletedTask; });
        Assert.That(exit, Is.EqualTo(3));
        Assert.That(ran, Is.False);
        Assert.That(error, Does.Contain("not meant to be started directly"));
    }

    [Test]
    public async Task ANewerContract_ExitsThree()
    {
        var newer = new AppUpgradeContext
        {
            Mode = AppUpgradeMode.Update, FromVersion = new Version(1, 0), ToVersion = new Version(2, 0),
            Scope = AppUpgradeScope.PerUser, InstallPath = Path, AppId = "com.example.app", ContractVersion = 2,
        };
        var (exit, _, error) = await Run(newer.ToArguments());
        Assert.That(exit, Is.EqualTo(3));
        Assert.That(error, Does.Contain("update Instella.Sdk"));
    }

    [Test]
    public async Task ASucceedingHandler_ExitsZero_AndProgressReaches100()
    {
        AppUpgradeContext? seen = null;
        var (exit, output, _) = await Run(Context().ToArguments(), (ctx, progress, _) =>
        {
            seen = ctx;
            progress.Report(40, "Migrating\r\norders");
            return Task.CompletedTask;
        });

        Assert.That(exit, Is.EqualTo(0));
        Assert.That(seen!.ToVersion, Is.EqualTo(new Version(2, 0, 0)));
        var lines = output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.That(lines, Is.EqualTo(new[] { "##instella progress 40 Migrating orders", "##instella progress 100" }));
        foreach (var line in lines)
            Assert.That(AppUpgradeContract.TryParseInstruction(line, out _, out _), Is.True, "the Runtime reads what the SDK writes");
    }

    [Test]
    public async Task AHandlerThatReports100_IsNotToldTwice()
    {
        var (_, output, _) = await Run(Context().ToArguments(), (_, progress, _) =>
        {
            progress.Report(100, "Done");
            return Task.CompletedTask;
        });
        Assert.That(output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries), Is.EqualTo(new[] { "##instella progress 100 Done" }));
    }

    [Test]
    public async Task AThrowingHandler_ExitsOne_WithTheExceptionOnStderr()
    {
        var (exit, _, error) = await Run(Context().ToArguments(), (_, _, _) => throw new InvalidOperationException("table locked"));
        Assert.That(exit, Is.EqualTo(1));
        Assert.That(error, Does.Contain("InvalidOperationException").And.Contain("table locked"));
    }

    [Test]
    public async Task ACancelledHandler_ExitsOne()
    {
        var (exit, _, _) = await Run(Context().ToArguments(), (_, _, _) => throw new OperationCanceledException());
        Assert.That(exit, Is.EqualTo(1));
    }

    [Test]
    public async Task ARefusingHandler_ExitsTwo_WithItsMessage()
    {
        var (exit, _, error) = await Run(Context(AppUpgradeMode.Downgrade, "2.0.0", "1.0.0").ToArguments(), (ctx, _, _) =>
            throw new AppUpgradeRefusedException($"cannot go back to {ctx.ToVersion}"));
        Assert.That(exit, Is.EqualTo(2));
        Assert.That(error.Trim(), Is.EqualTo("cannot go back to 1.0.0"));
    }

    [Test]
    public async Task Uninstall_RunsTheUninstallHandler_NotTheUpgrade()
    {
        var calls = new List<string>();
        var (exit, _, _) = await Run(Context(AppUpgradeMode.Uninstall, "2.0.0", null).ToArguments(),
            (_, _, _) => { calls.Add("upgrade"); return Task.CompletedTask; },
            (ctx, _) => { calls.Add($"uninstall {ctx.FromVersion}"); return Task.CompletedTask; });
        Assert.That(exit, Is.EqualTo(0));
        Assert.That(calls, Is.EqualTo(new[] { "uninstall 2.0.0" }));
    }

    [Test]
    public async Task Uninstall_WithoutAHandler_ExitsZero()
    {
        var (exit, output, _) = await Run(Context(AppUpgradeMode.Uninstall, "2.0.0", null).ToArguments());
        Assert.That(exit, Is.EqualTo(0));
        Assert.That(output, Does.Contain("no uninstall handler"));
    }

    [Test]
    public async Task AFailingUninstallHandler_ExitsOne()
    {
        var (exit, _, _) = await Run(Context(AppUpgradeMode.Uninstall, "2.0.0", null).ToArguments(),
            uninstall: (_, _) => throw new IOException("in use"));
        Assert.That(exit, Is.EqualTo(1));
    }

    [Test]
    public void IsInstellaInvocation_LooksAtTheFirstArgumentOnly()
    {
        Assert.That(InstellaUpgrade.IsInstellaInvocation(["--instella-upgrade"]), Is.True);
        Assert.That(InstellaUpgrade.IsInstellaInvocation(["--instella-uninstall"]), Is.True);
        Assert.That(InstellaUpgrade.IsInstellaInvocation(["x", "--instella-upgrade"]), Is.False);
        Assert.That(InstellaUpgrade.IsInstellaInvocation([]), Is.False);
    }
}
