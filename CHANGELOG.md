# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.1.0] - 2026-09-27

The first public release. Windows (x64, arm64, x86) is supported; Linux and macOS are
experimental (silent installs only). Instella is tested but not yet field-tested: until 1.0 there
is no compatibility promise. Formats, the updater command line, the wire protocol and the public API
may change between 0.x releases, and this changelog says when a change needs installations to be
reinstalled ([docs/compatibility.md](docs/compatibility.md)).

### Added since 0.1.0-rc.1

- **Install migrations** ([docs/migrations.md](docs/migrations.md)): classes derived from
  `InstallMigration`, registered with `InstallerBuilder.AddMigration<T>()` or `AddMigration(instance)`,
  that run under a condition during an install, upgrade, repair or uninstall. Conditions (mode,
  previous version range, files and folders, Run values, registry values, running programs, scope,
  custom) combine with `&`, `|` and `!`. Safe actions stop programs in a folder, repoint, delete or
  adopt Run values, delete named files and empty folders, and run programs; they refuse the install
  folder, protected folders and other Instella installations. `AfterCommit` migrations (the default)
  are best effort; `BeforeCommit` ones fail and roll back the install. A run-once migration is
  recorded and never runs again for the installation; one an in-app update skipped runs on the next
  installer run.
- `InstallerBuilder.WithAppManagedAutoStart(name)`: uninstall removes the app's own Run value while
  it points into the installation.
- `MigrationHarness` in `Instella.Installer.Testing` runs one migration on in-memory fakes, and
  `InstellaTestHarness` gains `WithPayload`, `KnownFolderPath`, `StartProcess`, `IsProcessRunning` and
  `RunFullWithArgsAsync` for full install/uninstall runs.
- Access-denied simulation in the test fakes: `InMemoryFileSystem.DenyWrites/DenyReads/AllowAll` and
  `FakePlatformServices.DenyRegistryWrites/DenyRegistryReads` report denials as the real file system
  and registry do; `MigrationHarness` exposes them as `DenyWrites`, `DenyReads`, `DenyRunKeyWrites`
  and `DenyRunKeyReads`; `InMemoryFileSystem.AddLink` simulates a symbolic link or junction.
- `IFileSystem.GetEntryState` (tells "absent" from "access denied"), `IsLink` and
  `EnumerateFilesWithoutLinks`, and `IPlatformServices.TryReadRegistryValueAsync` with
  `RegistryReadResult`, all with default implementations, so existing implementations keep compiling.
- Crash-safe migration undo: `BeforeCommit` changes are journaled (`undo.json`) before they are made,
  and the next installer run on the folder finishes an interrupted install's undo, or deletes the
  copies if the install was committed.

### Changed since 0.1.0-rc.1

- The installed manifest (`.instella-manifest.json`) gains two optional fields, `completedMigrations`
  and `adoptedItems`. `manifestVersion` stays 4; no reinstall is needed.
- Server images are published only by release tags: every release publishes `:<version>` (and a
  release without a suffix `:latest`) together with the NuGet packages. The automatic `:edge` and
  `:sha-<commit>` images from pushes to `master` are gone; `:edge` is now a one-off build started by
  hand (`server-image.yml`).

### Features

**Installer**
- A fluent builder (`InstellaInstaller.Create()`) describes the app, pages, steps, shortcuts, file
  associations, PATH entry, auto-start, registry values and prerequisites. `dotnet publish` produces
  one self-contained installer exe that carries the app, or an online (lite) installer that
  downloads it.
- A Win32 wizard with a licence page, options, progress with readable step names and selectable
  error details, `WithLaunchAfterInstall`, `WithBrandImage` and custom pages. Pages can be limited to
  install modes (`InModes`), and the wizard shows the pages for the mode it will run.
- Silent installs (`--silent`), with `AddCliFlag` / `MapCliFlag` to answer pages from the command
  line; a page a silent run cannot answer exits 14 naming the page and the flag.
- Per-user and machine-wide installs (UAC relaunch, Program Files, HKLM registration, all-users
  shortcuts), `--scope user|machine`, and a scope page for `ElevationMode.UserChoice`.
- Install over an existing installation: upgrade, repair, refused downgrade (`--allow-downgrade`),
  foreign-app detection. A re-install targets the existing installation's folder and scope, and an
  upgrade keeps the previous version's shortcuts, PATH entry, auto-start and file associations and
  removes the ones the new version dropped.
- Transactional installs and updates: files are staged, verified and flushed, then committed by
  same-volume renames under a journal; an interrupted commit is finished or rolled back by the next
  Instella process (`instella.exe --recover`, `InstellaClient.StartRecoveryAsync`). One Instella
  process at a time works on an installation (exit 52 when it is busy).
- Uninstall through the stub (`instella.exe --uninstall`), guarded by a tombstone: it refuses volume
  roots, protected folders and folders it did not install.
- `WithNewerVersionPrompt()` offers a newer published release before installing, and
  `WithVersionSelection()` adds `--list-versions`, `--app-version` and `--choose-version`. A signed
  installer hands over only to an installer with the same Authenticode signer.
- The Restart Manager finds processes that hold files and offers to close them.
- Authenticode signing in the build (`InstellaSignCommand`) signs the stub and the installer.
- `Instella.Installer.Testing`: a harness that runs installers against an in-memory file system,
  registry and fake platform services, never the host.

**Updates**
- Releases are signed with ECDSA P-256 by the publisher. Clients accept an update only if its
  signature verifies against a trusted key, it names the installed app, OS, architecture and
  channel, it is newer than the installed version, and every file matches its signed hash. Key
  rotation through a signed `trustedKeys` list.
- Binary patches (BSDiff) between consecutive versions, with a fall-back to full files; every
  download is capped at its signed size.
- Channels are free names (`stable`, `beta`, ...); versions are canonical (`1.3` is `1.3.0`).
- An update window with a restart countdown (`UpdateOptions.RestartCountdown`); the app is
  relaunched without admin rights, also after a machine-wide update.

**App SDK** (`Instella.Sdk`)
- `InstellaClient`: check for updates, download and apply them, `PostUpdate` / `PostUpdateArguments`
  after an update, installation health and recovery, and `DownloadTokenOverride`.

**Server** (`Instella.Server`, Docker)
- Packages, versions, channels and builds with deduplicated content, patch generation in the
  background, drafts, installers (`upload --installer/--offline-installer`) and a public download
  page per package.
- "Latest" is the highest published version on a channel, and an admin can cap it to hold a rollout.
- Access per package: open, API key, or download tokens (`idt_...`) compiled into installers with
  `WithDownloadToken`. A private package answers requests without valid credentials like an unknown
  one (404).
- API keys with separate permissions (upload, manage versions, ...), TOTP two-factor sign-in, a
  first-run setup token, IP bans, rate limits, security event logging with retention, and
  protected secrets at rest.
- Local or S3-compatible storage with an orphan sweeper that only deletes Instella's own objects.
- `/healthz`, EF Core migrations applied at startup, and an image that runs as uid 1654.

**CLI** (`instella-cli`)
- `init` scaffolds an installer project; `keys generate|show` creates publisher keys (owner-only
  files); `upload` signs and uploads a release (with `--sign-command` for a key service and
  `--draft`); `publish`, `list`, `delete`; `ci init` writes a GitHub or Gitea Actions release
  workflow that takes the version and channel from the tag.

**Tooling**
- `scripts/verify.ps1` runs nine stages (Build, Test, Migrations, Audit, Pack, Signing, Aot, E2E,
  Docker); `scripts/release.ps1` checks, gates and packs a release.
- Public API tracking (`PublicAPI.Shipped.txt`), XML documentation for every public member, symbol
  packages and deterministic builds.

[0.1.0]: https://github.com/DJGosnell/Instella/releases/tag/v0.1.0
