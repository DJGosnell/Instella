using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Instella.Core.FileSystem;
using Instella.Core.Installation;
using Instella.Core.Logging;
using Instella.Core.Manifest;
using Instella.Core.Platform;
using Instella.Core.Trust;
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Runtime.Core;
using Instella.Installer.Runtime.Installation;
using Instella.Installer.Runtime.Installation.Builders;
using Instella.Installer.Runtime.Installation.BuiltIn;
using Instella.Installer.Runtime.Runners;
using Instella.Installer.Testing;
using NUnit.Framework;

namespace Instella.Installer.Runtime.Tests.Transactions;

/// <summary>
/// Running an installer over an existing installation is a transactional
/// upgrade or repair, and a failed upgrade leaves the previous version fully working.
/// </summary>
[TestFixture]
public class InstallOverExistingTests
{
    private const string AppId = "com.test.quicknotes";
    private static readonly Version V1 = new(1, 0, 0);
    private static readonly Version V2 = new(2, 0, 0);

    private string _root = null!;
    private InMemoryFileSystem _fs = null!;
    private FakePlatformServices _platform = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "instella-over-tests", Guid.NewGuid().ToString("N"), "QuickNotes");
        _fs = new InMemoryFileSystem();
        _platform = new FakePlatformServices();
    }

    [Test]
    public async Task FirstInstall_ThenUpgrade_SwapsFilesAndDropsRemovedOnes()
    {
        var first = await InstallAsync(V1, new() { ["QuickNotes.exe"] = "exe-v1", ["old.dll"] = "old" });
        Assert.That(first.Success, Is.True, first.Error);

        var upgrade = await InstallAsync(V2, new() { ["QuickNotes.exe"] = "exe-v2", ["lib/new.dll"] = "new" });

        Assert.That(upgrade.Success, Is.True, upgrade.Error);
        Assert.That(PayloadTree(), Is.EquivalentTo(new Dictionary<string, string>
        {
            ["QuickNotes.exe"] = "exe-v2",
            ["lib/new.dll"] = "new",
        }));
        var manifest = await ReadManifest();
        Assert.That(manifest.Version, Is.EqualTo(V2));
        Assert.That(manifest.Files.Select(f => f.RelativePath), Is.EquivalentTo(new[] { "QuickNotes.exe", "lib/new.dll" }));
        Assert.That(_fs.DirectoryExists(Path.Combine(_root, ".instella")), Is.False, "no transaction folder is left behind");
    }

    [Test]
    public async Task UpgradeFailingInRegisterStage_LeavesPreviousVersionFullyWorking()
    {
        await InstallAsync(V1, new() { ["QuickNotes.exe"] = "exe-v1", ["lib.dll"] = "lib-v1" });
        var shortcutsBefore = _platform.Shortcuts.Count;

        var failing = StepBuilder.Create("user-register-step")
            .Execute((ctx, p, ct) => Task.FromResult(StepResult.Fail("user step exploded")))
            .NoRollbackNeeded("never succeeds")
            .Build();
        var upgrade = await InstallAsync(V2, new() { ["QuickNotes.exe"] = "exe-v2", ["new.dll"] = "n" }, userSteps: [failing]);

        Assert.That(upgrade.Success, Is.False);
        // Files and manifest are v1 again.
        Assert.That(PayloadTree(), Is.EquivalentTo(new Dictionary<string, string>
        {
            ["QuickNotes.exe"] = "exe-v1",
            ["lib.dll"] = "lib-v1",
        }));
        Assert.That((await ReadManifest()).Version, Is.EqualTo(V1));
        // Shortcuts and the ARP entry are restored, never removed.
        Assert.That(_platform.ShortcutsRemoved, Is.Empty);
        Assert.That(_platform.Shortcuts.Count, Is.GreaterThan(shortcutsBefore), "the upgrade re-created the v1 shortcuts on rollback");
        Assert.That(_platform.Shortcuts[^1].TargetPath, Does.EndWith("QuickNotes.exe"));
        Assert.That(_platform.UninstallEntriesRemoved, Is.Empty);
        Assert.That(_platform.UninstallEntries[^1].DisplayVersion, Is.EqualTo("1.0.0"));
        Assert.That(_platform.PathEntriesRemoved, Is.Empty);
    }

    [Test]
    public async Task UpgradeFailingInRegisterStage_RestoresOverwrittenRegistryValues()
    {
        var writes = new[] { new RegistryWriteSpec(RegistryHive.CurrentUser, @"Software\QuickNotes", "Theme", InstellaRegistryValueKind.String, _ => "v1-theme") };
        await InstallAsync(V1, new() { ["QuickNotes.exe"] = "v1" }, registry: writes);
        Assert.That(_platform.Registry.Get(RegistryHive.CurrentUser, @"Software\QuickNotes", "Theme"), Is.EqualTo("v1-theme"));

        var writesV2 = new[] { new RegistryWriteSpec(RegistryHive.CurrentUser, @"Software\QuickNotes", "Theme", InstellaRegistryValueKind.String, _ => "v2-theme") };
        var failing = StepBuilder.Create("boom").InStage(InstallStage.Register).After("write-registry-specs")
            .Execute((ctx, p, ct) => Task.FromResult(StepResult.Fail("boom"))).NoRollbackNeeded("fails").Build();
        await InstallAsync(V2, new() { ["QuickNotes.exe"] = "v2" }, registry: writesV2, userSteps: [failing]);

        Assert.That(_platform.Registry.Get(RegistryHive.CurrentUser, @"Software\QuickNotes", "Theme"), Is.EqualTo("v1-theme"),
            "a value the previous version had is restored, not deleted");
    }

    [Test]
    public async Task Repair_KeepsTrustedKeysAnUpdateRotatedIn_ButANewerInstallerInstallsItsOwn()
    {
        var keyA = NewKey();
        var keyB = NewKey();
        await InstallAsync(V1, new() { ["QuickNotes.exe"] = "exe" }, publisherKeys: [keyA]);
        Assert.That((await ReadManifest()).TrustedKeys, Is.EqualTo(new[] { keyA }));

        // An update signed by A rotated the installation to [B] (A revoked).
        var rotated = (await ReadManifest()) with { TrustedKeys = [keyB] };
        await new InstallManifestWriter(_fs).WriteAsync(_root, rotated, CancellationToken.None);

        var repair = await InstallAsync(V1, new() { ["QuickNotes.exe"] = "exe" }, publisherKeys: [keyA]);
        Assert.That(repair.Success, Is.True, repair.Error);
        Assert.That((await ReadManifest()).TrustedKeys, Is.EqualTo(new[] { keyB }), "an older installer must not reinstate a revoked key");

        var upgrade = await InstallAsync(V2, new() { ["QuickNotes.exe"] = "exe2" }, publisherKeys: [keyB, keyA]);
        Assert.That(upgrade.Success, Is.True, upgrade.Error);
        Assert.That((await ReadManifest()).TrustedKeys, Is.EqualTo(new[] { keyB, keyA }), "a newer installer carries the publisher's current keys");
    }

    [Test]
    public async Task TheDownloadToken_IsWrittenToTheInstalledManifest()
    {
        var token = "idt_" + new string('B', 43);

        var result = await InstallAsync(V1, new() { ["QuickNotes.exe"] = "exe" }, downloadToken: token);

        Assert.That(result.Success, Is.True, result.Error);
        Assert.That((await ReadManifest()).DownloadToken, Is.EqualTo(token));
    }

    // ---- an upgrade keeps and removes integrations correctly ----

    private InstallOptions Options(bool desktop = true, bool startMenu = true, bool path = true, bool autoStart = false, bool associations = false) => new()
    {
        InstallPath = _root,
        CreateDesktopShortcut = desktop,
        CreateStartMenuShortcut = startMenu,
        AddToPath = path,
        ConfigureAutoStart = autoStart,
        RegisterFileAssociations = associations,
        Elevation = ElevationMode.PerUser,
    };

    [Test]
    public async Task UpgradeWithoutTheDesktopShortcut_RemovesIt_AndUninstallLeavesNoTrace()
    {
        var before = _platform.Snapshot();
        Assert.That((await InstallAsync(V1, new() { ["QuickNotes.exe"] = "exe-v1" }, options: Options(autoStart: true))).Success, Is.True);
        Assert.That(_platform.Snapshot().Shortcuts, Has.Some.StartsWith("Desktop:"));

        var upgrade = await InstallAsync(V2, new() { ["QuickNotes.exe"] = "exe-v2" }, options: Options(desktop: false, path: false, autoStart: false));

        Assert.That(upgrade.Success, Is.True, upgrade.Error);
        var after = _platform.Snapshot();
        Assert.That(after.Shortcuts, Has.None.StartsWith("Desktop:"), "the unticked shortcut is gone");
        Assert.That(after.Shortcuts, Has.Some.StartsWith("StartMenu:"));
        Assert.That(after.PathEntries, Is.Empty, "the dropped PATH entry is gone");
        Assert.That(after.AutoStarts, Is.Empty, "the dropped auto-start is gone");

        var exit = await new UninstallModeRunner(Config(V2), new NullLog(), _platform, _fs)
            .RunAsync(new DispatchResult(DispatchKind.Mode, InstallerMode.Uninstall, _root, IsSilent: true), CancellationToken.None);
        Assert.That(exit, Is.EqualTo(InstellaExitCode.Success));
        Assert.That(_platform.Snapshot().SameAs(before), Is.True, $"left behind: {_platform.Snapshot()}");
    }

    [Test]
    public async Task AVersionThatDropsAnAssociation_UnregistersIt()
    {
        var qnote = new FileAssociation(".qnote", "QuickNotes note", null);
        var qtext = new FileAssociation(".qtext", "QuickNotes text", null);
        Assert.That((await InstallAsync(V1, new() { ["QuickNotes.exe"] = "exe-v1" }, options: Options(associations: true), associations: [qnote, qtext])).Success, Is.True);
        Assert.That(_platform.Snapshot().FileAssociations, Has.Some.Contains(".qnote"));

        var upgrade = await InstallAsync(V2, new() { ["QuickNotes.exe"] = "exe-v2" }, options: Options(associations: true), associations: [qtext]);

        Assert.That(upgrade.Success, Is.True, upgrade.Error);
        Assert.That(_platform.Snapshot().FileAssociations, Has.None.Contains(".qnote"));
        Assert.That(_platform.Snapshot().FileAssociations, Has.Some.Contains(".qtext"));
        Assert.That((await ReadManifest()).FileAssociations, Is.EqualTo(new[] { ".qtext" }));
    }

    [Test]
    public async Task FailedUpgrade_AfterTheRemoval_RestoresTheShortcut_PointingAtTheOldExe()
    {
        Assert.That((await InstallAsync(V1, new() { ["QuickNotes.exe"] = "exe-v1" }, options: Options())).Success, Is.True);
        var failing = StepBuilder.Create("boom").InStage(InstallStage.Register).After("remove-dropped-integrations")
            .Execute((_, _, _) => Task.FromResult(StepResult.Fail("boom"))).NoRollbackNeeded("never succeeds").Build();

        var upgrade = await InstallAsync(V2, new() { ["QuickNotes.exe"] = "exe-v2" }, userSteps: [failing], options: Options(desktop: false));

        Assert.That(upgrade.Success, Is.False);
        Assert.That(_platform.Snapshot().Shortcuts, Has.Some.StartsWith("Desktop:"), "re-created by rollback");
        var recreated = _platform.Shortcuts.Last(s => s.Location == ShortcutLocation.Desktop);
        Assert.That(recreated.TargetPath, Is.EqualTo(Path.Combine(_root, "QuickNotes.exe")));
        Assert.That((await ReadManifest()).Version, Is.EqualTo(V1));
    }

    [Test]
    public void SilentUpgrade_KeepsARemovedShortcutRemoved()
    {
        var config = ((InstellaInstallerImpl)InstellaInstaller.Create().WithApp("QuickNotes", AppId, V2)
            .WithShortcuts(s => s.Desktop().StartMenu()).Build()).ConfigForTests;
        var existing = new InstalledManifest
        {
            AppName = "QuickNotes", AppId = AppId, Version = V1, InstallDirectory = _root, ExecutableName = "QuickNotes.exe",
            InstalledAt = DateTime.UtcNow, HasDesktopShortcut = false, HasStartMenuShortcut = true, Files = [],
        };

        var options = InstallModeResolver.FollowExisting(Options(), config, existing);

        Assert.That(options.CreateDesktopShortcut, Is.False);
        Assert.That(options.CreateStartMenuShortcut, Is.True);
        Assert.That(InstallModeResolver.FollowExisting(Options(), config, null), Is.EqualTo(Options()), "a first install is unchanged");
    }

    private static PublisherKey NewKey()
    {
        using var key = ReleaseKeys.Generate();
        return ReleaseKeys.PublicKeyOf(key);
    }

    [Test]
    public async Task RerunningTheSameInstaller_IsARepair_ThatRestoresADeletedDll()
    {
        await InstallAsync(V1, new() { ["QuickNotes.exe"] = "exe", ["lib.dll"] = "lib" });
        await _fs.DeleteFileAsync(Path.Combine(_root, "lib.dll"), CancellationToken.None);

        var (mode, existing) = await InstallModeResolver.ResolveAsync(Config(V1), _fs, _root, default, new NullLog(), CancellationToken.None);
        Assert.That(mode, Is.EqualTo(InstallerMode.Repair));

        var repair = await InstallAsync(V1, new() { ["QuickNotes.exe"] = "exe", ["lib.dll"] = "lib" });

        Assert.That(repair.Success, Is.True, repair.Error);
        Assert.That(PayloadTree()["lib.dll"], Is.EqualTo("lib"));
        Assert.That(existing!.Version, Is.EqualTo(V1));
    }

    [Test]
    public async Task UserFinalizeStep_SeesCommittedFiles()
    {
        string? seen = null;
        var probe = StepBuilder.Create("probe").InStage(InstallStage.Finalize)
            .Execute(async (ctx, p, ct) =>
            {
                var read = await ctx.FileSystem.ReadAllBytesAsync(Path.Combine(ctx.InstallPath, "QuickNotes.exe"), ct);
                seen = read.Success ? Encoding.UTF8.GetString(read.Value!) : null;
                return StepResult.Ok;
            })
            .NoRollbackNeeded("read-only").Build();

        var result = await InstallAsync(V1, new() { ["QuickNotes.exe"] = "exe-v1" }, userSteps: [probe]);

        Assert.That(result.Success, Is.True, result.Error);
        Assert.That(seen, Is.EqualTo("exe-v1"));
    }

    [Test]
    public void PointOfNoReturn_OutsideFinalize_IsABuildError_AndDefaultsToFinalize()
    {
        var defaulted = StepBuilder.Create("ponr").Execute((c, p, t) => Task.FromResult(StepResult.Ok))
            .NoRollbackNeeded("x").PointOfNoReturn("irreversible").Build();
        Assert.That(defaulted.Stage, Is.EqualTo(InstallStage.Finalize));

        Assert.Throws<InvalidOperationException>(() => StepBuilder.Create("ponr").InStage(InstallStage.Register)
            .Execute((c, p, t) => Task.FromResult(StepResult.Ok)).NoRollbackNeeded("x").PointOfNoReturn("irreversible").Build());
    }

    [Test]
    public void PointOfNoReturn_IsOrderedAfterCommit()
    {
        var ponr = StepBuilder.Create("ponr").Execute((c, p, t) => Task.FromResult(StepResult.Ok))
            .NoRollbackNeeded("x").PointOfNoReturn("irreversible").Before("write-manifest").Build();

        Assert.Throws<InvalidOperationException>(
            () => StepOrdering.BuildOrderedSteps(OfflineInstallRunner.BuildDefaultSteps(), [ponr]),
            "a point of no return can never run before the commit, even when asked to");

        var plain = StepBuilder.Create("plain").InStage(InstallStage.Finalize)
            .Execute((c, p, t) => Task.FromResult(StepResult.Ok)).NoRollbackNeeded("x").Build();
        var names = StepOrdering.BuildOrderedSteps(OfflineInstallRunner.BuildDefaultSteps(), [plain]).Select(s => s.Name).ToList();
        Assert.That(names.IndexOf("plain"), Is.GreaterThan(names.IndexOf(CommitTransactionStep.StepName)));
    }

    // ---- Mode resolution table ----

    [Test]
    public async Task ModeResolution_Table()
    {
        // Empty / missing directory: first install.
        Assert.That((await Resolve(V1)).Mode, Is.EqualTo(InstallerMode.FirstInstall));

        await InstallAsync(V1, new() { ["QuickNotes.exe"] = "x" });
        Assert.That((await Resolve(V2)).Mode, Is.EqualTo(InstallerMode.Upgrade));
        Assert.That((await Resolve(V1)).Mode, Is.EqualTo(InstallerMode.Repair));

        var older = new Version(0, 9);
        var refused = Assert.ThrowsAsync<InstallRefusedException>(() => Resolve(older));
        Assert.That(refused!.ExitCode, Is.EqualTo(InstellaExitCode.UsageInvalidArgs));
        Assert.That((await Resolve(older, new DispatchResult(DispatchKind.Mode, InstallerMode.FirstInstall, _root, AllowDowngrade: true))).Mode,
            Is.EqualTo(InstallerMode.Upgrade));

        var foreign = Assert.ThrowsAsync<InstallRefusedException>(() => InstallModeResolver.ResolveAsync(
            Config(V2, appId: "com.other.app"), _fs, _root, default, new NullLog(), CancellationToken.None));
        Assert.That(foreign!.ExitCode, Is.EqualTo(InstellaExitCode.InstallGeneralFailure));
        Assert.That(foreign.Message, Does.Contain("different application"));
    }

    [Test]
    public async Task Precheck_MatchesResolve_AndStepsAsideForAnInterruptedTransaction()
    {
        Assert.That(await Precheck(V1), Is.Null, "nothing installed");
        await InstallAsync(V2, new() { ["QuickNotes.exe"] = "x" });

        Assert.That(await Precheck(V2), Is.Null, "repair");
        var older = await Precheck(V1);
        Assert.That(older?.ExitCode, Is.EqualTo(InstellaExitCode.UsageInvalidArgs));
        Assert.That(older!.Message, Does.Contain("2.0.0").And.Contain("1.0.0").And.Contain("--allow-downgrade"));
        Assert.That(await Precheck(V1, new DispatchResult(DispatchKind.Mode, InstallerMode.FirstInstall, _root, AllowDowngrade: true)), Is.Null);

        // Recovery of an interrupted update can roll the version back: leave that to ResolveAsync.
        _fs.AddFile(Path.Combine(_root, ".instella", "txn", "abc", "journal.json"), [1]);
        Assert.That(await Precheck(V1), Is.Null);
    }

    private Task<InstallRefusedException?> Precheck(Version installerVersion, DispatchResult? dispatch = null) =>
        InstallModeResolver.PrecheckAsync(Config(installerVersion), _fs, _root,
            dispatch ?? new DispatchResult(DispatchKind.Mode, InstallerMode.FirstInstall, _root), CancellationToken.None);

    [Test]
    public async Task ModeResolution_NonEmptyUnmanagedDirectory_IsRefusedUnlessForced()
    {
        _fs.AddFile(Path.Combine(_root, "unrelated.txt"), [1]);

        var refused = Assert.ThrowsAsync<InstallRefusedException>(() => Resolve(V1));
        Assert.That(refused!.ExitCode, Is.EqualTo(InstellaExitCode.InstallGeneralFailure));
        Assert.That(refused.Message, Does.Contain("--force"));

        var forced = await Resolve(V1, new DispatchResult(DispatchKind.Mode, InstallerMode.FirstInstall, _root, Force: true));
        Assert.That(forced.Mode, Is.EqualTo(InstallerMode.FirstInstall));
    }

    // ---- helpers ----

    private Task<(InstallerMode Mode, InstalledManifest? Existing)> Resolve(Version version, DispatchResult dispatch = default) =>
        InstallModeResolver.ResolveAsync(Config(version), _fs, _root, dispatch, new NullLog(), CancellationToken.None);

    private static FrozenConfig Config(Version version, string appId = AppId)
    {
        var b = new InstallerBuilder();
        b.WithApp("QuickNotes", appId, version);
        return ((InstellaInstallerImpl)b.Build()).ConfigForTests;
    }

    /// <summary>Resolves the mode like the runners do, then runs the full built-in pipeline.</summary>
    private async Task<ExecutionResult> InstallAsync(
        Version version, Dictionary<string, string> payload,
        IReadOnlyList<StepSpec>? userSteps = null, IReadOnlyList<RegistryWriteSpec>? registry = null,
        IReadOnlyList<PublisherKey>? publisherKeys = null, InstallOptions? options = null,
        IReadOnlyList<FileAssociation>? associations = null, string? downloadToken = null)
    {
        var (mode, existing) = await InstallModeResolver.ResolveAsync(Config(version), _fs, _root, default, new NullLog(), CancellationToken.None);
        var manifest = new InstellaManifest
        {
            AppName = "QuickNotes",
            AppId = AppId,
            Version = version,
            ServerUrl = "https://updates.example.test",
            ExecutableName = "QuickNotes.exe",
            FileAssociations = associations ?? [],
            PublisherKeys = publisherKeys,
            DownloadToken = downloadToken,
        };
        var context = new InstallContext
        {
            AppName = "QuickNotes",
            AppId = AppId,
            AppVersion = version,
            InstallPath = _root,
            Mode = mode,
            Scope = InstallationScope.PerUser,
            Manifest = manifest,
            Options = options ?? new InstallOptions
            {
                InstallPath = _root,
                CreateDesktopShortcut = true,
                CreateStartMenuShortcut = true,
                AddToPath = true,
                ConfigureAutoStart = false,
                RegisterFileAssociations = false,
                Elevation = ElevationMode.PerUser,
            },
            Platform = _platform,
            FileSystem = _fs,
            Log = new NullLog(),
            PayloadArchive = Zip(payload),
            ExistingInstallation = existing,
        };

        var builtIns = new List<IInstallStepExecution>(OfflineInstallRunner.BuildDefaultSteps());
        if (registry is { Count: > 0 }) builtIns.Add(new WriteRegistrySpecsStep(registry));
        var steps = StepOrdering.BuildOrderedSteps(builtIns, userSteps ?? []);
        return await new StepExecutor(steps).ExecuteAsync(context, progress: null, CancellationToken.None);
    }

    private Dictionary<string, string> PayloadTree()
    {
        var prefix = Path.GetFullPath(_root) + Path.DirectorySeparatorChar;
        return _fs.Snapshot()
            .Where(kv => kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(kv => (Rel: kv.Key[prefix.Length..].Replace(Path.DirectorySeparatorChar, '/'), kv.Value))
            .Where(x => !InstellaOwnedPaths.IsOwned(x.Rel))
            .ToDictionary(x => x.Rel, x => Encoding.UTF8.GetString(x.Value), StringComparer.OrdinalIgnoreCase);
    }

    private async Task<InstalledManifest> ReadManifest() =>
        (await new InstallManifestWriter(_fs).ReadAsync(_root, CancellationToken.None))!;

    private static MemoryStream Zip(Dictionary<string, string> files)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, text) in files)
            {
                using var s = zip.CreateEntry(path).Open();
                s.Write(Encoding.UTF8.GetBytes(text));
            }
        }
        ms.Position = 0;
        return ms;
    }

    private sealed class NullLog : IInstellaLogger
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
