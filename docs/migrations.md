# Install Migrations

An installer often has to deal with what came before it: a copy of the app installed without
Instella, settings an older version kept somewhere else, a file an older version left behind. An
**install migration** is a small class in your installer project that does one such job, under a
condition you declare, during an install, upgrade, repair or uninstall.

This page explains migrations, then walks through the most common case: [replacing an existing
installation](#replacing-an-existing-installation).

## A migration

```csharp
using Instella.Installer.Runtime.Migrations;

public sealed class MoveOldSettings : InstallMigration
{
    public override string Id => "move-settings-v2";
    public override string DisplayName => "Moving your settings";

    protected override Condition When() =>
        FileExists(KnownFolder.RoamingAppData, "ExampleApp/settings.ini")
        & !FileExists(KnownFolder.RoamingAppData, "ExampleApp/settings.json");

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Custom code: convert the file (see "Custom code" below).
        var folder = Context.GetFolderPath(KnownFolder.RoamingAppData)!;
        ...
        Log.Info("converted settings.ini to settings.json");
    }
}
```

Register it on the builder, one line per migration:

```csharp
InstellaInstaller.Create()
    .WithApp("ExampleApp", "com.example.app", version)
    .AddMigration<MoveOldSettings>()
    .AddMigration(new RemoveOldPlugin("legacy-plugin"))   // an instance, when it needs arguments
    .Build();
```

`AddMigration<T>()` creates the class once, with `new T()`. There is no assembly scanning, so
trimming and Native AOT keep working.

| Member | Meaning |
|---|---|
| `Id` | Permanent identifier: 1–64 characters of lowercase letters, digits, `.`, `_`, `-`. It is recorded in the installation once the migration succeeds; **never rename or reuse a shipped id**. |
| `DisplayName` | Shown on the wizard's Progress page while it runs. Defaults to `Id`. |
| `Timing` | `AfterCommit` (default), `BeforeCommit` or `Uninstall`: see [Timing and failures](#timing-and-failures). |
| `Order` | Order among migrations of the same timing (ascending, then by `Id`). Defaults to 0. |
| `RunOnce` | Record a success so the migration never runs again for the installation. Defaults to true; must be false for `Uninstall`. |
| `When()` | The condition. It only composes conditions: it is also called by `Build()`, where `Context` is not available. |
| `ExecuteAsync` | The work. Throwing fails the migration. |
| `RollbackAsync` | `BeforeCommit` only: undo your custom code when the install fails. |
| `Context` | Facts about the run: `Mode`, `PreviousVersion`, `Scope`, `InstallPath`, `IsPreview`, `GetFolderPath(KnownFolder)`, and `FileSystem`/`PlatformServices` for custom code. |
| `Log` | The install log; every line is prefixed `migration[<id>]:`. |

`Build()` refuses a migration with an invalid or duplicate id, an empty display name, an uninstall
migration marked `RunOnce`, a `When()` that reads `Context`, or a condition that can never be true
for its timing (for example `IsUninstall()` on an `AfterCommit` migration).

## Conditions

Conditions are protected methods of `InstallMigration`. Combine them with `&` (and), `|` (or) and
`!` (not). When a condition is false, the log says which part was false:
`migration[move-settings-v2]: skipped: FileExists(RoamingAppData/ExampleApp/settings.ini) is false`.

| Condition | True when |
|---|---|
| `IsFirstInstall()`, `IsUpgrade()`, `IsFirstInstallOrUpgrade()`, `IsRepair()`, `IsUninstall()` | The installer runs in that mode. |
| `UpgradingFrom("range")` | An upgrade from a version in the range (see below). |
| `FileExists(KnownFolder, "relative/path")` | The file exists. |
| `FolderExists(KnownFolder, "relative/path")` | The folder exists. |
| `InstellaInstallationAt(KnownFolder, "relative/path")` | The folder holds an Instella installation (`.instella-manifest.json`). |
| `RunValueExists("name")` | The Run value exists (HKCU for a per-user install, HKLM machine-wide). |
| `RunValuePointsInto("name", folder)` | The Run value starts a program inside the folder. |
| `RegistryValueExists(hive, "key", "name")` | The registry value exists. |
| `ProcessRunningIn(folder)` | A program has files in the folder open (Explorer, services and critical processes excepted). |
| `IsWindows()`, `IsPerUserInstall()`, `IsMachineInstall()` | Platform and scope. |
| `Condition.From(ctx => …, "description")` | Your own test. It should only read. A predicate that throws skips the migration with a warning, even under `!`: a check that never completed is never read as true. |
| `Condition.Always` | Always, typically for uninstall migrations. |

**Version ranges.** Comparators separated by spaces, all of which must hold: `<`, `<=`, `>`, `>=`,
`=` followed by a version, or a bare version (exactly that version). `*` matches any version.
Versions compare canonically: `2`, `2.0` and `2.0.0` are equal. Examples: `"<2"`, `">=1.0 <1.5"`,
`"1.4.2"`. For "either range", combine two conditions with `|`.

**Known folders.** Every path starts from a `KnownFolder`, resolved for the scope being installed:

| Known folder | Per-user install | Machine-wide install |
|---|---|---|
| `LocalAppData`, `RoamingAppData` | the user's | *unavailable* |
| `StartMenuPrograms` | the user's Start menu | all users' Start menu |
| `ProgramFiles`, `ProgramFilesX86`, `ProgramData` | the machine's | the machine's |
| `InstallFolder` | the folder being installed (conditions only) | same |

A machine-wide install runs elevated, possibly as another account (an administrator approving the
UAC prompt), so its "per-user" folders and HKCU may not be the user's. Conditions on them are false,
with the reason logged, and actions on them fail.

## Actions

Actions are protected methods too. Each one checks the safety rules below before touching anything.

| Action | What it does |
|---|---|
| `Folder(KnownFolder, "relative")` | Names a folder to act on (a `MigrationFolder`). |
| `StopProcessesInAsync(folder)` | Closes the programs that have files in the folder open: asks them to close, then ends them. Interactive installs ask the user first; silent installs close them only with `--force-close`. Fails if one is still running. |
| `RepointRunValueAsync("name", folder)` | If the Run value starts a program in the folder, points it at this installation's executable, keeping its arguments. Returns whether it changed. |
| `DeleteRunValueAsync("name", folder)` | Deletes the Run value if it starts a program in the folder. |
| `AdoptRunValueAsync("name")` | Records the Run value so uninstall removes it (only while it points into the installation). |
| `DeleteFilesAsync(folder, ["a.exe", "sub/b.dll"])` | Deletes exactly the named files. Missing files are skipped; no wildcards. |
| `DeleteFolderIfEmptyAsync(folder)` | Deletes the folder if nothing is left in it. Never recursive. |
| `RunProgramAsync(@"C:\…\uninstall.exe", ["/S"], [0, 3010])` | Runs a program (for example an old uninstaller) without a shell, waits up to 10 minutes, and fails on an exit code outside the list (empty list: 0). |

A refused or failed action throws `MigrationActionException`, which fails the migration.

### Safety rules

- **Paths** are a known folder plus a relative path. `..`, absolute paths, drive letters, device names
  and wildcards are refused when you build the folder or condition.
- **Actions refuse** the folder being installed, anything inside it and any folder that contains it;
  volume roots; the well-known folders (the profile, Desktop, Documents, Program Files, Windows,
  AppData, ProgramData, the Start menu, Temp, each known folder's root, `LocalAppData\Programs`,
  `LocalAppData\Microsoft`, …) and their ancestors; any folder that holds, contains or is inside an Instella installation; and a folder that cannot be listed to check.
- **Deleting** is limited to the files you name and to empty folders.
- **Processes**: only programs holding files in the validated folder, with the same prompt and
  `--force-close` rules as closing the app itself.
- **Registry**: only the scope's Run key, through the Run-value actions.

### When access is denied

Migrations run with the installer's rights: a per-user install cannot change Program Files, and
any install can meet a file or registry key it is not allowed to touch.

- **A denied write, delete or move** (a file, a folder, a Run value) fails the action with the
  reason, for example `DeleteFiles: could not delete '…\ExampleApp.pdb': Access to the path … is
  denied.` An `AfterCommit` migration logs it as a warning and runs again on the next installer run;
  a `BeforeCommit` migration puts back what it had already changed and fails the install.
- **A denied read looks like "not there"**, as in Windows itself: `File.Exists` and registry reads
  cannot tell "denied" from "absent", so `FileExists`, `RunValueExists` and similar conditions are
  false and the migration is skipped (and not recorded). Listing a folder that cannot be read fails
  `DeleteFolderIfEmptyAsync` with the reason.
- **A condition that cannot be evaluated at all** (it throws) skips the migration with a warning;
  it never fails the install.
- **Undo that is denied** (the folder became read-only in the meantime) is logged as a warning; the
  rest of the rollback continues, and the files the migration deleted are kept in
  `%TEMP%\Instella\migration-undo\…` (the log names the folder) instead of being lost.

`MigrationHarness` simulates all of these: `DenyWrites(root, relative)`, `DenyReads(root, relative)`,
`DenyRunKeyWrites()` and `DenyRunKeyReads()`. The fakes behind it, `InMemoryFileSystem.DenyWrites/
DenyReads` and `FakePlatformServices.DenyRegistryWrites/DenyRegistryReads`, report denials the way
the real file system and registry do, for your own tests of custom steps too.

### Custom code

`ExecuteAsync` can run any code, and `Context.FileSystem` and `Context.PlatformServices` reach the
file system and the registry directly. **Custom code bypasses every rule above**: check paths
yourself, and prefer the actions where they fit. Use `Context.FileSystem` rather than `System.IO`
so `MigrationHarness` can test the code.

## Timing and failures

| Timing | Runs | A failure |
|---|---|---|
| `AfterCommit` (default) | At the very end of an install, upgrade or repair, after every other step, including your own Finalize steps. | Is logged as a warning. The install succeeds and is kept; the migration is not recorded, so it runs again on the next installer run. Cancelling the install at this point skips the migration without undoing the install. |
| `BeforeCommit` | Just before the new files are moved into place (after all registration steps). | Fails the install. The migration's built-in actions are undone (deleted files are put back, Run values restored), `RollbackAsync` is called for your custom code, and the install is rolled back. A later failure in the install rolls the migration back the same way. |
| `Uninstall` | During uninstall, before Instella removes anything. | Is logged; the uninstall continues. |

Use `AfterCommit` unless the new version cannot work until the migration has run. Programs you run
(`RunProgramAsync`) cannot be undone, so put them in `AfterCommit` migrations.

The install log ends with a summary line, for example
`migrations: 1 completed (replace-pre-instella-copy), 1 skipped (move-settings-v2)`.

## Run-once, repair and in-app updates

A run-once migration that succeeds is recorded in the installed manifest (`completedMigrations`) and
never runs again for that installation. Upgrades and repairs keep the record. A migration that was
skipped or failed is not recorded, so a later installer run can still apply it.

**In-app updates do not run migrations.** The updater applies the new version's files; it does not
run the installer's steps, and it never replaces the installed stub. A migration shipped in version 3
therefore runs:

- when a user installs or upgrades with the version 3 installer (or later);
- for an installation that auto-updated to version 3, the next time any installer of version 3 or
  later runs on it: a repair from Installed Apps, or a later manual upgrade.

Two consequences:

- **Test state, not versions or modes.** By the time a catch-up run happens, the installed version
  is the auto-updated one and the run may be a repair. `FileExists(...)` and `RunValuePointsInto(...)`
  still describe the situation correctly; `UpgradingFrom("<3")` or `IsFirstInstallOrUpgrade()` may
  not. Keep migrations idempotent.
- **Changes that must happen on update belong in the app.** On the first start after an update,
  `InstellaClient.IsPostUpdate` is true; do update-time fix-ups there.

## Uninstall

Uninstall runs, in order: the uninstall migrations (by `Order`, then `Id`), your steps' `OnUninstall`
hooks, the removal of adopted Run values, then Instella's own cleanup. Uninstall mirrors install in
reverse: migrations run last when installing, so they run first when uninstalling, while every file
and registration is still there.

**`WithAppManagedAutoStart("name")`** is for an app with its own "start with Windows" setting, which
writes a Run value Instella did not create. Uninstall deletes that value, but only while it points
into the installation being removed, so a value pointing at another copy is left alone. The name is
recorded in the installed manifest (`adoptedItems`), as are values adopted with
`AdoptRunValueAsync`; they are honoured even if a later version drops the call or the migration.

## Preview

In preview (`EnablePreview()` + `--preview`), migration steps appear in the step list by display
name. In `MigrationHarness.Preview()`, actions report what they would do (`preview: would
delete-file …`) and change nothing.

## Testing

`MigrationHarness` (in `Instella.Installer.Testing`) runs one migration against in-memory fakes:
every known folder lives in a fake profile, and the registry, running programs and program runs are
fakes. Nothing on the machine is touched.

```csharp
var harness = MigrationHarness.For<ReplacePreInstellaCopy>()
    .WithFile(KnownFolder.LocalAppData, "ExampleApp/ExampleApp.exe")
    .WithRunningProcess(KnownFolder.LocalAppData, "ExampleApp/ExampleApp.exe");
harness.WithRunValue("ExampleApp", $"\"{harness.PathOf(KnownFolder.LocalAppData, "ExampleApp/ExampleApp.exe")}\"");

var result = await harness.RunAsync();

Assert.That(result.Outcome, Is.EqualTo(MigrationOutcome.Completed));
Assert.That(result.FileExists(KnownFolder.LocalAppData, "ExampleApp/ExampleApp.exe"), Is.False);
Assert.That(result.RunValue("ExampleApp"), Does.Contain(result.PathOf(KnownFolder.InstallFolder, "")));
```

Other setup: `Mode`, `PreviousVersion`, `Scope`, `Elevated` (a machine-wide install), `Preview`,
`AlreadyCompleted`, `WithFolder`, `WithInstellaInstallation`, `WithRegistryValue`, `ForceClose(false)`
(a silent install without `--force-close`), `ProgramExitCode`, `DenyWrites`/`DenyReads`/
`DenyRunKeyWrites`/`DenyRunKeyReads` (access denied), `FailLaterStep` (rolls a
`BeforeCommit` migration back). `RunAsync` validates the migration as `Build()` does. The result
reports the outcome, the skip reason or error, whether the id would be recorded, deleted files,
stopped programs, registry changes, adopted items, programs run, planned actions (preview) and the
log.

For a full silent install and uninstall through your real installer, `InstellaTestHarness` has
`WithPayload`, `KnownFolderPath`, `StartProcess` and `RunFullWithArgsAsync`.

## Replacing an existing installation

A common migration: the app was distributed before you adopted Instella, and users have a copy
Instella does not know about. A concrete (anonymized) example:

- A tray app, **ExampleApp**, was distributed as a zip. Users unpacked it to
  `%LOCALAPPDATA%\ExampleApp\`, which holds only `ExampleApp.exe` and `ExampleApp.pdb`.
- Its own "Start with Windows" setting writes the Run value `ExampleApp` under
  `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, pointing at the old exe.
- It has no Installed Apps entry and no shortcut.
- Its user data is in `%APPDATA%\ExampleApp` and must not be touched.
- It usually runs in the tray with no main window, so it cannot simply be asked to close.

**Without a migration**, installing the Instella build (per-user, into
`%LOCALAPPDATA%\Programs\ExampleApp`) leaves two copies on disk. Instella only recognises its own
installations, and it only closes programs running from its own install folder, so the old copy
keeps running. At sign-in the old Run value still starts the old exe, and whichever copy starts
first wins. On uninstall, Instella removes only the auto-start entry it created itself, so the app's
own Run value is left pointing at a deleted exe.

**With a migration:**

```csharp
public sealed class ReplacePreInstellaCopy : InstallMigration
{
    public override string Id => "replace-pre-instella-copy";
    public override string DisplayName => "Removing the previous copy of ExampleApp";

    private MigrationFolder OldCopy => Folder(KnownFolder.LocalAppData, "ExampleApp");

    // Only state: a repair or a later upgrade can catch up (see "Run-once, repair and in-app updates").
    protected override Condition When() =>
        IsPerUserInstall()
        & FileExists(KnownFolder.LocalAppData, "ExampleApp/ExampleApp.exe")
        & !InstellaInstallationAt(KnownFolder.LocalAppData, "ExampleApp");

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await StopProcessesInAsync(OldCopy, ct);                 // close the tray app
        await RepointRunValueAsync("ExampleApp", OldCopy, ct);   // sign-in starts the new copy
        await DeleteFilesAsync(OldCopy, ["ExampleApp.exe", "ExampleApp.pdb"], ct);
        await DeleteFolderIfEmptyAsync(OldCopy, ct);             // anything else stays
    }
}

InstellaInstaller.Create()
    .WithApp("ExampleApp", "com.example.app", version)
    .WithElevation(ElevationMode.PerUser)
    .AddMigration<ReplacePreInstellaCopy>()
    .WithAppManagedAutoStart("ExampleApp")   // uninstall removes the app's own Run value
    .Build();
```

What happens:

1. **Install.** After the new copy is in place, the migration closes the running old copy (asking
   first in the wizard; silently only with `--force-close`), points the `ExampleApp` Run value at the
   new exe with the same arguments, deletes the two old files and the then-empty folder, and is
   recorded. `%APPDATA%\ExampleApp` is untouched, so the new copy finds the user's settings.
2. **If it cannot finish** (for example a silent install without `--force-close` while the old copy
   runs), the install still succeeds, the log explains why, and the migration runs again the next
   time an installer runs: a repair from Installed Apps is enough.
3. **Later installs and repairs** skip it: it is recorded as completed.
4. **Uninstall** deletes the `ExampleApp` Run value, because it points into the installation being
   removed. If the user has since pointed it at another copy, it is left alone.

The pieces to adapt: the folder and file names, the Run value name, and whether the old copy has its
own uninstaller (run it with `RunProgramAsync` instead of deleting files) or a Start menu folder
(`Folder(KnownFolder.StartMenuPrograms, "ExampleApp")` with `DeleteFilesAsync` and
`DeleteFolderIfEmptyAsync`). A shortcut directly in the Start menu's Programs folder or on the
Desktop cannot be removed by the actions, because those folders are protected; use custom code for
it and check the path yourself.

## Limitations

- A repair or downgrade by an installer built before migrations existed (Instella 0.1.0) rewrites
  the installed manifest without the migration record, so completed migrations can run again later.
  Keep them idempotent.
- Folders are compared by path; a folder that is a junction to somewhere else is not detected. The
  actions only delete named files and empty folders, which limits the impact.
