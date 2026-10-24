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
| `RunAs(ServiceAccount)` | `ServiceAccount.LocalSystem` | The account the service runs as (see below) |
| `StartAfterInstall(bool start = true)` | true for Automatic and AutomaticDelayed, false for Manual and Disabled | Whether the install starts it |
| `StartTimeout(TimeSpan)` | 60 s | How long a start may take to reach Running |
| `StopTimeout(TimeSpan)` | 30 s | How long a stop may take |
| `StartFailureIsWarning()` | off | A failed start is a warning instead of a failed install |

A `Disabled` service is never started.

### Accounts

| `ServiceAccount` | Account written to the service manager |
|---|---|
| `LocalSystem` (default) | LocalSystem |
| `LocalService` | `NT AUTHORITY\LocalService` |
| `NetworkService` | `NT AUTHORITY\NetworkService` |
| `VirtualAccount` | `NT SERVICE\{name}`, with the unrestricted service SID |

No password is ever asked for or stored. A virtual account is usually the best choice: it has its
own identity for file and registry permissions, and no more rights than you grant it.

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

Argument errors (a null delegate or account, a timeout of zero or less, a negative restart delay)
throw at the call.

## Steps and ordering

When the installer declares services, or the installation it replaces recorded some, four built-in
steps join the [install pipeline](../llm.md):

| Step | Stage | What it does |
|---|---|---|
| `register-services` | Register, after the other Register steps | Creates each declared service that does not exist; reconfigures one this installation already owns, after taking a snapshot of its configuration. Services are handled in dependency order |
| `remove-dropped-services` | Register, right after `register-services` | On an upgrade or repair, stops and deletes the services the previous version declared and this one does not |
| `stop-services` | Finalize, right before `commit-transaction` | Stops the installation's running services, dependents first, each within its `StopTimeout` |
| `start-services` | Finalize, right after `app-upgrade` | Starts every service that starts after install, plus every service `stop-services` stopped (unless it is now Disabled), in dependency order, each within its `StartTimeout` |

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

## Exit codes

| Code | Name | When |
|---|---|---|
| 16 | `InstallServiceFailed` | A service failed to start (an error, a crash while starting, or no Running within `StartTimeout`), and it has no `StartFailureIsWarning()`. The install rolled back. 16 wins over rollback warnings, as 15 does for a failed upgrade program |
| 10 | `InstallGeneralFailure` | A refusal before anything changed (see Security), a service executable missing from the payload, or a service that could not be stopped |

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
  the installing user's own account write, append, delete children, or change permissions or owner,
  the install is refused with exit 10. Anyone who could replace the service's exe could run code as
  the service. Program Files passes; a folder under the user's profile or `%TEMP%` does not.
- **No takeover of foreign services.** A service of the same name that this installation's manifest
  does not record (another app's, or one created by hand) is never reconfigured, stopped or deleted:
  the install is refused with exit 10 and the service is left as it was.
- **LocalSystem is the default**, as with most services; prefer `RunAs(ServiceAccount.VirtualAccount)`,
  `LocalService` or `NetworkService` when the service does not need full machine rights.
- Executables are paths inside the install folder, quoted when written to the service manager, so a
  path with spaces cannot be read as another program.

## Limits

- Uninstall and in-app updates do not stop, reconfigure or delete services yet; they only leave the
  services' own processes out of the running-program check.
- Custom accounts (a user name and password) are not supported.
