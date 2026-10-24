# Windows Services

An installer can register Windows services that run from the installed files. First install,
upgrade and repair create or reconfigure each service, stop it before the new files go live and
start it again afterwards. When anything fails, the whole operation rolls back, services included.

Services are Windows-only. There is no cross-platform abstraction: `OnWindows` runs its delegate
only on Windows, so on Linux and macOS an installer declares no services.

## Declaring a service

```csharp
InstellaInstaller.Create()
    .WithApp("Contoso Sync", "com.contoso.sync", new Version(2, 1))
    .WithExecutableName("Contoso.Sync.exe")
    .WithElevation(ElevationMode.SystemWide)          // required for services
    .OnWindows(w => w
        .AddService("Contoso.Sync", s => s
            .WithDisplayName("Contoso Sync")
            .WithDescription("Keeps the Contoso folders in sync.")
            .WithArguments("--service")
            .RestartOnFailure(TimeSpan.FromSeconds(30), resetAfter: TimeSpan.FromDays(1))
            .RunAs(ServiceAccount.VirtualAccount))
        .AddService("Contoso.Sync.Broker", s => s
            .WithExecutable("bin/Contoso.Broker.exe")
            .WithStartType(ServiceStartMode.Manual)
            .StartAfterInstall()))
    .Build()
    .RunAsync(args);
```

`AddService(name, configure)` can be called as often as needed. `configure` receives a
`ServiceBuilder`. Each of its methods returns the builder:

| Method | Default | Meaning |
|---|---|---|
| `WithDisplayName(string)` | the service name | The name the Services console shows |
| `WithDescription(string)` | none | The description the Services console shows |
| `WithExecutable(relativePath)` | the app's main executable (`WithExecutableName`) | The service's exe, relative to the install folder; it must be in the payload |
| `WithArguments(params string[])` | none | Arguments appended to the quoted exe path |
| `WithStartType(ServiceStartMode)` | `Automatic` | `Automatic`, `AutomaticDelayed`, `Manual` or `Disabled` |
| `DependsOn(params string[])` | none | Services that must run first (cumulative; others than your own are allowed) |
| `RestartOnFailure(delay, resetAfter)` | no failure actions | Three Restart actions after `delay`; the failure count resets after `resetAfter` |
| `RunAs(ServiceAccount)` | `ServiceAccount.LocalSystem` | The account the service runs as (see [Accounts](#accounts)) |
| `StartAfterInstall(bool start = true)` | true for Automatic and AutomaticDelayed, false for Manual and Disabled | Whether the install starts it |
| `StartTimeout(TimeSpan)` | 60 s | How long a start may take to reach Running |
| `StopTimeout(TimeSpan)` | 30 s | How long a stop may take |
| `StartFailureIsWarning()` | off | A failed start is a warning instead of a failed install |

A `Disabled` service is never started.

### What `Build()` refuses

`Build()` throws `InvalidOperationException` naming the service when:

- the installer declares a service and is not `WithElevation(ElevationMode.SystemWide)` (per-user,
  user-choice and the default all are refused): services are machine-wide;
- a name is not 1 to 80 characters of `A-Z a-z 0-9 . _ -`, or two names differ only in case;
- `DependsOn` makes a cycle among the services this installer declares (a service depending on itself
  included);
- an executable path leaves the install folder, is absolute, or is one of Instella's own files;
- `StartFailureIsWarning()` is set on a service that is never started;
- a custom step is named `register-services`, `remove-dropped-services`, `stop-services` or
  `start-services`, or a point-of-no-return step says `Before("start-services")`.

Argument errors (a null delegate or account, a timeout of zero or less, a negative restart delay,
a gMSA name that is not `DOMAIN\name$`, an empty `WithServicePasswordVariable` name) throw at the call.

## Accounts

| `ServiceAccount` | Account written to the service manager | Password | "Log on as a service" |
|---|---|---|---|
| `LocalSystem` (default) | LocalSystem | none | built in |
| `LocalService` | `NT AUTHORITY\LocalService` | none | built in |
| `NetworkService` | `NT AUTHORITY\NetworkService` | none | built in |
| `VirtualAccount` | `NT SERVICE\{name}`, with the unrestricted service SID | none | built in |
| `ManagedServiceAccount(@"CORP\app-gmsa$")` | the name as given | none: Windows supplies it | granted when missing |
| `FromInstaller(defaultUser = null)` | the account the person installing supplies | asked for at install time | granted when missing |

A virtual account is usually the best choice: it has its own identity for file and registry
permissions, and no more rights than you grant it. Use a group-managed service account (gMSA) when
the service needs a domain identity: `ManagedServiceAccount` takes `DOMAIN\name$` and throws
`ArgumentException` for anything else; no password is asked for or stored.

### `FromInstaller`: an account the person installing supplies

```csharp
.OnWindows(w => w
    .WithServicePasswordVariable("CONTOSO_SYNC_PASSWORD")   // optional; default INSTELLA_SERVICE_PASSWORD
    .AddService("Contoso.Sync", s => s.RunAs(ServiceAccount.FromInstaller(@"CORP\svc-sync")))
    .AddService("Contoso.Sync.Broker", s => s.RunAs(ServiceAccount.FromInstaller())))
```

Every service using `FromInstaller` shares one account. The `defaultUser` of the first such service
(in declaration order) pre-fills the page and is the silent default; the others' defaults are ignored.
A name without `\` and without `@` is a local account and is written as `.\name`; any other name is
used as given. The resolved name is what the service manager and the installed manifest record.

**Wizard.** A built-in `service-account` page, after the Options page and before Progress, asks for
the user name, the password and its confirmation. The password fields are masked (`PasswordInput`)
and nothing typed enters the page's state. When the user presses Next the page checks that both
passwords match and are not empty, then tries one network logon: a wrong user name or password
blocks the page with the reason; any other refusal (the account may not log on over the network, for
example) is logged and let through, since the service logs on differently.

**Silent.** Two reserved flags supply the account:

| Flag | Meaning |
|---|---|
| `--service-account <user>` | The account. Without it: on an upgrade or repair, the account the installation records (as the wizard does); else the `FromInstaller` default user |
| `--service-password-file <path>` | A file holding the password. Read once; one trailing newline (`\n` or `\r\n`) is trimmed; the file is left in place. An unreadable or empty file exits 14 |

Without `--service-password-file`, the password comes from the environment variable
`INSTELLA_SERVICE_PASSWORD`, or the one `WithServicePasswordVariable(name)` names; an empty variable
counts as absent, and it is read only when a password is needed. A given password file is always
read, so an administrator can change the stored password on an upgrade. A silent run with no account
exits 14 naming `--service-account`; one with an account but no password exits 14 naming
`--service-password-file` and the variable. Both are checked before anything changes.

There is no `--service-password` flag on purpose: a password on the command line is visible to every
process on the machine and lands in shell history and process-creation logs. Passing one is an
unknown flag (exit 40). An author flag named `service-account` or `service-password-file` makes
`Build()` throw, as other reserved flags do.

**Upgrade and repair.** When the installation already records a custom account for every
`FromInstaller` service and the account does not change, the page is not shown and no password is
needed: the service is reconfigured without one and the service manager keeps the stored secret.
The page is shown (and silent runs need a password) when a service ran as another kind of account
before, or the account changes.

### "Log on as a service" (`SeServiceLogonRight`)

A gMSA or `FromInstaller` account needs the `SeServiceLogonRight` user right. `register-services`
grants it once per account that lacks it and records the account in the installed manifest
(`grantedLogonRights`). A right the account already had is never recorded, so it is never revoked.
When the install fails, rollback revokes only the rights this run granted. Uninstall revokes every
right the manifest records; an account an administrator set up keeps its right. Built-in and
virtual accounts never touch the local security policy.

### A wrong password

The service manager takes any password; a wrong one shows when the service starts. `start-services`
reports error 1069 in plain words, for example `the service 'Contoso.Sync' could not log on as
'CORP\svc-sync' (error 1069): the account's user name or password is wrong, or it may not log on as a
service`, and the install rolls back with exit 16. When an upgrade changed the account, rollback puts
the previous account back but cannot restore its password (Windows never gives it out): the log says
so, and the previous service may need its password set again by hand.

## Steps and ordering

When the installer declares services, or the installation it replaces recorded some, four built-in
steps join the [install pipeline](../llm.md):

| Step | Stage | What it does |
|---|---|---|
| `register-services` | Register, after the other Register steps | Creates each declared service that does not exist; reconfigures one this installation already owns, after taking a snapshot of its configuration. Services are handled in dependency order |
| `remove-dropped-services` | Register, right after `register-services` | On an upgrade or repair, stops and deletes the services the previous version declared and this one does not |
| `stop-services` | Finalize, right before `commit-transaction` | Stops the installation's running services, dependents first, each within its `StopTimeout`. Running services of other products that depend on them are stopped too, and remembered |
| `start-services` | Finalize, right after `app-upgrade` | Starts every service that starts after install, plus every service `stop-services` stopped (unless it is now Disabled), in dependency order, each within its `StartTimeout`; then starts again the other products' dependents `stop-services` stopped (one that does not start is a warning) |

So on an upgrade the order is: new configuration (staged files) → stop → commit the new files → the
app's [upgrade program](app-upgrade.md) → start. The old version never runs on the new files, and the
upgrade program runs while the services are stopped. A `Manual` service that was running before an
upgrade is running after it.

Custom Finalize steps run after `start-services`, when the services are running. A step that must
see them stopped (with the new files already live) says `Before("start-services")`. A step can still
say `Before("commit-transaction")` or `Before("write-manifest")` to run earlier.

The installed manifest records each service (`services`: name, relative executable, account, start
type), so the next upgrade knows what it owns, what to stop and what was dropped.

## Rollback

Every service step undoes its work when the install fails at any later point:

- a service the install created is stopped (when running) and deleted;
- a service the install reconfigured gets its previous configuration back, then the services
  `stop-services` stopped are started again: the previous version restarts on its own configuration
  and its own files;
- a dropped service is re-created from its snapshot and started when it was running.

When rollback cannot restore, re-create or restart a service, it tries everything else first and the
failure is reported as a rollback warning (exit 13), unless a failed start already decided the exit code.

## Updates and repair

The updater (`--update`, Manage → Check for updates) and its repair (`--repair`, Manage → Repair)
work on the services the installed manifest records, with the timeouts and `StartFailureIsWarning()`
of the stub's declaration of the same name (a recorded service the stub does not declare uses the
defaults: 30 s to stop, 60 s to start, and its start failure is an error):

1. After the app that launched the updater has exited, and before the running-program check, every
   recorded service that is running is stopped, dependents first. A service still stopping when its
   `StopTimeout` ends has its process ended. A service that will not stop (access denied, an error,
   still running) ends the update with exit 26 before anything is downloaded or staged; the services
   already stopped are started again.
2. The files are staged and committed, and the app's upgrade program runs.
3. The services that were running are started again, dependencies first, each waited for up to its
   `StartTimeout`. Services that were stopped before the update stay stopped.
4. A service that does not start rolls the update back (the message says so when the app's upgrade
   program already changed its data), starts the previous version's services and exits 26.
   With `StartFailureIsWarning()` the update completes and logs a warning.

Any other failure after step 1 (a download that fails, cancellation, programs that would not close,
a commit rolled back) starts the stopped services again once the files are back; the exit code is
unchanged. When the rollback itself fails (exit 23) the services stay stopped until `--recover`.

`--restart` (and the update window's restart) starts the services whose executable is the app's main
executable instead of launching it as a desktop program.

## Updating a service

A service can update itself, opt-in, with `InstellaClient.StartServiceUpdateAsync(update)`. The
desktop calls do not work there: `StartUpdaterAsync` and `LaunchUpdaterAndExitAsync` make the updater
wait for the app to exit (the service manager would see a crash) and may show a window or a UAC prompt
in session 0, where nobody can answer. Inside one of the installation's services they throw
`InvalidOperationException` pointing to `StartServiceUpdateAsync`. `InstellaClient.IsRunningAsServiceAsync()`
says whether the current process is one of the installation's recorded services, so code shared
with a desktop build can choose:

```csharp
public sealed class UpdateWorker(ILogger<UpdateWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var check = await InstellaClient.CheckForUpdateAsync(stoppingToken);
            if (check.Update is { } update)
            {
                var start = await InstellaClient.StartServiceUpdateAsync(update, stoppingToken);
                if (start.IsStarted)
                    log.LogInformation("Updating to {Version} (updater pid {Pid})", update.Version, start.ProcessId);
                else
                    log.LogWarning("Cannot update to {Version}: {Reason}", update.Version, start.RefusalReason);
            }
            await Task.Delay(TimeSpan.FromHours(6), stoppingToken);
        }
    }
}
```

`StartServiceUpdateAsync` starts the stub (`instella --update … --service-update --silent`) detached
and returns at once; the service keeps running. The updater stops the recorded services through the
service manager (this one included), updates the files, and starts the services again as described in
[Updates and repair](#updates-and-repair). It never waits for a parent process, never shows a window
or a message box, never asks for elevation (not elevated: exit 51) and never launches the app's
executable; programs that hold the files open are not closed (exit 24, the services are started
again). It never calls `Environment.Exit` and never throws for a refusal: the result's `IsStarted`
is false and `RefusalReason` says why. Only a null `update` (`ArgumentNullException`) and
cancellation before the start throw.

### Which accounts can update

The updater replaces files in the install folder and stops and starts the services, so the service
must run elevated: as **LocalSystem** (the default of `RunAs`) or as an administrator account
(a `FromInstaller` or managed service account only when it is in the Administrators group).
`LocalService`, `NetworkService` and virtual accounts cannot: their update requests are refused
before anything starts. Such a service can still be updated by the installer, or by a desktop
companion app with `StartUpdaterAsync`.

### The checks

Before it starts anything, `StartServiceUpdateAsync` checks, in this order, and the first that fails
is the refusal:

| Check | Refusal reason (start) |
|---|---|
| The app was installed by Instella | `not installed by Instella …` |
| The installed manifest has a `services` field (an installer without service support wrote none, and its stub would not stop the service) | `the installation records no services field …` |
| It records at least one service | `the installation records no Windows service` |
| Windows | `Windows services exist only on Windows` |
| The process is elevated | `the service is not elevated …` |
| It can write the install folder (and `.instella/`) | `the service account cannot write to {folder}: …` |
| It can open every recorded service with stop and start rights | `the service account cannot stop and start service '{name}': …` |
| It is not in a job object that forbids breakaway | `the service runs in a job object that does not allow breakaway …` |
| The stub `instella.exe` is in the install folder | `the Instella stub instella.exe is missing …` |

The checks are a snapshot: something can still change before the updater runs. A later failure is
handled by the updater as any other: the update rolls back and the services are started again.

### Job objects

When a service stops, the service manager ends the processes of its job object, if it runs in one.
The updater must outlive the service, so it is started with `CREATE_BREAKAWAY_FROM_JOB` when the
service runs in a job that allows breakaway (`JOB_OBJECT_LIMIT_BREAKAWAY_OK` or
`JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK`). A job that allows neither is refused, because the updater
would die with the service. A service outside any job needs nothing.

## Uninstall

Uninstall stops every recorded service (dependents first, ending the process of one stuck stopping)
and deletes it right after the running-program check, before the app's uninstall handler, uninstall
migrations, `OnUninstall` hooks, integrations and files. When a service cannot be stopped or deleted
the uninstall exits 33, names the service, starts again the ones it had stopped and removes nothing
else; the installation can be uninstalled later.

A service that already existed when the installation was made (the manifest records it as not
created by Instella) is stopped but never deleted: it stays registered, stopped.

## Marked for deletion

Windows removes a deleted service only once nothing holds it open. While the Services console, Event
Viewer or another tool has it open, the service stays registered and **marked for deletion**:

- Uninstall waits up to 30 s for it to go. If it is still there, the uninstall finishes (exit 0) and
  says a restart may be needed to remove the service completely.
- An install that meets a service of its name marked for deletion stops before it changes anything
  (exit 16) and says to close the Services console and Event Viewer, or restart Windows, then run the
  installer again.

## Recovery

The services an update or an upgrade stopped are written beside the transaction journal
(`.instella/txn/{id}/services-running.json`). Recovery (`--recover`, and the recovery every updater,
uninstall, Manage and install run does first) reads them before it rolls the interrupted transaction
back, then starts each listed service the installation records that exists and is not running. A
service that does not start there is a warning: the files are consistent and `--recover` exits 0.
The journal format is unchanged.

## Exit codes

| Code | Name | When |
|---|---|---|
| 16 | `InstallServiceFailed` | A service failed to start (an error, a crash while starting, or no Running within `StartTimeout`), and it has no `StartFailureIsWarning()`. The install rolled back. 16 wins over rollback warnings, as 15 does for a failed upgrade program |
| 16 | `InstallServiceFailed` | Also: the install met a service of the same name that is **marked for deletion** (see below). Nothing was installed |
| 10 | `InstallGeneralFailure` | A refusal before anything changed (see Security), a service executable missing from the payload, or a service that could not be stopped |
| 26 | `UpdateServiceFailed` | The updater (or Manage → Repair) could not stop a service before the update (nothing was downloaded or changed, the services it had stopped run again), or the new version's service would not start (the update rolled back and the previous version's services were started again) |
| 33 | `UninstallServiceFailed` | Uninstall could not stop or delete a service. Nothing else was removed; the installation is still there to uninstall later |

The log and the silent run's stderr name the service and the reason, for example
`service 'Contoso.Sync' could not be started: ... did not reach Running within 60 s`. With
`StartFailureIsWarning()` the install completes and logs the same text as a warning.

## Running programs

Before an upgrade, repair, uninstall or update, Instella checks for programs using the installation's
files and asks to close them (silent: exit 24 without `--force-close`). The installation's own
services are not on that list: their process ids are ignored, and the service steps stop them
through the service manager instead. Another copy of the same executable (the app started from the
desktop, for example) still counts.

## Publishing: INSTELLA0105

The build manifest lists each service and its executable. When publishing appends the payload,
**INSTELLA0105** fails the build for each service whose executable (or, without `WithExecutable`,
the main executable) is not a payload entry after `WithPayloadFilter`:

```text
error INSTELLA0105: service 'Contoso.Sync.Broker': executable 'bin/Contoso.Broker.exe' is not in the payload
```

The installer checks the same thing again before it changes anything (exit 10).

## Security

A service usually runs with more rights than the user who installs it, so Instella refuses the
setups that would let a non-administrator take it over:

- **SystemWide only.** Services need `WithElevation(ElevationMode.SystemWide)`; a per-user install
  never registers one.
- **The install folder must be one only administrators can change.** Before anything is created, the
  install folder (or, when it does not exist yet, its nearest existing parent, by the entries it would
  inherit) is checked: when its access list lets Users, Authenticated Users, Everyone, INTERACTIVE or
  the installing user's own account write, append, delete, delete children, or change permissions or
  owner (entries that reach only the folder's files count too), or when one of them owns the folder,
  or owns or can delete children of the folder that holds it, the install is refused with exit 10. Anyone who could replace the service's exe could run code as
  the service. Program Files passes; a folder under the user's profile or `%TEMP%` does not.
- **No takeover of foreign services.** A service of the same name that this installation's manifest
  does not record (another app's, or one created by hand) is never reconfigured, stopped or deleted:
  the install is refused with exit 10 and the service is left as it was.
- **LocalSystem is the default**, as with most services; prefer `RunAs(ServiceAccount.VirtualAccount)`,
  `LocalService` or `NetworkService` when the service does not need full machine rights.
- Executables are paths inside the install folder, quoted when written to the service manager, so a
  path with spaces cannot be read as another program.

## Limits

- In-app updates never reconfigure a service (a new account, start type or dependency); run the new
  version's installer for that. They only stop and start the services the installation records.
