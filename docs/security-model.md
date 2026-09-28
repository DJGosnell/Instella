# Security Model

This document describes what Instella protects, how, and where the protection stops. It
covers the supported platform, Windows. Linux and macOS are experimental (no elevation,
no code signing integration) and are not covered by these statements.

Related documents:

- [signing-and-keys.md](signing-and-keys.md): generating, storing, rotating and backing up
  publisher keys.
- [distribution-and-signing.md](distribution-and-signing.md): Authenticode signing of the
  installer and stub (`InstellaSignCommand`).
- [footer-format.md](footer-format.md): the payload footer and its hash.
- [compatibility.md](compatibility.md): format versions and reader rules.
- [server-deployment.md](server-deployment.md): running the server behind TLS.

## Summary

- The **installer** a user downloads is trusted through its Authenticode signature, which Windows
  and the user check. Instella adds a SHA-256 in the payload footer that detects corruption; it is
  not a signature.
- The installer carries the publisher's **public keys**. They are copied into the installation,
  and from then on every update must be a **release manifest signed by one of those keys**. The
  signed manifest lists every file's SHA-256, and every file written during an update is checked
  against it.
- The **server is not trusted for integrity**. It is trusted only for availability. A compromised
  or impersonated server can refuse service, withhold updates, and see request metadata. It cannot
  make an installation accept code the publisher did not sign.
- Every file replacement is a **transaction** with an on-disk journal, so a crash or power loss
  leaves either the old version or the new one.

## Assets and actors

| Actor | Assumed capability |
|---|---|
| Publisher | Holds the publisher private key(s) and the Authenticode certificate. Trusted. |
| Update server operator, or anyone who compromises the server or its storage | Full control over what the server returns. Not trusted for integrity. |
| CI pipeline and its upload API key | Builds and uploads releases, and in the Automatic and Approved tiers signs them with the online publisher key. Trusted to sign; a compromised CI or repository account is what release approval (`Delayed`, `Required`) guards against. |
| Approver (an admin UI user, or an API key with the Approve releases permission) | Publishes or rejects releases the server holds back. Cannot make installations accept anything that is not signed by a trusted key. An approve key cannot also upload, and never approves its own upload. |
| Network attacker | Can observe and modify traffic that is not protected by TLS. |
| Other local users | No write access to another user's profile or to Program Files. |
| Processes running as the installing user | Can modify anything that user can modify, including a per-user installation. Instella does not defend against these (see Known limitations). |

## The three manifests

| Document | Produced by | Contents relevant to trust |
|---|---|---|
| Build manifest (`instella.json`, `InstellaManifest`) | `dotnet publish`, from the fluent builder | `serverUrl`, `publisherKeys`, `allowUnsignedUpdates`, `allowInsecureServer`. Embedded in the installer. |
| Installed manifest (`.instella-manifest.json`, `InstalledManifest`) | The installer, in the install directory | `trustedKeys` (copied from the build manifest), `serverUrl`, `appId`, `platform`, `architecture`, `version`, the installed file list with hashes, and `installedPerUser` (scope). |
| Release manifest (`ReleaseManifest`) | `instella upload`, signed with the publisher private key | `appId`, `version`, `os`, `arch`, `channel`, every file's path, size and SHA-256, optionally a new `trustedKeys` list, and optionally the published installers (kind, file name, size, SHA-256), which an older installer checks before handing off to one. |

## Trust chain

### 1. The installer file

The installer is the user's own compiled installer program with the app payload appended.

- **Authenticode.** When the project sets `InstellaSignCommand`, the build signs a copy of the
  program that becomes the installed stub (`instella.exe`) and then signs the finished installer.
  Windows, SmartScreen and the user check these signatures. Instella does not check the
  signature of the installer that is running (an unsigned installer runs), but it does check the
  installers it hands over to (see [Installer handoff](#installer-handoff)). See
  [distribution-and-signing.md](distribution-and-signing.md).
- **Footer SHA-256.** The installer's 80-byte footer holds a SHA-256 over everything before it,
  with the two PE fields that `signtool` rewrites (`CheckSum` and the security directory entry)
  read as zeros, so signing does not invalidate it. The runtime recomputes it before any UI or
  network access in install, upgrade and repair modes. A mismatch, a malformed footer, or a footer
  from a newer Instella exits **12**. This detects corruption and truncation. It does not prove
  origin: anyone who can rewrite the file can recompute the hash.
- **No self-healing.** A file whose footer exists but fails verification is never "repaired" from
  the server. Only a file with no footer at all (a lite installer) downloads its payload.
- **Lite installers.** A lite installer fetches the signed release for exactly its own version
  from the server, verifies it against the publisher keys compiled into the installer, downloads
  the full build, and checks that the archive contains exactly the release's files with matching
  sizes and SHA-256 before anything is extracted. A failure exits 12.

The publisher keys that anchor everything later come from the installer's own code, so the
integrity of the whole chain rests on the installer being genuine. Authenticode is what tells the
user that.

### 2. Publisher keys

`WithPublisherKey(base64)` compiles an ECDSA P-256 public key into the installer. It may be called
more than once (for a backup key). `Build()` refuses a configuration that has `WithServer(...)`
but no publisher key, unless `AllowUnsignedUpdates()` is called. A missing key is therefore a build
error, not a silent loss of verification.

At install time the keys are written into the installed manifest's `trustedKeys`. Updates are
verified against that list only. Nothing on a command line can add a key: the updater's arguments
do not include a server URL, a package id or keys, and the updater reads all three from the
installed manifest.

### 3. Update acceptance rules

The updater (`instella.exe --update`, started by the SDK or by the Manage window) fetches the
target's signed release from the server itself and accepts it only if all of these hold:

1. **Signature.** The release's key id names a key in the installation's `trustedKeys`, the
   public key matches that id, and the ECDSA P-256 / SHA-256 signature (IEEE P1363) verifies over
   `"INSTELLA-RELEASE-V1\n"` followed by the exact manifest bytes received. The bytes are verified
   before they are parsed.
2. **Identity.** `appId` equals the installation's app id, and `os`/`arch` equal the
   installation's platform and the architecture it was installed as.
3. **Version.** The release version is strictly greater than the installed version (this blocks
   replaying an older signed release to force a downgrade) and equals the version the update was
   offered for.
4. **Files.** Every file placed on disk hashes to the SHA-256 the signed manifest lists, whether it
   was downloaded in full or produced by applying a patch. Files are hashed while they are written
   into a staging folder; a mismatch fails the update before anything live is touched.

The verifier also rejects: a `formatVersion` other than 1; a file path that is not in canonical
safe form (see Path containment); a file path that names an Instella-owned file (`instella.exe`,
`.instella-manifest.json`, `.instella/…`); duplicate paths (compared case-insensitively); a
malformed hash or negative size; and a rotation list that is empty or whose key ids do not match
their keys.

Files the new version no longer contains are computed locally as "installed files minus release
files". The server's own list of deleted files is not used.

**Repair** uses the same engine with the installed version as the target: the release must be
signed by a trusted key and must equal the installed version, and every file whose on-disk hash
differs from the release is replaced.

**Key rotation.** A signed release may carry a `trustedKeys` list. After that release is
installed, the installation trusts exactly that list. The new list is accepted only because the
release carrying it verified against a key the installation already trusted. `instella upload
--trusted-key` sets the list; see [signing-and-keys.md](signing-and-keys.md).

### 4. Where verification happens

| Flow | What is verified | When |
|---|---|---|
| Offline installer | Footer SHA-256; Authenticode by Windows / the user | Before any UI or network access |
| Lite installer | Release pinned to the installer's version, against the compiled-in keys; each file's size and SHA-256 in the downloaded archive | Before extraction |
| SDK `CheckForUpdateAsync` | The release embedded in the `check-update` response, against the installed keys; strictly newer; equal to the offered version | Before reporting `UpdateAvailable`, so the updater is not launched for a release it would refuse. A failure is reported as `Failed`. |
| Updater (`--update`, `--repair`) | Fetches the release itself and verifies it again. This check is authoritative. | Before staging |
| Patch update | Patch manifest and patch archive are unsigned recipes. Each patch blob must hash to its name, and each patched output must hash to the signed release value; otherwise that file is downloaded in full. | During staging |
| Prerequisites | SHA-256 declared in the builder | After download, before execution |
| Installer handoff (`WithNewerVersionPrompt`, `WithVersionSelection`) | The release for the chosen version and channel, against the running installer's keys; the downloaded installer's size and SHA-256 from that release; when the running installer is Authenticode-signed, `WinVerifyTrust` and the same leaf signer | After download, before the downloaded installer starts |

#### Installer handoff

An installer built with `WithNewerVersionPrompt()` or `WithVersionSelection()` can download another
version's installer from the server and start it. The downloaded file is checked against the signed
release (size, SHA-256, and the channel the version was listed from), then re-hashed through a
handle that denies writers, and that handle stays open until the started installer exits, so the
file cannot be swapped between the check and the start. When the running installer is
Authenticode-signed, the downloaded one must also pass `WinVerifyTrust` (revocation from the local
cache only) with the same leaf signer subject; otherwise the handoff exits 12 and the file is
deleted. This matters because the handed-over installer compiles in its own publisher keys: without
the signer check, a leaked, rotated-out publisher key could still sign a release whose installer
replaces the keys (see [signing-and-keys.md](signing-and-keys.md#revocation)).

## What the server can and cannot do

The server stores the signed release bytes verbatim and returns them unmodified. Clients never
rely on the server for integrity.

**The server cannot:**

- make an installation accept files that are not listed in a release signed by a trusted key;
- change a file's contents without the change being detected by its hash;
- replay an older signed release to downgrade an installation;
- deliver another application's release, or a release for another OS or architecture;
- add or change trusted keys, or change the server URL an installation uses;
- make the installer or updater write outside the install directory (see Path containment).

**The server can:**

- refuse service, or withhold updates indefinitely (a "freeze" attack): installations keep their
  current version;
- choose which newer signed release to offer on the requested channel. Any newer release signed by
  a trusted key for the same app, OS, architecture and channel is accepted;
- serve arbitrary unsigned metadata that is shown or used but not trusted: the changelog text, the
  `mandatory` flag, sizes, and patch availability;
- see request metadata: the client's IP address, the package id, the installed version, OS,
  architecture and channel sent with `check-update`, which files and builds are downloaded, and the
  User-Agent (`Instella-SDK/1.0`, `Instella-Updater/1.0`; the product token, not the release version);
- accept unsigned uploads for a package that has no registered publisher keys. Installations
  still refuse such releases unless the installer was built with `AllowUnsignedUpdates()`.

**Server-side publisher keys** (admin UI, package properties) are defence in depth. When at least
one key is registered for a package, the server rejects an upload whose release is unsigned or
does not verify against a registered key. At upload completion the server also checks that the
release names the session's app, version, platform and channel and lists exactly the uploaded
`(path, sha256, size)` set. None of this is part of the client trust chain.

**Release approval** (admin UI, package properties: `Automatic`, `Delayed`, `Required`) can only
hold a correctly signed release back until someone approves it or a delay ends; it never changes
what clients verify. That is deliberate: no server setting reduces client-side checks, because a
compromised server could flip it. Whether an installation verifies releases at all is compiled into
its installer (`WithPublisherKey` versus `AllowUnsignedUpdates()`). Release approval guards against
a compromised CI or repository account, not a compromised server: the server holds the pending
release and can publish it, as it could serve any release CI signed. Each decision (pending,
approved, rejected, published after a delay, blocked because the key was removed, setting changed,
publisher key added or removed) is written to the security log with the admin or API key that made
it.

### Private packages don't reveal that they exist

For a package that is not open, a request with no credentials, or with an invalid API key or download token,
gets exactly what a request for an unknown package gets: 404 `Package not found` for the package routes, 404 for
downloads, patches, releases and installers, and `{"updateAvailable": false}` from check-update. Only a valid
credential that lacks permission gets 403. Denials are still written to the Security log (a repeat of the same
event from the same address for the same package within a minute is counted, not written again).

The trade-off: an installed app whose download token was revoked, expired or never compiled in silently sees "no
update", and a publisher who mistypes a package id gets the same 404 as one without access. The failures show up
in the admin Security log, which is where to look first.

Every public route is rate limited per client address: downloads (`Downloads:PermitsPerMinute`, 600), the package
API (`Api:PermitsPerMinute`, 300) and sign-in (`Auth:PermitsPerMinute`, 20, on top of the per-user and per-address
login backoff). Updaters retry a 429 up to three times, honouring `Retry-After`, so many installs behind one NAT
address keep working at the default limits.

### Download tokens

A private package (one that is not open) needs a credential for every download route. Installations
carry a **download token** for that: the admin issues one per package (`idt_` followed by 43 random
characters; the server stores only its SHA-256), the installer compiles it in with
`WithDownloadToken(token)`, and the installed manifest keeps it (`downloadToken`). The SDK, updater,
lite installer, installer handoff and Manage window send it as `Authorization: Bearer` to the
package's own server only: the same scheme, host and port as the installed `serverUrl`. A redirect to
another host (a presigned S3 URL) never carries it. `InstellaClient.DownloadTokenOverride` replaces the
token at run time, for example after the app fetched a new one from its own backend.

What a token protects: the package's releases, builds, patches and installers from people who never
received the installer, and the package's existence from anonymous probing. A token only reads; it
cannot upload, publish, delete or see other packages.

What it does not protect: the token is inside every installer and every installation of the package, so
anyone who has the installer, or an installed copy, can extract it and download what that installation
could. It is a distribution control, not a per-user licence. When a token leaks, the admin revokes it
(effective within a minute, the server's cache lifetime) or lets it expire, and ships installers with a
new one. Installations built with the revoked token then silently see "no update" (see above) until
they are reinstalled or the app sets `DownloadTokenOverride`.

## Transport (HTTPS policy)

`ServerUrlPolicy` defines the rule for a server URL:

- It must be an absolute URI.
- `https` is always accepted.
- `http` is accepted only when the host is loopback (`localhost`, `127.0.0.0/8`, `::1`), so local
  development works, or when the author opted in.
- Any other scheme is rejected.

Where it is enforced:

- `InstallerBuilder.Build()` and `InstellaManifest.Validate()`: a non-loopback `http` server URL
  fails the build unless `AllowInsecureServer()` is called. With the opt-in, every installer run
  logs a warning.
- The CLI's `upload`, `list` and `delete`: refused unless `--allow-insecure` is passed, and warned
  about when it is.
- The SDK's update check, the updater and the manage window: before any request they check the
  installed manifest's `serverUrl` again, with its `allowInsecureServer` flag (copied from the
  build manifest at install). A per-user installed manifest is user-writable, so this stops an
  edited URL from moving updates onto plain http. A refused URL fails the check (`Failed`) or
  the update (exit 20).

HTTPS protects the privacy of update traffic and stops tampering before the signature check runs.
It is not what makes updates trustworthy; the signature is. .NET's `HttpClient` does not follow a
redirect from `https` to `http`.

A download token (see [Download tokens](#download-tokens)) and an API key are sent in the clear over
`http`. With `AllowInsecureServer()` or `--allow-insecure` on a network you do not control, anyone on
the path can copy them. Use `http` only on loopback or a trusted development network. Prerequisite download URLs are not subject to `ServerUrlPolicy`;
they are protected by their declared SHA-256.

## Prerequisites

A prerequisite with `WithDownloadUrl(...)` must also declare `WithSha256(...)`; `Build()` refuses
otherwise. The installer downloads into a fresh `%TEMP%\Instella\prereq-{random}\` folder, then
opens the file with a handle that denies writers, hashes it through that handle, and runs it only
if the hash matches, keeping the handle open while it runs. A bundled prerequisite
(`WithBundlePath`) comes from the payload and is covered by the footer hash. Installers are started
with `ProcessStartInfo.ArgumentList` and `UseShellExecute = false`. A hash mismatch fails the
install (exit 10) without executing anything.

## DLL search policy

The installer usually runs from the Downloads folder, and several libraries it loads (`gdiplus`,
`uxtheme`, `dwmapi`, `shcore`, `comctl32`) are not KnownDLLs. Two layers stop a DLL planted next to
the installer from being loaded:

- The Runtime assembly carries `[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]`,
  which applies to every `DllImport`/`LibraryImport` in it (honoured by NativeAOT).
- The first statement of `InstellaInstallerImpl.RunAsync` on Windows calls
  `SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32)`, before any UI or COM work, so
  libraries loaded indirectly by the OS, COM or the runtime are also restricted to System32.

Libraries loaded before `RunAsync` starts (the executable's static imports, and anything the .NET
host loads during startup) are outside this policy. The SDK does not change the DLL search policy:
inside the user's app that policy belongs to the app.

## Elevation boundary

*Scope* is where an installation lives. `PerUser` means `%LOCALAPPDATA%\Programs`, HKCU and the
user PATH. `SystemWide` means `%ProgramFiles%\{Publisher}\{App}`, HKLM, the machine PATH and
all-users shortcuts. The installed manifest records the scope (`installedPerUser`).

**What runs elevated.** Only a process that needs machine scope. The installer's application
manifest is `asInvoker`, so it always starts unelevated; the explicit level also turns off
Windows' installer-detection auto-elevation of executables named like `setup.exe`.

| `ElevationMode` | Interactive | Silent |
|---|---|---|
| `PerUser` | Per-user, never prompts | Per-user |
| `SystemWide` | Machine; relaunches elevated if needed | Machine; exits **51** if not elevated |
| `UserChoice` | A built-in scope page is shown first; "all users" relaunches elevated | `--scope user\|machine` (default `user`); `machine` without elevation exits 51 |

**How the relaunch works.** The unelevated process starts its own executable with the `runas`
verb (the UAC prompt), waits, and returns the child's exit code. It forwards its original command
line, with any earlier `--scope`/`--elevated-child` removed, plus `--scope machine` and
`--elevated-child`, quoted with `WindowsCommandLine.Join` (the documented `CommandLineToArgvW`
rules). `--elevated-child` stops a relaunch loop: an elevated child that still lacks rights exits
51. If the user declines UAC, an interactive `UserChoice` run returns to the scope page with a
notice; other runs exit 51.

**No page answers cross the boundary.** The scope page is the first thing shown, and the elevated
child shows every other page itself. The only data that crosses is the command line the user (or
their script) supplied.

**The elevated child's payload.** The child re-reads its payload from its own `.exe`, which may sit
in a user-writable folder. Windows denies write access to an image file while it is mapped by a
running process, so the file cannot be replaced under the child, and the footer hash is verified
again.

**Operations on a machine installation.** Uninstall, Update and Recover read the installed
manifest first. When it records a machine install and the process is not elevated, they relaunch
elevated with the same arguments. A silent Uninstall exits 51 instead of prompting. Update and
Recover prompt even when silent: the app starts them, so a user is there (there is no prompt-free
machine updater yet). The Manage window opens unelevated and relaunches elevated only for
Uninstall, Update and Repair. Uninstall from Windows Settings starts the stub from Program Files,
which self-elevates.

**Target binding.** The stub only ever elevates for the installation it belongs to, the folder it
sits in. `--update` must name that folder (`--app-path`), or it exits 40 before reading anything.
`--uninstall`, `--recover` and `--manage` accept another folder with `--path` and act on it
unelevated, but never show UAC for it: a machine-wide foreign target exits 51 ("run the uninstaller
in that folder, or run this one as administrator"), and a foreign target in an `--elevated-child`
is refused (51), because the stub never starts such a child. Without this, a medium-integrity
process could get the publisher's signed stub to run elevated against a folder it controls. A
per-user install belongs to the user who could already modify it, and a machine install's stub
sits in an admin-only folder, so the only elevated path left is the stub's own relaunch for its
own folder. An administrator who starts the stub elevated on purpose gains nothing.

**The app is never restarted elevated.** After an update (and after "Launch … when I click
Finish"), an elevated installer starts the app through `%WINDIR%\explorer.exe`, which runs as the
signed-in user, so the app never inherits the admin token. No arguments are passed on that path;
`UpdateOptions.AdditionalArgs` travel in the post-update marker instead. Explorer chooses the
working directory (normally the exe's folder).

The updater takes the server URL, package id and trusted keys from the installed manifest. For a
machine install that file is in Program Files and writable only by administrators, so an
unprivileged user cannot redirect an elevated update.

## App upgrade programs

An app can ship an [upgrade program](app-upgrade.md): its own exe, named by `instella-upgrade.json` at
the app's root, which the installer and the updater run on every version change and, when it opts in,
before an uninstall.

- **Trust.** The program and the declaration are ordinary release files: they are in the signed release
  manifest, hashed while staging, and trusted exactly as much as the app's other files. The updater
  already trusts those files enough to install them; running one is no wider trust. No release-manifest
  or trust-rule change was needed.
- **Rights.** The program runs with the installer's or updater's rights and nothing more: elevated for a
  machine-wide installation (the updater relaunches elevated for one), the user's rights for a per-user
  installation. It never runs elevated for a per-user install.
- **Which file.** The declaration names a path relative to the install folder, checked with `SafePath`,
  never under `.instella/` and never the stub. It must be one of the app's files for the version being
  installed (payload, signed release or installed manifest); a declaration naming anything else fails the
  operation. On install and update the program runs from the new, just-verified files, after the commit.
- **Uninstall.** Before running the program (elevated, for a machine install), uninstall hashes it and
  compares it with the installed manifest's record; a program changed since it was installed is not run.
- **Containment.** No shell, arguments as a list, stdin closed, the install folder as working directory.
  A time limit (default 30 minutes, at most a day) ends the whole process tree. On Windows the program
  runs in a job object that ends it if the installer or updater dies, so it cannot keep changing data
  after recovery has rolled the files back. Its output goes to the log with size caps (4,096 characters
  per line, 1 MiB per run).
- **Rollback.** A failure rolls the files back. The commit stays journalled as `Committing` while the
  program runs, so a crash is rolled back by the next run's recovery. The app's data is the program's
  responsibility: it must leave data the old version can use when it fails.

## Path containment

Every relative path that comes from outside the process goes through `SafePath` before it becomes
a file-system path: payload archive entries, release manifest paths (a patch is used only for a
path the signed release lists), server-side upload paths, and uninstall tombstone entries. `SafePath` rejects absolute paths,
drive and UNC roots, `.`/`..` and empty segments, NUL, `:` (NTFS alternate data streams), Windows
reserved device names, and segments that end in `.` or a space. The rules apply on every platform,
because a package built on Linux must still install on Windows. The combined path is also checked
to stay inside its root.

Instella-owned paths (`instella.exe`, `.instella-manifest.json`, `.instella/`) cannot be written
by payload entries or release files; the transaction stages them only through a separate internal
call.

## Crash safety: transactions and the journal

Installs, upgrades, repairs and updates all replace files through one `InstallTransaction`:

1. **Stage.** New files are written under `.instella/txn/{id}/stage/`, hashed as they are written.
   Nothing live is touched.
2. **Verify.** Every staged file is re-hashed.
3. **Commit.** Each live file is renamed into `backup/` and the staged file renamed into place.
   Everything is under the install root, so every step is a same-volume rename. The installed
   manifest is always the last file committed, so it names the new version only once everything
   else is in place.

The journal (`.instella/txn/{id}/journal.json`) is written at each state transition by writing a
temporary file, flushing it to disk, and renaming it over the journal. Which files of an
operation have moved is read back from which of the live, stage and backup copies exist, so
rollback and recovery can themselves be interrupted and re-run.

Every stub entry point that targets an existing installation (update, uninstall, manage, install
over an existing directory, and `--recover`) first recovers: an interrupted commit is rolled back;
any other leftover transaction folder is deleted. A journal with an unknown `journalVersion` is
never interpreted: the installation is left untouched and the process exits 23. The app itself can
detect an interrupted update with `InstellaClient.GetInstallationHealth()` and start
`instella --recover` with `StartRecoveryAsync()`.

For an install, the transaction commits at the start of the Finalize stage. User steps marked
`PointOfNoReturn` always run after the commit, so when rollback stops at a point of no return the
installed manifest already describes what is on disk.

## Uninstall cleanup

A running uninstaller cannot delete its own `.exe`. Instead of a `cmd.exe` trampoline, uninstall
writes `.instella/uninstall.tombstone` listing exactly the files it could not delete, plus a random
nonce, then starts a temporary copy of itself (`%TEMP%\Instella\cleanup\{nonce}.exe`) with
`--cleanup --path {root} --token {nonce} --parent-pid {pid}`. Cleanup refuses to delete anything
unless the path is not a volume root, is not a protected folder (the user profile, Desktop,
Documents, Program Files, Windows, System, AppData, LocalAppData, ProgramData, the Start-menu
program folders, Temp) or an ancestor of one, a tombstone exists there, its nonce equals the token
(constant-time comparison), and its recorded root equals the path. It then deletes only the listed
files, the `.instella/` folder and directories left empty. Files the user added stay. Temporary
copies older than 24 hours are removed on each cleanup run.

## Development escape hatches

| Switch | Effect | Guard |
|---|---|---|
| `AllowUnsignedUpdates()` | No publisher key needed; releases are not signature-checked. For an update the file list comes from the server's full build archive; files are still hashed while staging, which catches corruption only. Anyone who controls the server or the network path can then push code. | Recorded in the installed manifest; warned about on every installer run. |
| `AllowInsecureServer()` | Permits a non-loopback `http` server URL. | Recorded in the installed manifest; warned about on every installer run. |
| `instella upload --unsigned` | Uploads without a signature. | Refused by the server when the package has registered keys; refused by installations that were not built with `AllowUnsignedUpdates()`. |
| `instella ... --allow-insecure` | CLI talks to a non-loopback `http` server. | Warned about on each use. |

Do not ship production installers with either builder switch.

## Known limitations

- **Freeze and selection.** The server decides whether and which update is offered. Instella
  cannot detect a withheld update.
- **Unsigned metadata.** Changelog text, the `mandatory` flag and sizes in `check-update` are not
  signed.
- **Release approval is enforced by the server.** A compromised server can publish a pending release
  (it is signed, so installations accept it). An approve key and an upload key both held by CI defeat
  `Required`; the server cannot detect that, so keep approve keys on people's machines. There are no
  notifications for pending releases yet: check the admin dashboard or `instella pending`.
- **Audit retention.** Security log entries, including release decisions, are deleted after
  `Retention:SecurityEventDays` (default 90). A rejected release is deleted, so the log is its only
  record.
- **The installer is the root of trust.** Instella does not verify the Authenticode signature of
  the installer that is running (only of installers it hands over to). A first
  install, or an installer for a newer version, writes that installer's compiled-in keys to
  `trustedKeys`, replacing whatever list the installation had. (A repair or downgrade keeps the
  recorded list.) A genuine, signed installer is therefore required for every install.
- **Per-user installations are user-writable.** Any process running as the user can modify the
  app's files and the installed manifest, including `trustedKeys` and `serverUrl`. Instella's
  guarantees for per-user installs are against the network and the server, not against code
  already running as that user. Machine installs in Program Files are writable only by
  administrators.
- **Patch archive location.** The patch archive is spooled to a delete-on-close file in `%TEMP%`,
  not under `.instella/txn/{id}/` as the design describes. Its SHA-256 is compared with the value
  `check-update` reported (passed to the updater as `--patch-sha256`); a mismatch only disables
  patching. Security does not depend on this check, because every patched output is hashed against
  the signed release.
- **Closing the app.** The updater waits for the app process that launched it (`--parent-pid`), then
  asks Windows' Restart Manager which processes have files of the installation open or loaded (plus
  the main executable by name under the install path), and asks them to close. Explorer, services and
  critical system processes are never closed; a process it may not close keeps its files in use, and
  the update exits 24 when files stay locked. If a commit rename still fails, the update rolls back
  (exit 22).
- **App upgrade programs outside Windows.** Linux and macOS (experimental) have no job object: an
  upgrade program can outlive an installer that is killed while it runs.
- **Prerequisite elevation.** A prerequisite with `RequiresElevation` (the default) that runs
  from a per-user install without administrator rights raises a UAC prompt of its own. A silent
  install never prompts, so it fails with exit 11 instead; run it from an elevated prompt.
- **Machine updates prompt.** Every update of a machine-wide installation shows a UAC prompt, even
  when silent.
- **Stub refresh.** Auto-updates never replace the installed stub. Custom steps, `OnUninstall`
  hooks and uninstall logic change only when the user runs a newer installer.
- **Unsigned stub without a sign command.** Without `InstellaSignCommand` the installed
  `instella.exe` is unsigned, even if the finished installer is signed by hand afterwards.
- **DLL policy starts at `RunAsync`.** Libraries loaded before the installer's entry point are
  outside the System32-only policy.
- **Experimental platforms.** On Linux and macOS there is no elevation, no code signing
  integration, and no graphical installer; none of the Windows-specific statements above apply.
