# Instella — Fluent-builder installer framework for .NET apps

Instella is a cross-platform packaging and update system. App authors reference a
pair of NuGet packages in their own `*.Installer` project, describe the installer
fluently in C#, and `dotnet publish` produces a single self-contained exe (or
ELF / Mach-O) with the app payload appended and a manifest embedded. No external
stub is downloaded; the user's compile IS the installer.

**Target framework**: .NET 10. Core, Sdk, Runtime and Testing set `IsAotCompatible` + `IsTrimmable`
(trim/AOT analyzer warnings are build errors via `TreatWarningsAsErrors`); every `JsonSerializer`
call uses a source-generated context. The end-to-end `PublishAot=true` check of the sample app and
installer is `verify.ps1 -Stage Aot` (needs the VS C++ build tools).
**Platforms**: Windows x64/arm64/x86 supported; Linux and macOS **experimental** — silent
installs only (a non-silent run prints "interactive UI is not available on this platform; re-run
with --silent to install" and exits 40), a warning banner on every run, and build warning
`INSTELLA0001` for non-`win-*` RIDs (suppress with `<NoWarn>INSTELLA0001</NoWarn>`). The macOS
start-menu shortcut is disabled; Linux file associations register a hidden
`{appId}-{ext}.desktop` handler; helper tools' exit codes are checked (`ExternalCommand`).
Elevation/UAC relaunch is Windows-only (`NoElevationService` elsewhere).
**Compression**: the payload is a plain Deflate ZIP (`System.IO.Compression`);
BSDiff (BSDIFF40 with Brotli-compressed ctrl/diff/extra blocks) for binary-diff
updates. Brotli is used only inside patch blobs.
**Security model**: `docs/security-model.md` (trust chain, server trust, HTTPS, elevation) and
`docs/signing-and-keys.md` (publisher keys). Format versions and API policy: `docs/compatibility.md`.
Server deployment: `docs/server-deployment.md`. Local gate: `docs/verification.md`. Releases:
`scripts/release.ps1 -Tag v0.1.0[-rc.1] [-Push]` (clean tree at the tag, tag = `VersionPrefix`, dated
CHANGELOG heading for a final release, `verify.ps1 -NoSkip -VersionSuffix`, pack to `artifacts/<tag>/`,
server image, push only with `-Push`).

**Versions are canonical**: `AppVersions` (Core, internal) treats `1.3`, `1.3.0` and `1.3.0.0` as one
version (a zero revision is dropped) at every comparison in Core, Runtime, SDK, CLI and the server.

**Public API**: Core, Sdk, Runtime and Testing track their public surface in
`PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt` (Microsoft.CodeAnalysis.PublicApiAnalyzers).
`RS0016`/`RS0017` (undeclared / missing API) and `CS1591` (missing XML doc) are build errors, so any
public-surface change must be recorded in `PublicAPI.Unshipped.txt` and documented. Engine types
(step executor, update engine, downloaders, diff engine, platform service implementations,
`SafePath`, `Checksum`, `InstellaOwnedPaths`, `UpdaterArgs`, `InstallTransaction`, journal types,
JSON contexts, `EmbeddedResources`) are internal.

## Solution structure

```
src/
  Instella.Core/              # shared types: build manifest (Manifest/), installed manifest,
                              #   owned paths, exit/mode enums, journal (Installation/),
                              #   release manifest + signing (Trust/), routes/DTOs/URL policy (Wire/),
                              #   platform services, filesystem + SafePath, BSDiff/BSPatch,
                              #   Checksum, update DTOs + UpdaterArgs (Update/), footer reader (Internal/)
  Instella.Sdk/               # app-facing SDK: InstellaClient (update check, updater launch,
                              #   post-update detection, installation health / recovery)
  Instella.Installer.Runtime/ # fluent builder, mode runners, elevation, step pipeline,
                              #   InstallTransaction (Core/Transactions/), update engine (Core/Update/),
                              #   widget renderers (Win32/GTK/Cocoa)
  Instella.Installer.Build/   # MSBuild task + targets for the two-stage publish
  Instella.Installer.Testing/ # CI harness: InMemoryFileSystem/Registry, FakePlatformServices
  Instella.Server/            # ASP.NET Core distribution + update server (+ Blazor admin UI).
                              #   See src/Instella.Server/llm.md
  Instella.CLI/               # `instella init|keys|upload|publish|list|delete|ci` tool
tests/                        # one *.Tests project per src project, plus Contract.Tests
                              #   (real server + clients) and E2E.Tests ([Explicit], processes)
```

Note on type homes: `UpdateInfo` / `UpdateCheckResult` / `UpdateCheckStatus` live in **Core.Update**
(re-exposed by the SDK); `InstalledManifest` lives in **Core.Installation** (so the SDK reads it
without Runtime); `UpdaterEngine` / `HttpUpdateDownloader` live in **Runtime** (`Core/Update/`).
`InstellaInstallerImpl` + `FrozenConfig` live in Runtime `Builders/`.

## Usage: the fluent builder

The user's `*.Installer` project's `Program.cs` (only `WithApp` is required; `WithServer` then
requires `WithPublisherKey` unless `AllowUnsignedUpdates()`):

```csharp
using Instella.Core.Installation;            // InstallStage, StepResult
using Instella.Core.Manifest;                // ElevationMode
using Instella.Core.Platform;                // RegistryHive
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Runtime.UI.Widgets; // ImageSource

return await InstellaInstaller.Create()                    // -> InstallerBuilder
    .WithApp("QuickNotes", "com.example.quicknotes", new Version(1, 2, 0))
    .WithServer("https://updates.example.com")
    .WithPublisherKey("MFkwEwYHKoZIzj0CAQYI...")           // from `instella keys generate`
    .WithPublisherKey("MFkwEwYHKoZIzj0CAQYI...")           // offline backup key
    .WithPublisher("Example Corp")
    .WithIcon(ImageSource.FromFile("../QuickNotes/Assets/icon.ico"))
    .WithShortcuts(s => s.Desktop().StartMenu())
    .WithFileAssociation(".qnote", "QuickNotes Document")   // icon? must be ImageSource.FromFile
    .WithPrerequisite(p => p
        .WithName("VC++ 2022 Runtime")
        .WithDownloadUrl("https://aka.ms/vs/17/release/vc_redist.x64.exe")
        .WithSha256("<64 hex chars>")                       // required with WithDownloadUrl
        .WithDetectionRegistry(@"HKLM\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\X64"))
    .WithElevation(ElevationMode.UserChoice)
    .AddPage("license", p => p                              // typed widget adders, not factories
        .Heading("Licence")
        .ScrollableText("...")
        .CheckBox("agree", "I accept the licence")
        .ContinueWhen(s => s.Bool("agree")))
    .AddCliFlag<bool>("accept-license")
    .MapCliFlag("accept-license", "license.agree")          // silent installs need --accept-license
    .AddStep("post-install", sb => sb
        .InStage(InstallStage.Finalize)
        .Execute((ctx, progress, ct) => { ctx.Log.Info("hi"); return Task.FromResult(StepResult.Ok); })
        .NoRollbackNeeded("log-only"))
    .OnWindows(w => w.AddRegistryKey(RegistryHive.CurrentUser, @"Software\QuickNotes",
        k => k.SetString("InstalledBy", "Instella")))       // auto-tracked for uninstall reversal
    .Build()                                                // -> IInstellaInstaller; throws on bad config
    .RunAsync(args);                                        // Task<int> RunAsync(string[], CancellationToken=default)
```

### Root builder surface (`InstallerBuilder`, all return `this` except `Build`)

| Method | Purpose |
|---|---|
| `WithApp(name, appId, version)` | Required identity |
| `WithServer(url)` / `WithChannel(ch)` | Update server + channel (default `stable`; any `ChannelNames` name: 1-32 of `[a-z0-9-]`; the server creates a channel on its first upload) |
| `WithPublisherKey(base64Spki)` | Trust updates signed by this ECDSA P-256 key; repeatable (backup key). Required with `WithServer` |
| `AllowUnsignedUpdates()` | Dev escape hatch: no key needed, releases not verified (warned on every run) |
| `AllowInsecureServer()` | Permit plain `http` to a non-loopback host (warned on every run) |
| `WithPublisher/WithHomepage/WithLicense/WithDescription(text)` | Metadata |
| `WithIcon(ImageSource)` / `WithExecutableName(name)` | Branding / main-exe name (resolved at build time; `INSTELLA0102` if ambiguous). An installer exe with no icon (no `ApplicationIcon`, no `.ico` from `WithIcon`) shows the Instella logo as its window icon |
| `WithBrandImage(ImageSource?)` | Image at the top of the Welcome page, scaled to 64 px high at 100 %. Default: the Instella logo; `null` shows none |
| `WithDownloadToken(string token)` | A download token (`idt_` + 43 chars, created in the server's package panel) for a `PackageKeyRequired` package; `ArgumentException` for anything else; `Build()` requires `WithServer`. Carried as `InstellaManifest.DownloadToken` → `InstalledManifest.DownloadToken` (`downloadToken`); updates keep it. Readable by anyone with the installer: it limits access, it is not a secret |
| `WithInstallPath(Func<InstallContext,string>)` | Runtime-resolved path; `ctx.Scope` is already resolved |
| `WithElevation(ElevationMode)` | `PerUser`=0 / `SystemWide`=1 / `UserChoice`=2 (default UserChoice) |
| `WithShortcuts(Action<ShortcutBuilder>)` | `Desktop(bool=true)`, `StartMenu(bool=true)` — no custom locations |
| `WithFileAssociation(ext, description, icon?=null)` | Icon must be `ImageSource.FromFile` (else throws) |
| `WithAutoStart(bool=true)` / `WithPathRegistration(bool=true)` | System-integration toggles |
| `WithLaunchAfterInstall(bool checkedByDefault=true)` | Wizard's last page offers "Launch {app} when I click Finish" (interactive only; an elevated install starts the app via `explorer.exe`, as the signed-in user). `instella init` templates call it |
| `WithNewerVersionPrompt(bool enabled=true)` | Interactive first install/upgrade only (not silent, not the elevated relaunch, not with `--no-newer-check`): before the scope page, `NewerVersionOffer` lists `packages/{id}/versions` (channel = `WithChannel`, non-deprecated, a build for this OS/arch with an `online` installer), takes the highest version above its own, verifies that version's signed release with the compiled publisher keys (`MustEqual`), and asks Yes (download + run it) / No (install this version) / Cancel (exit 1). The download goes to `%TEMP%\Instella\handoff\{guid}\{fileName}` and must match the signed size + SHA-256; it runs with the original args + `--no-newer-check`, and this process exits with its exit code. Any failure before the prompt (offline, 8 s timeout, private package, unsigned/untrusted release, no installer) is logged and the install carries on silently; a failed download shows a message and installs this version. Build() requires `WithServer` + a publisher key. `instella init` templates call it; the sample calls it when it has a key |
| `WithVersionSelection(bool enabled=true)` | Accepts `--list-versions` (prints versions with an `online` installer for this OS/arch on `--channel`, non-deprecated, newest first, marks latest / this installer, full changelogs; attaches to the parent console), `--app-version <v\|latest>` (own version or `latest` when it is the latest → installs here; another listed version → verified handoff; not listed → 40) and `--choose-version` (Win32 page with a drop-down, this installer's version always offered; cancel → 1; with `--silent` → 40). The handed-over installer gets the args minus the selection flags and `--channel`, plus `--no-newer-check`. Without the call the flags exit 40. Needs `WithServer` + a publisher key. Server errors → 21; an installer that does not verify → 12. Sample: on; `init` template: commented out |
| `WithPrerequisite(Action<PrerequisiteBuilder>)` | `WithName`(req)/`WithBundlePath`/`WithDownloadUrl`+`WithSha256`/`WithDetectionRegistry`/`WithInstallArguments`/`WithSuccessExitCodes` |
| `AddPage(id, Action<PageBuilder>)` | Custom wizard page (unique id) |
| `AddStep(name, Action<StepBuilder>)` | Custom install step (unique name; `migration:` prefix reserved) |
| `AddMigration<T>()` / `AddMigration(InstallMigration)` | Install migration: a class derived from `InstallMigration` (see Install migrations). `new T()`, no scanning, AOT-safe |
| `WithAppManagedAutoStart(runValueName)` | The app writes its own Run value (HKCU per-user / HKLM machine); uninstall deletes it only while it points into the installation. Recorded as an adopted item. Not with `WithAutoStart()` and the app id as the name |
| `OnWindows/OnLinux/OnMacOS(Action<*Builder>)` | Platform sub-builders; the delegate runs only on that OS (Linux/macOS builders are empty) |
| `AddCliFlag<T>(name, default?, help?)` | Typed flag; `T`∈{bool,int,long,string,string[],FileInfo,DirectoryInfo} |
| `MapCliFlag(flagName, "pageId.widgetId")` | Flag value pre-fills the widget (interactive) and answers it (silent) |
| `ConfigureLogging(Action<LoggingBuilder>)` / `WithLogLevel(InstellaLogLevel)` | Logging config |
| `WithPayloadFilter(Action<PayloadFilterBuilder>)` | Glob include/exclude over payload zip entries (build-time) |
| `EnablePreview()` | Opt into `--preview` (see Preview mode) |
| `Build()` | Freeze → `IInstellaInstaller`; throws on missing `WithApp`, bad server URL, server without key, dup step/page names, reserved-flag redeclare, unmappable `MapCliFlag`, an invalid migration (id, duplicate id, blank display name, uninstall + `RunOnce`, `When()` reading `Context`, a condition that can never be true for its timing) |

`Build()` validation of `MapCliFlag`: the flag must be declared with `AddCliFlag`; the key must
name an existing page + widget (an unqualified widget id is accepted only when exactly one page
declares it); types must fit (bool → CheckBox; string → TextInput/Dropdown/RadioGroup/
FolderPicker/FilePicker).

`InstallerBuilder`, `WindowsBuilder`, `LinuxBuilder`, `MacOSBuilder`, `RegistryKeyBuilder`,
`PageBuilder`, `StepBuilder`, `ShortcutBuilder`, `PrerequisiteBuilder` and `PayloadFilterBuilder` are
sealed classes with internal constructors (not interfaces), so builder methods can be added
without breaking anyone.

`OnWindows(w => ...)`: `w.AddRegistryKey(RegistryHive, keyPath, Action<RegistryKeyBuilder>)`;
key builder setters `SetString/SetExpandString/SetDWord/SetQWord/SetMultiString/SetBinary`, each
with a static-value overload and a `Func<InstallContext,T>` dynamic overload. `RegistryHive`:
`AutoFromScope` (HKCU per-user, HKLM machine), `CurrentUser`, `LocalMachine`.

`Prerequisite` (Core.Manifest) is a class with init-only properties: `Name, DownloadUrl, Sha256,
BundlePath, DetectionRegistry, InstallArguments (["/quiet","/norestart"]), SuccessExitCodes
([0,3010]), RequiresElevation`. A download runs only after its SHA-256 matches (checked through
the handle held open while it runs); 3010 sets `ctx.RebootRequired`. `RequiresElevation`
(default true) on Windows without admin rights: the interactive wizard runs the prerequisite
through a UAC `runas` prompt; a silent install refuses it (never prompts). A declined prompt or a
silent refusal fails the install with exit 11. Off Windows it runs with the installer's own rights,
and registry-only detection is skipped with a warning.

### Page builder (`PageBuilder`)

Widgets are added via **typed methods** (not static factories): `Heading(text)`,
`Paragraph(text)`, `ScrollableText(text)`, `BrandImage(ImageSource, maxHeight=96)`,
`TextInput(id,label,default="")`, `CheckBox(id,label,default=false)`,
`RadioGroup(id,label,params RadioOption[])`, `Dropdown(id,label,params string[])`,
`FolderPicker(id,label,defaultPath?)`, `FilePicker(id,label,params FileFilter[])`,
`Progress()`, `StatusLine()`, plus escape hatch `Widget(Widget)`. Control:
`ContinueWhen(Func<PageState,bool>)`, `OnEnter/OnLeave(Func<InstallContext,CancellationToken,Task>)`,
`OnValidate(Func<PageState,ValidationResult>)`, `When(Func<InstallContext,bool>)`,
`InModes(params InstallerMode[])` (default: page shown in `FirstInstall` only, so a licence page
shows on first installs only). `InModes` applies to the wizard and to silent runs alike: the wizard
predicts the mode (FirstInstall, Upgrade or Repair) from its default folder before the first page,
and the Options page refuses a folder holding another copy of the app, so the mode can't change.
`PageState`: `Bool(id,default=false)`, `Text(id,default="")`, `Get<T>/TryGet<T>`, `Set(id,value)`, `Clear/Snapshot`.

**Silent mode (`--silent`)** answers pages without a UI (`PageFlow`): for each applicable page
(`InModes` + `When`) seed widget defaults, apply mapped CLI flags, run `OnEnter`, check
`OnValidate` and `ContinueWhen`, run `OnLeave`. A page that would block exits **14**
(`InstallSilentMissingState`) with a message naming the page and the `MapCliFlag` fix. This runs
before anything is downloaded or written.

### Step builder (`StepBuilder`)

`Execute(StepExecuteAsync)` — required; delegate is
`Task<StepResult>(InstallContext, IStepProgress, CancellationToken)`. Exactly one rollback
mode required (else `Build` throws): `Rollback(StepRollbackAsync)` /
`UseTrackedRollback()` (replays `ctx.Track*`) / `NoRollbackNeeded(reason)`. Hints:
`InStage(InstallStage)` (default `Register`), `Weight(int)` (progress aggregation),
`WithDisplayName(text)` (shown by the wizard as "{text}…" while it runs and in the error details when it
fails; default the step name; built-in steps have their own, e.g. `commit-transaction` = "Moving files into place"),
`When(Func<InstallContext,bool>)`, `Before/After(string name | InstallStage)`,
`PointOfNoReturn(reason)` (defaults to `Finalize`; any other stage is a `Build()` error),
`OnUninstall(Func<InstallContext,CancellationToken,Task>)` (run by uninstall in reverse stage
order, before built-in unregistration — the stub is the user's compiled installer, so the delegate
exists at uninstall time). `StepResult`: `Ok` (static), `Fail(error)`,
`OkWithWarnings(IReadOnlyList<string>)`. `InstallStage` enum: `Prereqs, Extract, Register, Finalize`.

### Widget set (flat, native-mapped)

12 widgets (`UI/Widgets`, positional `record`s): `Heading, Paragraph, ScrollableText,
BrandImage, TextInput, CheckBox, RadioGroup, Dropdown, FolderPicker, FilePicker,
Progress, StatusLine`. Each maps to small native control sets: Win32
(`STATIC`/`EDIT`/`BUTTON`/`COMBOBOX`/`msctls_progress32`), GTK 3
(`GtkLabel`/`GtkEntry`/`GtkCheckButton`/`GtkComboBoxText`/`GtkProgressBar`), Cocoa
(`NSTextField`/`NSButton`/`NSPopUpButton`/`NSProgressIndicator`). `FolderPicker`/`FilePicker`
render as a compound row (label + EDIT + "Browse…" button). Layout is a deterministic
vertical stack at fixed 96-DPI heights — no flex/grid/reflow — except that `ScrollableText`
takes the page's spare height down to the footer buttons (Win32 `pageHeightPx`, GTK expand/fill;
Cocoa keeps 160). One `*WidgetFactory` per
platform pattern-matches the closed `Widget` hierarchy (no per-widget renderer interface).
`ImageSource.FromFile/FromResource/FromBytes`.

### Reserved CLI flags

```
--silent              Headless; answer pages from mapped flags or exit 14
--path <dir>          Install/uninstall/recover target dir
--install --update --uninstall --manage --cleanup --recover   Explicit modes
--scope user|machine  Install scope for UserChoice (default user when silent)
--allow-downgrade     Let an older installer replace a newer installed version
--force               Install into a non-empty folder with no installed manifest
--force-close         Close programs using the app's files without asking (silent upgrade/repair: else exit 24; uninstall: else exit 32)
--no-newer-check      Do not offer a newer version (WithNewerVersionPrompt); added to the handed-over installer
--list-versions       (WithVersionSelection) print installable versions + changelogs for --channel (default WithChannel)
--app-version <v|latest>  (WithVersionSelection) install that version; works with --silent
--choose-version      (WithVersionSelection) pick from a list (interactive only; silent → 40)
--emit-manifest <p>   Build-time: serialize FrozenConfig -> instella.json and exit
--preview[ ...]       Preview mode (only if .EnablePreview())
--log-level <lvl>     Overrides level
--help
```

Canonical set is `Builders/ReservedCliFlags.All`. It also reserves `--elevated-child` (UAC
relaunch), `--token` / `--parent-pid` (cleanup, updater), the updater family (`--app-path --app-exe
--from-version --to-version --channel --use-patch --patch-sha256 --allow-force-close --restart
--no-restart --graceful-timeout --extra-args --repair`, parsed by the shared `UpdaterArgs`), and
the preview family (`--preview-mode --preview-speed --preview-fail`). There is no `--server-url` or
`--package-id`: the updater reads both from the installed manifest. Redeclaring any reserved flag
via `AddCliFlag<T>` throws at `Build()`. An unknown flag or a wrong-typed value prints the error
plus `--help` text and exits 40.

## Two-stage publish (Instella.Installer.Build)

Targets in `src/Instella.Installer.Build/build/Instella.Installer.Build.targets`, all
`AfterTargets="Publish"` (+ helper `_InstellaResolvePaths`).
Everything Instella writes lives under `$(IntermediateOutputPath)instella-*` (payload, host
build, stub, manifest, append stamp), removed by `InstellaClean` (`AfterTargets="Clean"`):

1. **`InstellaPublishPayload`** — deletes `obj/.../instella-payload/`, then publishes each
   `<ProjectReference InstellaPayload="true">` into `instella-payload/<project>/` (an explicit
   `PublishDir`, so stale files never leak and `UseArtifactsOutput`/custom output paths work),
   propagating only `Configuration`+`RuntimeIdentifier` (payload projects own their own
   AOT/trim/self-contained). Collects outputs into `@(InstellaCollectedPayloadFile)`.
2. **`InstellaCollectPayloadPaths`** — second authoring mode: enumerates pre-built dirs from
   `<InstellaPayload Include="..\dir" />` items in place (no copy), with optional `<Subdirectory>`
   metadata for zip-entry namespacing.
3. **`InstellaGenerateManifestFromBuilder`** — builds the installer project again for the build
   host (`RuntimeIdentifier=`, `PublishAot=false`, `SelfContained=false`, output in
   `obj/.../instella-host/`) and runs `dotnet exec <that dll> --emit-manifest <path>`, so any RID
   (win-arm64 on x64, Linux on Windows) works; failure is `INSTELLA0203`. The builder code in
   `Program.cs` must therefore be free of side effects and must not branch on the build
   machine's OS. `InstellaInstallerImpl.RunAsync` serializes `FrozenConfig.ToManifest()` to
   `obj/.../instella-manifest/instella.json` (`InstellaGeneratedManifestPath` overrides).
4. **`InstellaAppendPayload`** — the `AppendPayloadToSelf` task: refuses an exe signed before
   Instella touched it (`INSTELLA0201`) and strips any earlier payload (republishing never stacks;
   its own signed output is un-signed first); stamps a `.ico` app icon into the exe when the
   project has no `<ApplicationIcon>` (Windows build hosts; otherwise `INSTELLA0205`, and
   `<ApplicationIcon>` is the way to set the exe icon); copies the exe to
   `obj/.../instella-stub/`, signs that copy with `InstellaSignCommand` (a command with `{0}` for
   the path; `INSTELLA0204` on failure) and carries it as `.instella/instella[.exe]`; embeds the
   app icon as `.instella/app.ico`; writes a deterministic Deflate ZIP (sorted entries, 1980-01-01
   timestamps, Unix modes in `ExternalAttributes`: 0755 for the main executable and stub); appends
   it with the resolved manifest (the emitted file is never rewritten); signs the finished
   installer. An unchanged republish (same content hashes, exe still carrying that payload) is a
   no-op, and any republish is byte-identical, except when `InstellaSignCommand` adds an
   Authenticode timestamp (the signature then differs per run; the payload and footer hash don't).

```xml
<ItemGroup>
  <ProjectReference Include="..\QuickNotes\QuickNotes.csproj">
    <InstellaPayload>true</InstellaPayload>
    <ReferenceOutputAssembly>false</ReferenceOutputAssembly>
    <Private>false</Private>
  </ProjectReference>
</ItemGroup>
```

The Build package defaults `InstellaEnabled=true` unconditionally; the `instella init`
template / sample sets `InstellaEnabled=false` for **Debug** so `dotnet run` gives a JIT
installer (the dev loop). The package also defaults the Windows `<ApplicationManifest>`
(common-controls v6, per-monitor DPI v2, `asInvoker` — elevation is programmatic, and the
explicit level disables Windows' installer-detection auto-elevation of setup-named exes). That
default is chosen by the **build host** (`'$(OS)' == 'Windows_NT'`), not the target RID: a Windows
installer built on Linux/macOS gets no manifest unless the project sets `<ApplicationManifest>`. At
install time `StageUninstallerStubStep` stages the payload's `.instella/instella[.exe]` as the
installed stub (so a signed installer installs a validly signed `instella.exe`); only a payload
without one falls back to truncating the running installer, with a warning.

## Installer runtime (Instella.Installer.Runtime)

### Entry sequence (`InstellaInstallerImpl.RunAsync`)

1. `DllSearchHardening.Apply()` — `SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32)` on
   Windows, before any UI or COM work (the assembly also has
   `[DefaultDllImportSearchPaths(System32)]`).
2. `--emit-manifest` → write manifest, exit 0. `--preview` → preview runner (exit 40 without
   `EnablePreview()`).
3. `ModeDispatcher.Resolve(args)`; `--help/-h/-?` → help. `CliArgParser.Parse` once: strict (unknown
   flag → 40) except in Update and Recover, which log and ignore unknown options (the updater
   command line is a cross-version contract, below). Everything after `--extra-args` belongs to the app.
   `--path` is normalised here (`InstallPaths.TryNormalize`: rooted against the current directory,
   trailing separators dropped; unusable → 40).
4. Logging init; experimental-platform banner off Windows; warnings for `AllowInsecureServer` /
   `AllowUnsignedUpdates`.
5. Install modes: `EmbeddedResources.EnsurePayloadIntact()` — a footer that exists but fails
   verification exits **12** before any UI or network ("the installer file is damaged"). Only a
   file with no footer at all (lite installer) downloads its payload.
6. Elevation (`ElevationGate`, below), then dispatch to the mode runner.

### Modes & runners

`InstallerMode` (`Instella.Core.Installation`): `FirstInstall, Upgrade, Repair, Update,
Uninstall, Manage, Cleanup, Recover`.

| Mode | Runner | Purpose |
|---|---|---|
| FirstInstall/Upgrade/Repair | `InstallModeRunner` (+ `InteractiveInstallRunner` wizard) | `InstallModeResolver` picks the mode once the path is known; embedded payload or verified server download; built-in + user step pipeline in one `InstallTransaction` |
| Update | `UpdateModeRunner` | `UpdaterEngine` + `HttpUpdateDownloader` (+ `BsDiffEngine`); `UpdateWindow` (Win32) unless `--silent` |
| Uninstall | `UninstallModeRunner` | Recover, confirm, `OnUninstall` hooks, remove integrations/tracked items/files, tombstone + cleanup copy |
| Manage | `ManageModeRunner` | Default when a sibling `.instella-manifest.json` exists: Uninstall / Check for updates / Repair / Close (headless report when silent or off Windows) |
| Cleanup | `CleanupModeRunner` | Tombstone-guarded deletion of what the uninstaller could not delete (started by uninstall only) |
| Recover | `RecoverModeRunner` | Finish/undo an interrupted transaction in `--path` (or the stub's folder), exit |
| (preview) | `PreviewModeRunner` | Drives `--preview` against the simulated executor |

`ModeDispatcher.Resolve(args, siblingManifestProbe?)` → `DispatchResult` (kind, mode, install
path, silent, scope argument, `AllowDowngrade`, `Force`, `ForceClose`, `ElevatedChild`, parsed
`Cli`): explicit flags pick the mode; no flag → `Manage` if a sibling manifest is present else
`FirstInstall`.

**Updater command line (cross-version contract, `docs/compatibility.md`)**: a newer SDK may send
flags an older stub doesn't know, so Update and Recover ignore unknown options. Target binding:
`--update` only targets the stub's own folder (`InstallPaths.StubDirectory`; anything else exits 40
before a manifest is read); uninstall/recover/manage accept a foreign `--path` but never elevate for it
(51), and an `--elevated-child` with a foreign target is refused. The updater refuses a manifest of
another app id (20). Uninstall deletes manifest entries through `SafePath` (escaping entries skipped).

**Install paths are normalised everywhere**: `--path`, `WithInstallPath` results (a throw or
an unusable folder → exit 10) and the Options page (blocks a folder that is not a full path); the
elevated child gets the normalised `--path`; the Installed Apps uninstall command is quoted by
`WindowsCommandLine.Join`, so a trailing backslash can't swallow the closing quote.

**Re-install targeting (`ExistingInstallTargeting` in `InstalledVersionProbe.cs`)**, before scope
resolution: `InstalledVersionProbe.FindInstallationsAsync` looks at HKCU and HKLM `InstallLocation`,
then the two default folders (only folders whose manifest names this app). `--path` takes its scope
from the installation there (a contradicting `--scope` → 40); one installation becomes the target
folder and scope (scope page skipped, the elevated child gets `--path`); two: `--scope` chooses,
silent without it → 40, the scope page names both. The Welcome page says what is installed where and
that it will be upgraded or repaired; the Options page refuses moving an existing installation.

**Install over an existing directory (`InstallModeResolver`)**: recover any transaction first;
no directory or empty → FirstInstall; installed manifest with another `appId` → exit 10; newer
installer → Upgrade; same version → Repair; older → exit 40 unless `--allow-downgrade`; non-empty
directory without a readable installed manifest → exit 10 unless `--force` (a directory holding
only `.instella/` leftovers of an uninstall, no manifest, counts as empty and they are deleted).
Upgrade and Repair default the Options (shortcuts, PATH, auto-start, associations; wizard check boxes
and silent runs) to the existing installation's choices (`InstallModeResolver.FollowExisting`).
The wizard checks early with the read-only `PrecheckAsync` (another `appId`, or older without
`--allow-downgrade`; skipped while a `.instella/txn/` exists): at the default path before the wizard
opens (a one-page `instella-refused` window, reason in a copyable read-only text box, Close, then the same exit code), and on leaving
the Options page for the chosen folder (blocked with a message box). `ResolveAsync` still decides.
If programs use the installation's files (see "Programs using the app's files" below): silent
exits 24 unless `--force-close`; interactive asks Continue (close them) / Try Again / Cancel.

**Programs using the app's files (`RunningAppGate`)**: uninstall, upgrade, repair and updates
first find every process that has a file under the install folder open or loaded (Windows Restart
Manager `RmGetList` over all files; plus processes named like the main exe under the folder, the
only check on Linux/macOS), whatever the process is called. Ignored: this process, processes of
the same program (an elevated child's parent), and Explorer/services/critical processes (never
closed; reported in the log). Closing = `CloseMainWindow`, wait 10 s (updater: 2 s after its own
`GracefulTimeout`), then kill. `RmShutdown` is not used (own timeouts, sends WM_QUERYENDSESSION).

### Step pipeline

`StepExecutor` runs an ordered `IReadOnlyList<IInstallStepExecution>`. Built-in steps (kebab
`Name`s drive `--preview-fail`/`Before`/`After`), ordered by `InstallStage`:

- **Prereqs**: `PrerequisitesStep` (`prerequisites`)
- **Extract**: `ExtractPayloadStep` (`extract-payload`) — begins the `InstallTransaction` and
  **stages** every payload entry (nothing live yet); computes `Delete` ops for files the previous
  version had and this payload lacks
- **Register**: `CreateShortcutsStep` (`create-shortcuts`), `RegisterFileAssociationsStep`
  (`register-file-associations`), `AddToPathStep` (`add-to-path`), `ConfigureAutoStartStep`
  (`configure-auto-start`), `RemoveDroppedIntegrationsStep` (`remove-dropped-integrations`: on an
  upgrade removes a shortcut, association, PATH entry or auto-start the new version no longer
  requests; rollback re-creates exactly what it removed, pointing at the previous exe; so the
  manifest's "created" flags equal "present", and uninstall reverses exactly what exists),
  `StageUninstallerStubStep` (`stage-uninstaller-stub`),
  `RegisterUninstallEntryStep` (`register-uninstall-entry`), and conditional
  `WriteRegistrySpecsStep` (`write-registry-specs`, only when any `OnWindows().AddRegistryKey(...)`)
- **Finalize**: `WriteManifestStep` (`write-manifest`, stages the installed manifest as the
  transaction's last op), `CommitTransactionStep` (`commit-transaction`, swaps everything live)

`OfflineInstallRunner.BuildDefaultSteps()` is the shared 11-step source; `StepOrdering`
(Kahn topo-sort, stage-bucketed) merges in the conditional registry step + user steps. Every user
Finalize step and every `PointOfNoReturn` step is ordered after `commit-transaction` (a user step
may opt earlier with an explicit `Before("write-manifest")`/`Before("commit-transaction")`, but a
PONR step never can). During Extract/Register the new files are staged, not live
(`ExecutableResolver` uses `ExtractedFiles`).

User-step rollback: on any failure — including cancellation, which gets a fresh 5-minute token
for rollback — the executor reverses completed steps LIFO (step `RollbackAsync`, then the tracking
ledger's generic unwind), collecting rollback-of-rollback failures as warnings. The ledger
(`ctx.Track{File,Directory,RegistryValue,RegistryValueRestore,RegistryKey,PathEntry}`) is
persisted into the installed manifest as `trackedItems` and reversed by uninstall. A succeeded
`PointOfNoReturn` step snapshots the completed/ledger indices so only later work unwinds. Failed
upgrades re-register from the previous `InstalledManifest`.

### Install migrations (`Runtime/Migrations/`, public namespace `Instella.Installer.Runtime.Migrations`)

User doc: `docs/migrations.md`. A migration is `class X : InstallMigration` with `Id` (permanent,
1–64 of `[a-z0-9._-]`), `DisplayName` (Progress page; default `Id`), `Timing`
(`MigrationTiming.BeforeCommit|AfterCommit(default)|Uninstall`), `Order` (int, default 0), `RunOnce`
(default true; must be false for `Uninstall`), `protected Condition When()`, `protected Task
ExecuteAsync(ct)`, `protected virtual Task RollbackAsync(ct)` (BeforeCommit only), `protected
MigrationContext Context` / `protected IInstellaLogger Log` (prefix `migration[<id>]: `; both throw
outside a run, so `When()` may only compose conditions).

- **Conditions** (protected, return `Condition`, combine with `& | !`, `Condition.From(ctx => …,
  description)`, `Condition.Always`): `IsFirstInstall/IsUpgrade/IsFirstInstallOrUpgrade/IsRepair/
  IsUninstall()`, `UpgradingFrom("range")` (space-separated comparators `< <= > >= =`, bare version
  = exact, bare major `2` = `2.0.0`, `*` any; Upgrade mode only), `FileExists/FolderExists/
  InstellaInstallationAt(KnownFolder, relative)`, `RunValueExists(name)`, `RunValuePointsInto(name,
  MigrationFolder)`, `RegistryValueExists(hive, key, name)`, `ProcessRunningIn(MigrationFolder)`,
  `IsWindows()`, `IsPerUserInstall()`, `IsMachineInstall()`. A false condition logs the leaf that was
  false. A throwing `From` delegate throws `ConditionEvaluationException` (never "false", so `!` cannot
  turn it into "true"); any exception while evaluating (e.g. access
  denied) is a logged skip, never a failure. A denied read looks absent (as `File.Exists` does); a denied
  write/delete fails the action with the reason.
- **Actions** (protected): `Folder(KnownFolder, relative)` → `MigrationFolder`;
  `StopProcessesInAsync(folder)`, `RepointRunValueAsync(name, from)` (to the new exe, arguments
  kept), `DeleteRunValueAsync(name, pointingInto)`, `AdoptRunValueAsync(name)`, `DeleteFilesAsync(folder,
  names)` (named relative paths, no wildcards, missing skipped), `DeleteFolderIfEmptyAsync(folder)`,
  `RunProgramAsync(exePath, args, successExitCodes)` (full path, no shell, 10 min). A refusal or
  failure throws `MigrationActionException`.
- **`KnownFolder`**: `LocalAppData`, `RoamingAppData` (per-user only: unavailable to a machine-wide
  install), `StartMenuPrograms` (user's or all users' by scope), `ProgramFiles`, `ProgramFilesX86`,
  `ProgramData`, `InstallFolder` (conditions only). Run key = HKCU per-user / HKLM machine.
- **Safety** (`MigrationFolderGuard`): known folder + `SafePath` relative, normalised; actions refuse
  `InstallFolder`, the install folder / inside it / an ancestor of it, volume roots, `PathGuards`
  protected folders (shared with `--cleanup`) and every known-folder root and `LocalAppData\{Programs,
  Microsoft,Packages,Temp}`, `RoamingAppData\Microsoft`, and any folder holding
  `.instella-manifest.json` in it, in an ancestor, or anywhere beneath it (a tree that cannot be listed is
  refused too). Processes: `RunningAppGate` over the folder's files, same prompt /
  `--force-close` rules. `Context.FileSystem`/`PlatformServices` bypass all of it.
- **Pipeline**: steps `migration:<id>` (`MigrationStep`). `BeforeCommit` go just before
  `write-manifest` (after all Register steps); a failure undoes the migration's actions (journal;
  deleted files moved to `%TEMP%\Instella\migration-undo\…` until the install completes), calls
  `RollbackAsync`, fails the install; a later failure rolls it back too. `AfterCommit` go last, after
  every custom Finalize step, as `IBestEffortStep`: a failure is a warning, cancel skips them without
  rollback. Order: timing, `Order`, `Id`.
- **Run-once**: success → id in `MigrationRuntime.Completed` → `completedMigrations` (staged by
  `write-manifest` for BeforeCommit; `WriteManifestStep.AmendManifestAsync` after the loop for
  AfterCommit). Skipped/failed ids are not recorded. Repair/upgrade keep and merge the list, so a
  completed migration never runs again and a missing one catches up on the next installer run.
- **Auto-updates never run migrations** (the updater does not run the pipeline and never replaces the
  stub): installer-only with catch-up; conditions should test state, not the mode or the version.
  Update-time changes belong in the app (`InstellaClient.IsPostUpdate`).
- **Preview**: `MigrationRuntime.IsPreview` makes actions log `preview: would …` and change nothing;
  `PreviewModeRunner` lists `migration:<id>` steps (uninstall list: uninstall migrations first).
- **Seams** (`MigrationRuntime` on `InstallContext.Migrations`): `KnownFolderResolver`,
  `ILockingProcessFinder`, `IProcessCloser`, `AppRunningPrompt`, `ForceClose`, `IProgramRunner`,
  `IsPreview`; `InstallerServices.KnownFolders/ProcessCloser` feed them in harness runs.

### Transactions (`Runtime/Core/Transactions/InstallTransaction.cs`)

One component for first install, upgrade, repair and update. Layout under the install root:
`.instella/txn/{txnId}/journal.json`, `stage/…`, `backup/…` (same volume, so every commit step is
a rename; Windows allows renaming a running `.exe`).

- **Stage**: `StageFileAsync` hashes while writing into `stage/`; an expected-hash or size
  mismatch deletes the staged file and throws `UpdateTrustException`. Paths go through
  `SafePath`; Instella-owned paths only via `StageOwnedFileAsync`.
- **Verify**: re-hash every staged file → `Staged`.
- **Commit** (ignores cancellation): op order = payload replaces, deletes, owned files, installed
  manifest **last**. Each live file is renamed into `backup/`, the staged file into place.
  Renames retry transient locks 5× (100–1600 ms).
- **Rollback / recovery** are idempotent by inspection (which of live/stage/backup exists).
- **Journal** (`TransactionJournal`, Core, `journalVersion` 1): written only at state transitions
  (`Staging → Staged → Committing → Committed`, or `RolledBack`) via `journal.tmp` +
  `Flush(flushToDisk: true)` + rename.
- **Recovery** (`InstallTransaction.RecoverAsync`) runs first in Update, Uninstall, Manage,
  install-over-existing (`InstallModeResolver`) and `--recover`: `Committing` → roll back;
  anything else → delete the transaction folder. A journal with an unknown version → touch
  nothing, exit **23**.
- The SDK's `GetInstallationHealth()` reads only the journal (`HasInterruptedCommit`).
- **Durability**: each staged file is flushed (`FlushFileBuffers`) while its write handle is
  open, before `Committing` is journalled. **Bounded downloads**: staged files, BSPatch output,
  the patch archive and its entries, the full-build spool and the lite payload are capped at their
  signed sizes; JSON responses at 4 MiB.
- **Install-root lock (`InstallRootLock`)**: a named mutex `Global\Instella-{sha256(lower(root))[..32]}`
  held on a dedicated thread (re-entrant per process) by update, uninstall, recover, manage, silent and
  interactive install. Busy → exit **52** (`InstallationBusy`) after 60 s (silent) / 10 s (interactive);
  a mutex created by an elevated process counts as busy, an abandoned one as acquired.

`InstellaOwnedPaths` (Core, internal): `instella.exe`/`instella` (stub), `.instella-manifest.json`,
`.instella/` (txn folders + journals, uninstall tombstone, `app.ico`; in the payload it also
carries the signed stub, which is staged at the root rather than extracted there). Updates never delete or replace
them, and a release may not list them. The stub is refreshed only by running a newer installer
(Upgrade/Repair), not by auto-updates.

### Update engine (`UpdaterEngine`)

Linear pipeline: recover → read installed manifest (stale launch if `--from-version` ≠ installed
→ exit 20) → fetch `release/{appId}/{to}/{os}/{arch}` and `ReleaseVerifier.Verify` against the
installed manifest's `trustedKeys` (`MustBeNewerThan` = installed, `MustEqual` = target) → plan
per file (on-disk hash equals release → unchanged; patch only if the live file still has its
recorded hash; else full-file download from `download/…/file/{path}`; deletions = installed −
release, computed locally, owned paths skipped) → close the app (wait for `--parent-pid`, then
`CloseMainWindow` on every program using the app's files (`RunningAppGate`), `GracefulTimeout`,
then kill only with `--allow-force-close`; else an interactive update asks Continue / Try Again /
Cancel and a silent one exits 24) → stage (patched outputs and downloads hashed
against the signed release; any patch failure falls back to a full download) → commit (new
installed manifest: version, file list, `trustedKeys` rotated if the release carries them) →
complete → on Windows, when the install registered one, set the Installed Apps entry's
`DisplayVersion`/`EstimatedSize` (HKCU or HKLM per `installedPerUser`; failure only logged) → write the
post-update marker `{root}/.instella/post-update/{appId}.{updateId}.json` (real updates only, not repair;
`.tmp` + flush + rename; older markers of the app deleted; a failure is only a warning) → when `--restart`:
silent restarts at once; the window shows "{App} was updated to {v}", counts down `--restart-countdown`
seconds (default 5; 0 closes at once) with "Restart now", and closing it any way restarts too
(`--no-restart`: "Close", no restart). The app is started by `AppLauncher`: through
`%WINDIR%\explorer.exe` when the updater is elevated (so never as admin), directly otherwise, and never
with arguments (`--extra-args` travel in the marker). The manifest refresh also records the release's
channel (not on repair).

Exit mapping: 0 success; 1 cancelled (only before commit); 20 failure before commit (including
any trust failure); 21 server unreachable; 22 commit failed, rolled back; 23 rollback failed or
unknown journal (run `--recover`); 24 app would not close.

**Repair** (`--repair`, Manage window's Repair): same engine with target = installed version,
`MustEqual` only, every file whose on-disk hash differs from the signed release is replaced.
**Check for updates** in the Manage window runs `check-update` and the engine in-process. Both
are hidden without a server URL. With `AllowUnsignedUpdates` and no keys the engine uses the full
build ZIP as the file list (hashed while staging, not verified).

### Scope and elevation (`ScopeResolver`, `ElevationGate`, `WindowsElevationService`)

| `ElevationMode` | Interactive | Silent |
|---|---|---|
| `PerUser` | per-user | per-user |
| `SystemWide` | machine; relaunch elevated if not elevated | machine; exit 51 if not elevated |
| `UserChoice` | built-in scope page **first** (heading + "Version X"); "all users" relaunches elevated | `--scope user\|machine`, default user; machine unelevated → exit 51 |

Relaunch: `ProcessStartInfo{UseShellExecute=true, Verb="runas"}` of the same exe with the original
args (minus earlier `--scope`/`--elevated-child`) + `--scope machine` + `--elevated-child`, quoted
by `WindowsCommandLine.Join`; the parent waits and returns the child's exit code. Declined UAC:
interactive UserChoice returns to the scope page with a notice; otherwise exit 51.
`--elevated-child` still unelevated → exit 51 (no loop). No page answers cross the boundary.

Machine scope: `%ProgramFiles%\{Publisher}\{App}` (x86 build on x64: Program Files (x86)),
`CommonPrograms`/`CommonDesktopDirectory` shortcuts, HKLM classes / `Run` / Uninstall / machine
PATH. The installed manifest records `installedPerUser`; Uninstall, Update and Recover of a
machine install relaunch elevated with the same args (silent Uninstall/Recover exit 51 instead;
Update prompts even when silent). The Manage window opens unelevated and elevates only for
Uninstall/Update/Repair.

### Uninstall and cleanup

Uninstall: recover → read installed manifest (missing → 31) → confirm (interactive Windows) →
programs using the app's files (`RunningAppGate`): interactive asks (Cancel → 1), silent without
`--force-close` → 32 with nothing changed → uninstall migrations (`Order`, `Id`; failure logged) →
`OnUninstall` hooks → adopted items + `WithAppManagedAutoStart` Run values (deleted only while they
point into the install folder) → remove shortcuts/associations/PATH/auto-start/ARP/registry values (then
created keys if empty) and `trackedItems` → delete recorded files (one still locked, e.g. a DLL
loaded by Explorer: moved to `.instella/pending-delete/{guid}-{name}`, which a loaded DLL allows;
exit 32) → delete owned files. What it cannot delete (its own running stub, locked files) goes into
`.instella/uninstall.tombstone` (`appId, installRoot, nonce, createdAt, remaining[]`); the stub
copies itself to `%TEMP%\Instella\cleanup\{nonce}.exe` and starts
`--cleanup --path {root} --token {nonce} --parent-pid {pid}`. No `cmd.exe` trampoline.

Cleanup refuses unless: not a volume root; not a protected folder (profile, Desktop, Documents,
Program Files (x86 too), Windows, System, AppData, LocalAppData, ProgramData, Programs,
CommonPrograms, Temp) or an ancestor of one; tombstone present; nonce matches (constant-time);
tombstone root equals the path. Then: wait for the parent (≤30 s), delete each `remaining` entry
(`SafePath`, retry/backoff), delete `.instella/`, remove empty directories bottom-up. User-added
files stay. Each run deletes sibling temp copies whose `LastWriteTime` is older than 24 h. The copy keeps
the stub's own `LastWriteTime` (`File.Copy` preserves it), so the age is the stub's, not the copy's: an
older copy that is not running goes on the next cleanup run; a running one is locked and skipped.

### Install context

`InstallContext` (passed to every step). Read: `AppName/AppId/AppVersion/InstallPath/Mode/
Scope/Manifest/Options/Platform(IPlatformServices)/FileSystem(IFileSystem)/Log(IInstellaLogger)`.
Also `Cli` (`ctx.Cli.Get<T>("flag")`), `Pages` (`ctx.Pages["license"].Bool("agree")`),
`ExistingInstallation`, and (read-only for steps) `ExtractedFiles`, `ExecutablePath`,
`RebootRequired` (settable). Engine bookkeeping (`PayloadArchive`, `Transaction`, the registration
outcome flags, the manifest companion lists, `Ledger`) is internal. Tracking: the `ctx.Track*`
methods above. `InstallContext` has an internal constructor; tests get one from
`InstellaTestHarness`.

### Manifests (three distinct documents)

- **Build manifest `instella.json`** — `Instella.Core.Manifest.InstellaManifest`: `SchemaVersion`
  (1; readers accept ≤ 1, absent reads as 0), `AppName, AppId, Version, ServerUrl, ExecutableName,
  Shortcuts, PathRegistration, AutoStart, FileAssociations, Prerequisites, Elevation, IconPath,
  Channel, Description, Publisher, HomepageUrl, LicenseUrl, PayloadFilter, PublisherKeys,
  AllowUnsignedUpdates, AllowInsecureServer, DownloadToken`. Produced by `--emit-manifest`, embedded in the
  installer. `Validate()` throws `InvalidManifestException` (including the server-URL rule).
  Serialized via `ManifestJsonContext` (camelCase).
- **Installed manifest `.instella-manifest.json`** — `Instella.Core.Installation.InstalledManifest`,
  staged by `WriteManifestStep` and committed last; read by uninstall/update/manage/SDK. The
  single source of truth about an installation. Flat camelCase, `ManifestVersion=4`: `appName,
  appId, version, installDirectory, executableName, installedAt, hasDesktopShortcut,
  hasStartMenuShortcut, addedToPath, hasAutoStart, hasUninstallEntry, installedPerUser,
  fileAssociations, platform, architecture, serverUrl, channel, trustedKeys(PublisherKey{keyId,
  publicKey}), allowUnsignedUpdates, allowInsecureServer, downloadToken?, files(InstalledFile{relativePath,sha256,size}),
  registry(ManifestRegistryEntry{hive,keyPath,valueName,kind,perUser,isKey}),
  trackedItems(ManifestTrackedItem{kind,path,recursive,hive,valueName}),
  completedMigrations?(string[], sorted ids of run-once migrations that succeeded),
  adoptedItems?(ManifestAdoptedItem{kind:"run-value",name,perUser,source: migration id | "app-managed"}),
  declaredCliFlags(ManifestCliFlag{name,typeName,mapsTo}),
  logging(ManifestLoggingConfig{defaultLevel,fileSinkPath,retainCount})`. `trustedKeys` is copied
  from the build manifest's `PublisherKeys` on a first install or a newer-version installer; a
  repair/downgrade keeps the recorded list (`WriteManifestStep.TrustedKeysAfterInstall`).
  `InstallManifestWriter.ReadAsync` returns null (→ 31 for uninstall) if missing, unreadable, or
  `ManifestVersion != 4`. Writers preserve unknown JSON properties through read-modify-write, so a
  newer field survives an older updater.
- **Release manifest** — `Instella.Core.Trust.ReleaseManifest` (`formatVersion` 1, `appId,
  version, os, arch, channel, createdAt, files[{path,size,sha256,executable}], trustedKeys?,
  installers?[{kind,fileName,size,sha256}]`). `installers` (kind `online`/`offline`,
  `Core.Wire.InstallerKinds`) is optional and additive, so `formatVersion` stays 1 and readers
  that predate it ignore it. Built and signed by `instella upload`; the server stores the exact bytes and serves
  them as `SignedRelease(Manifest base64, Signature base64, KeyId)`. See Trust below.

Version policy for every format: `docs/compatibility.md`.

### Logging (Logsmith Standalone)

`Logsmith.Generator` is `PrivateAssets="all"` in Runtime; the public surface is ours
(`Instella.Core.Logging`): `InstellaLogLevel` (`Trace=0,Debug=1,Info=2,Warn=3,Error=4,Critical=5`),
`IInstellaLogger`, `IInstellaLogSink`, `InstellaLogEntry`, `LoggingBuilder`. User bridge sinks
never see Logsmith types. `LoggingBuilder`: `WithLevel`, `LevelFromEnvironment("INSTELLA_LOG_LEVEL")`,
`DisableEnvironmentLevel`, `File(path)`, `NoFile`, `Console(bool=true)`, `AddSink(IInstellaLogSink)`,
`OnInternalError(Action<Exception>)`. Default-on file sink in `%TEMP%`; level resolved from
`--log-level`, env var, `WithLogLevel(...)`, or compile-time default. Sink failures are swallowed
— logging never fails an install.

### Exit codes

`Instella.Core.Installation.InstellaExitCode` (values are load-bearing; never reordered):

| Code | Name | Meaning |
|---|---|---|
| 0 | `Success` | |
| 1 | `UserCancelled` | Nothing changed |
| 10 | `InstallGeneralFailure` | Install failed and rolled back; foreign app / unmanaged non-empty folder |
| 11 | `InstallPrereqFailed` | A prerequisite failed: hash mismatch, unlisted exit code, failed detection, or elevation declined/refused (`InstallFailureExit`) |
| 12 | `InstallIntegrityFailed` | Footer hash mismatch, built by a newer Instella, a lite-installer download that fails its signed release, or a handed-over installer that fails verification (hash, or a different Authenticode signer when this installer is signed) |
| 13 | `InstallRollbackCompletedWithWarnings` | Install failed and rollback left something behind |
| 14 | `InstallSilentMissingState` | Silent run met a page no mapped flag answered |
| 20 | `UpdateGeneralFailure` | Failed before commit (trust failure, stale launch, …) |
| 21 | `UpdateServerUnreachable` | Also a lite installer that cannot reach its server |
| 22 | `UpdateRolledBack` | Commit failed; previous version intact |
| 23 | `UpdateRollbackFailed` | Rollback failed, or a journal recovery can't act on (unknown or absent version, unparsable, another folder's id, missing with files in `backup/`); nothing touched |
| 24 | `UpdateAppCouldNotClose` | Also silent upgrade/repair with the app running and no `--force-close` |
| 30 | `UninstallGeneralFailure` | |
| 31 | `UninstallManifestMissing` | |
| 32 | `UninstallFilesLocked` | Remaining files handed to cleanup; or silent uninstall refused (nothing changed) because programs use the app's files and no `--force-close` |
| 40 | `UsageInvalidArgs` | Bad flags, `--preview` without opt-in, downgrade without `--allow-downgrade`, non-silent run off Windows, refused cleanup |
| 41 | `UsageUnknownMode` | |
| 50 | `UnsupportedPlatform` | The OS or process architecture is not supported (`PlatformNotSupportedException`) |
| 51 | `InsufficientPrivileges` | Machine scope without elevation in a silent run, or UAC declined; a foreign machine-wide `--path` (never elevated for); a foreign target in an `--elevated-child` |
| 52 | `InstallationBusy` | Another Instella process holds the install folder (`InstallRootLock`: 60 s wait silent, 10 s interactive) |

**Failures are visible.** A non-silent run shows why it stopped in a message box (`IUserMessages`,
`Runners/UserMessages.cs`; the text ends with the log path) as well as on stderr: 12, 40 (plus "Run with --help"),
50, 51, 52, uninstall 30–32, update failures before its window opens, recover and manage errors. The wizard and
the update window show their own failures. An unexpected exception anywhere in `RunAsync` is caught, shown
("Something went wrong: …") and exits 10 (20 update, 30 uninstall). Author callbacks run through `UserCode`: a
throwing `When` shows the page, a throwing `ContinueWhen` / `OnValidate` blocks it, a throwing widget `Visible`
shows it and `Enabled` disables it; nothing leaves the Win32 window procedure. An interactive lite download
exits 21 (network) or 12 (trust), like a silent one. Manage's update check reports a timeout or a non-JSON reply
(21) instead of crashing.

### Payload format

v3 footer (80 bytes) at the logical end of file; layout in
`src/Instella.Core/Internal/PayloadFooterReader.cs` and `docs/footer-format.md`. Fields:
`manifestOffset(8), manifestLength(4), configLength(4), archiveOffset(8), archiveLength(8),
payloadSha256(32), formatVersion(4)=3, flags(4), magic "INSTELLA"(8)`. Layout order: manifest →
config → archive → footer. `VerifyIntegrity()` hashes `[0, footerStart)` with the PE `CheckSum`
and security directory entry read as zeros (so Authenticode signing keeps it valid) and throws
`FooterIntegrityException` on mismatch; a newer `formatVersion` or unknown flag is refused as
"built by a newer Instella" (exit 12). `GetPayloadEndOffset` is PE-aware (parses the
`IMAGE_DIRECTORY_ENTRY_SECURITY` table) so a signed Windows exe's Authenticode blob is skipped.
On macOS the payload can live at `Contents/Resources/payload.instella` in a `.app` bundle
(resolved by Runtime `EmbeddedResources`).

## Trust (Instella.Core.Trust)

- `ReleaseKeys`: `Generate()` (ECDSA P-256), `ExportPrivateKeyPem(key, password?)` (PKCS#8; with a
  password: encrypted, AES-256-CBC, PBKDF2-SHA256, 600,000 iterations), `ImportPrivateKeyPem`,
  `PublicKeyOf` → `PublisherKey(KeyId, PublicKey)`.
- `KeyIds.Compute(spki)` = first 16 hex chars of SHA-256(SPKI DER); `KeyIds.FromPublicKey(base64)`.
- `ReleaseSigner.Sign(manifest|bytes, ECDsa)`: signs `"INSTELLA-RELEASE-V1\n" + bytes`,
  SHA-256, IEEE P1363 (64 bytes). The manifest is serialized once; verifiers check the bytes, then
  parse.
- `ReleaseVerifier.Verify(SignedRelease, TrustPolicy(TrustedKeys, AppId, Os, Arch,
  MustBeNewerThan?, MustEqual?))` → `ReleaseManifest` or `UpdateTrustException`. Checks: key id
  known and matches its key; signature; `formatVersion == 1`; appId (ordinal); os/arch; version
  floor/pin; every path `SafePath`-canonical, not Instella-owned, unique (case-insensitive);
  sha256 64 lower-hex; sizes ≥ 0; `trustedKeys` (if present) non-empty and self-consistent.
  `KeysAfter(release, current)` = release's list or current. `TrustPolicy.Channel` (init, null =
  skip) must equal the release's channel case-insensitively: the SDK passes the requested channel,
  the updater `--channel` (not on repair).
- `ServerUrlPolicy.Check(url, allowInsecure)` (Core.Wire): absolute; `https`, or `http` to a
  loopback host (`localhost`, `127.0.0.0/8`, `::1`), or any `http` with the opt-in. Enforced by
  `InstallerBuilder.Build()`, `InstellaManifest.Validate()` and the CLI (`--allow-insecure`). The
  SDK and the updater use the URL from the installed manifest, which passed this check at build.

Where verification happens: lite installer (`ServerPayloadDownloader`: release pinned to
`AppVersion` against the build manifest's keys; the downloaded ZIP must contain exactly the
release's files with matching size and hash before extraction); SDK `CheckForUpdateAsync`
(`CheckUpdateResponse.Release` against the installed keys, strictly newer, equal to the offered
version — else `Failed`); updater (fetches the release itself; authoritative); patches (unsigned
recipes, outputs hashed against the release); prerequisites (declared SHA-256).

## Test harness (Instella.Installer.Testing)

```csharp
await using var harness = InstellaTestHarness.Create()
    .WithAppId("com.example.test")
    .WithInstallPath(@"C:\FakeInstall\Example")
    .WithPageState("welcome", s => s.Set("agree", true))
    .Build();

var result = await harness.RunStepAsync(new MyCustomStep());        // -> ExecutionResult
Assert.That(result.Success, Is.True);
Assert.That(harness.Registry.Get(RegistryHive.CurrentUser, "Software\\Example", "Name"), Is.EqualTo("test"));
Assert.That(harness.LogSink.Entries.Any(e => e.Message.Contains("configured")), Is.True);
```

Builder also: `WithAppName/WithVersion/WithMode/WithScope/WithElevation/WithServerUrl/
WithCliArgs/WithPlatform(TargetPlatform)/WithFileSystem/WithPlatformServices/WithLogSink/WithInstaller/
WithElevationService(FakeElevationService)`.
Run: `RunStepAsync`, `RunInstallAsync(IReadOnlyList<…>)`, `RunFullAsync()` (drives an attached
`IInstellaInstaller.RunAsync` with the harness's fakes). **`RunFullAsync` never touches the host:** UAC goes
to `FakeElevationService` (not elevated; each relaunch is recorded in `RelaunchRequests` and answered with
`RelaunchExitCode`, default 0), message boxes are recorded in `ShownMessages`, app starts in `LaunchedPrograms`,
Restart Manager finds nothing, the stub folder is the harness install path, and any window (wizard, scope page,
update window, Manage window) throws "interactive UI requested in a harness run; use --silent or provide a host",
which the installer reports and exits 10. Use `--silent` for full runs.
Properties: `Context, Registry(IFakeRegistry), FileSystem(IFakeFileSystem),
PlatformServices(FakePlatformServices), LogSink(RecordingSink), Logger, PageStates, Elevation, ShownMessages,
LaunchedPrograms`. For full runs with migrations: `Builder.WithPayload(files)` (the app files a full
install installs), `RunFullWithArgsAsync(args)` (several runs on one harness: install, repair,
uninstall), `KnownFolderPath(KnownFolder, scope)` (the fake profile migrations see),
`StartProcess(exe, closes)` / `IsProcessRunning(exe)` (fake running programs).

**`MigrationHarness`** runs one migration alone:

```csharp
var result = await MigrationHarness.For<ReplaceOldCopy>()
    .WithFile(KnownFolder.LocalAppData, "ExampleApp/ExampleApp.exe")
    .WithRunningProcess(KnownFolder.LocalAppData, "ExampleApp/ExampleApp.exe")
    .RunAsync();                                        // -> MigrationResult
Assert.That(result.Outcome, Is.EqualTo(MigrationOutcome.Completed));
```

Setup: `For<T>()/For(instance)`, `Mode`, `PreviousVersion` (implies Upgrade), `Scope`, `Elevated`
(implies machine-wide), `Preview`, `InstallPath`, `WithApp`, `AlreadyCompleted`, `WithFile`,
`WithFolder`, `WithInstellaInstallation`, `WithRunValue` (scope's Run key), `WithRegistryValue`,
`WithRunningProcess(root, relativeExe, closes)`, `ForceClose` (default true), `ProgramExitCode`,
`DenyWrites/DenyReads(root, relative)`, `DenyRunKeyWrites/DenyRunKeyReads()` (access denied),
`FailLaterStep` (BeforeCommit rollback), `PathOf`. `RunAsync` validates as `Build()` does.
`MigrationResult`: `Outcome` (`Completed/Skipped/AlreadyCompleted/Failed/RolledBack`), `Ran`,
`SkipReason`, `Error`, `RecordedAsCompleted`, `DeletedFiles`, `StoppedProcesses`,
`RegistryChanges(MigrationRegistryChange{Hive,KeyPath,Name,Before,After})`, `AdoptedItems`,
`ProgramsRun`, `PlannedActions` (preview), `Actions`, `LogLines`, `FileExists/FolderExists/RunValue/PathOf`.

- **`InMemoryFileSystem : IFakeFileSystem`** — dict-backed `IFileSystem`; `DenyWrites(path)` (changes fail
  `AccessDenied`) and `DenyReads(path)` (`Exists` false, reads fail, `EnumerateFiles` throws
  `UnauthorizedAccessException`, as the real APIs do), `AllowAll()`; OS-appropriate path
  comparer; implicit parent dirs; clone-on-write. Seed/inspect: `AddFile/AddDirectory/Snapshot`.
- **`InMemoryRegistry : IFakeRegistry`** — `(hive,keyPath,name)`-keyed, case-insensitive
  cross-platform. `Set/Get/Contains/Delete/DeleteKey/Snapshot`.
- **`FakePlatformServices`** — composes the above; routes registry calls through the fake on
  every platform; `DenyRegistryWrites/DenyRegistryReads(hive, key)` simulate access denied (writes fail,
  reads return null); records calls in public lists: `Shortcuts(+Removed), FileAssociations(+Removed),
  PathEntries(+Removed), AutoStarts(+Removed), UninstallEntries(+Removed)`.
- **`RecordingSink : IInstellaLogSink`** — thread-safe (`ConcurrentQueue`); `Entries`,
  `AtOrAbove(level)`, `Clear`.
- **`RecordingLogger : IInstellaLogger`** — writes straight to a sink, no Logsmith/static state,
  parallel-test-safe.

## SDK (Instella.Sdk)

App-facing (consumed by the user's *app* project, not the installer):

```csharp
if (InstellaClient.GetInstallationHealth() == InstallationHealth.InterruptedUpdate)
{
    await InstellaClient.StartRecoveryAsync();                    // runs `instella --recover`, returns its exit code
    return;                                                       // exit so files can be repaired
}
if (InstellaClient.PostUpdate is { } update)                     // once per user after an update
    Migrate(update.FromVersion);

var result = await InstellaClient.CheckForUpdateAsync();          // or (channel, ct)
if (result.UpdateAvailable)
    await InstellaClient.LaunchUpdaterAndExitAsync(result.Update!); // or StartUpdaterAsync + own shutdown
```

`InstellaClient` (static):
- `TryGetCurrentInfo(out InstellaInfo?)` / `GetCurrentInfo()` (throws `InstellaNotInstalledException`).
  The installed manifest is found by walking up ≤ 3 levels from `AppContext.BaseDirectory`; not
  installed (e.g. F5 in the IDE) is a normal state. `InstellaInfo`: `AppName, AppId, Version,
  InstallRoot, Channel, Architecture, IsPerUser, ServerUrl` (trusted keys internal).
- `CheckForUpdateAsync(ct)` / `CheckForUpdateAsync(channel=null, ct=default)` → `UpdateCheckResult
  (Status: UpToDate|UpdateAvailable|NotInstalled|Failed, Update?, Error?)`; errors are returned as
  `Failed`, never thrown (cancellation is rethrown). Null channel = the installation's channel. An
  update is reported only when its signed release verifies (see Trust).
- `DownloadTokenOverride` (static `string?`): sent with `CheckForUpdateAsync` instead of the installed
  `downloadToken`; affects only the SDK's own check (the updater always uses the token stored at install time, so
  that one needs access too; per-customer tokens need per-customer installers). A token the server refuses looks like
  "no update" (uniform answers, server 8.9); the server's Security log names the token's prefix.
- **Server HTTP**: every client (updater, manage, lite download, newer-version offer, version selection,
  handoff, SDK) builds its client with Core's internal `ServerHttp.Create(server, token, userAgent, timeout)`;
  `ServerAuthHandler` adds `Authorization: Bearer idt_…` only to requests for the server's own scheme, host and
  port (never to presigned S3 redirects or prerequisite downloads). Token sources: updater and Manage → installed
  manifest; lite download, newer-version offer, version selection, handoff → `FrozenConfig`; SDK → override ??
  installed.
- `StartUpdaterAsync(UpdateInfo, UpdateOptions?=null, ct=default)` → `UpdaterStartResult(ProcessId)`:
  starts `{InstallRoot}/instella[.exe]` with `UpdaterArgs.ToArgumentList()` via
  `ProcessStartInfo.ArgumentList` (no string escaping), passes `--parent-pid`, no delay; `ct` is
  observed only before start. The caller must exit promptly. `LaunchUpdaterAndExitAsync(...)` =
  that + `Environment.Exit(0)`.
- `GetInstallationHealth()` → `InstallationHealth.Healthy|InterruptedUpdate` (journal only);
  `StartRecoveryAsync(ct)` → `Task<int>`: runs `instella --recover --path {root} --silent` and awaits it
  (0 recovered; 51 UAC declined for a machine install; 23 unreadable journal). Silent recover may still
  show UAC: the app started it, so a user is there.
- `PostUpdate` → `PostUpdateInfo(FromVersion, ToVersion, Channel, CompletedAt, Arguments)?`;
  `IsPostUpdate`, `PreviousVersion`, `PostUpdateArguments` derive from it. Read once (lazy) from the
  newest valid marker of this `appId` (`markerVersion` 1, ≤ 30 days old); reported once per user: the
  seen `updateId` is kept in `%LOCALAPPDATA%\Instella\{appId}\last-post-update` (a failed write still
  reports). A user who first starts the app within 30 days of an update also sees it once.
- `UpdateOptions`: `AllowForceClose=true, GracefulCloseTimeout=30s, AdditionalArgs?,
  RestartAfterUpdate=true, RestartCountdown=5s, Silent=false, PreferPatch=true`. Ask the user before
  updating (version, `Changelog`, `PatchSize`/`FullSize`); the sample's `UpdateDialog` does. `UpdateInfo` (Core.Update): `Version,
  Changelog, FullSize, PatchAvailable, PatchSize?, PatchSha256?, Mandatory, Channel`.

The SDK does not change the app's DLL search policy.

## Diff and checksum (in Instella.Core)

- Internal (visible to Runtime, Server and CLI): `Instella.Core.BSDiff.BSDiffEncoder.Create(ReadOnlySpan<byte> old, ReadOnlySpan<byte> new, Stream out)`;
  `Instella.Core.BSDiff.BSPatch.Apply(...)` (memory + streamed overloads). BSDIFF40 signature,
  Brotli blocks. The encoder is `BSDiffEncoder`, not `BSDiff`: a type named `BSDiff` inside
  the `Instella.Core.BSDiff` namespace is shadowed by the namespace itself anywhere within
  the `Instella.Core` tree, which forced call sites to alias or fully qualify it.
- `Instella.Core.Utilities.Checksum` — SHA-256: `ComputeSHA256Async(Stream,ct)`,
  `ComputeSHA256(byte[]/span)`, `VerifySHA256Async`. Lowercase hex.
- `Instella.Core.FileSystem.SafePath` — the one place untrusted relative paths (archive entries,
  patch/release paths, uploads, tombstone entries) become file-system paths: rejects absolute,
  drive/UNC, `.`/`..`/empty segments, NUL, `:`, reserved device names, trailing `.`/space.
- Platform enums: the Server persists its own `TargetOS`/`Architecture` (`Instella.Server.Models`,
  0-based) while Core's `TargetPlatform`/`Architecture` are 1-based. Only canonical names cross
  the wire (`PlatformStrings`: `windows|linux|macos`, `x64|x86|arm64|arm32`), and every server-side
  parse goes through `Instella.Server.Models.PlatformMapping`, so the numeric difference is
  harmless. Channels are free names checked everywhere by `Instella.Core.Wire.ChannelNames`
  (1-32 of `[a-z0-9-]`, not starting or ending with '-'; normalised to lower case): `upload/start`,
  `check-update` and `installer/…` answer 400 with `ChannelNames.Rule` for an invalid name. A valid
  name the package doesn't have is just empty (check-update: no update).

## CLI tool (Instella.CLI)

`System.CommandLine` 2.0; one file per command under `Commands/`.

```bash
instella init   --name "Quick Notes" [--output .] [--app-id ...] [--server ...] [--publisher-key B64] [--app path/App.csproj]
instella keys generate --out publisher.key.pem [--password-env VAR] [--force]
instella keys show     --key publisher.key.pem [--password-env VAR]
instella upload --server URL --package com.example.app --version 1.0.0 --path ./publish \
                [--api-key KEY | $INSTELLA_API_KEY] [--os --arch --channel --changelog] \
                [--signing-key PEM|path | $INSTELLA_SIGNING_KEY] [--unsigned] [--allow-insecure] \
                [--trusted-key B64 ...] [--installer web.exe] [--offline-installer setup.exe] \
                [--sign-command CMD --signing-public-key B64 | $INSTELLA_SIGN_COMMAND + $INSTELLA_SIGNING_PUBLIC_KEY] [--draft]
instella publish --server URL --package ID --version V --os OS --arch ARCH [--path DIR] \
                [--installer F] [--offline-installer F] [--yes] [--signing-key ... | --sign-command ... --signing-public-key ...]
instella ci init --host github|gitea --signing kms-azure|kms-aws|kms-gcp|draft|secret --app-project P \
                --installer-project P --package ID --server URL [--name N] [--rid win-x64] [--output .] [--force]
instella list packages --server URL [--allow-insecure]
instella list versions --server URL --package ID [--api-key KEY] [--allow-insecure]
instella delete --server URL --package ID --version V [--api-key KEY] [--yes] [--allow-insecure]
```

- `init` scaffolds `{Name}.Installer/` (`.csproj` + fluent-builder `Program.cs` + `README.md` + a
  `.gitignore` with `*.pem`, no static `instella.json`; the folder drops spaces: "Quick Notes" →
  `QuickNotes.Installer`); `--app` adds the payload reference (relative to the installer project).
  The Build props set `ValidateExecutableReferencesMatchSelfContained=false`, so a self-contained
  installer over a framework-dependent app publishes (no NETSDK1150); package versions come from the CLI's own informational version; the
  template has `.WithPublisherKey(...)` (the given key or a placeholder). Prompts for the name if
  omitted (fails on redirected stdin).
- `keys generate` writes a PKCS#8 PEM (encrypted when `--password-env` names a non-empty env var),
  created owner-only from the start (`Services/PrivateKeyFile.cs`: mode 0600 on Unix, a protected ACL
  for the current user and SYSTEM on Windows) and warns when the file is inside a git work tree;
  refuses to overwrite without `--force`, prints key id, public key, the
  `.WithPublisherKey("…")` line and a backup-key reminder. `keys show` prints the same for an
  existing key.
- `upload` is session-based: `POST api/v1/upload/start` → per-file `POST
  api/v1/upload/{session}/file?path=&sha256=` (SHA-256 dedup) → build + sign the `ReleaseManifest`
  from exactly the uploaded files → `POST api/v1/upload/{session}/complete` with the
  `SignedRelease`; any failure cancels the session (`DELETE api/v1/upload/{session}`). Key from
  `--signing-key` or `INSTELLA_SIGNING_KEY` (PEM text or file path), password from
  `INSTELLA_SIGNING_KEY_PASSWORD`; no key → refused unless `--unsigned`. `--os/--arch` are
  detected from a RID segment in `--path` when omitted. Each `--trusted-key` (base64 SPKI, as
  `keys show` prints) goes into the release's `trustedKeys` rotation list (needs a signing key).
  `--installer` / `--offline-installer` upload the online / offline installer built for this
  version and platform (`POST api/v1/upload/{session}/installer?kind=&fileName=&sha256=`) and list
  them in the signed release's `installers`; the file name (letters, digits, `. _ - + ( )`, spaces,
  ≤ 128 chars) is the download name. The server then serves them at
  `GET api/v1/installer/{id}/{version|latest}/{os}/{arch}/{online|offline}[?channel=]`.
  `--sign-command` signs through an external command instead of a local key (KMS/HSM): placeholders
  `{digest-hex} {digest-base64} {digest-base64url} {digest-file} {message-file}` (digest = SHA-256 of
  `"INSTELLA-RELEASE-V1\n" + manifest bytes`, `ReleaseSigner.MessageFor`); stdout = signature as
  base64/base64url/hex, raw 64-byte or DER, or JSON `result`/`signature`/`Signature`; `cmd /d /s /c`
  on Windows, `/bin/sh -c` elsewhere; 2-minute timeout; verified against `--signing-public-key`
  before upload (`Services/ReleaseSigningKeys.cs`, `Services/SigningOptions.cs`). Cannot be combined
  with `--signing-key`. `{digest-file}` / `{message-file}` are inserted quoted (for `cmd` or `/bin/sh`),
  and the same paths are in the command's environment as `INSTELLA_DIGEST_FILE` / `INSTELLA_MESSAGE_FILE`
  for arguments such as `fileb://$INSTELLA_DIGEST_FILE`. A `--signing-key` holding PEM text warns that
  the process list shows it.
- `upload --draft` (no key; refused together with a signing key or `--unsigned`) sends the unsigned
  manifest bytes as `CompleteUploadRequest.draftManifest`; the server checks them against the session
  like a signed release and stores the build with `VersionBuild.IsDraft`. Drafts are invisible to
  clients: not in `packages/{id}/versions`, check-update, `release`, `download`, `installer`, the
  download page, and never a patch source. `publish` fetches `GET api/v1/drafts/{id}/{v}/{os}/{arch}`
  (`DraftResponse{manifest, uploadedAt, changelog}`, upload permission), checks identity and — with
  `--path` / `--installer` / `--offline-installer` — the exact files and installers (refuses on any
  difference; without `--path` it warns that it signs the server's list), asks unless `--yes`, signs
  the exact bytes and `POST …/publish` with the `SignedRelease`. The server requires byte equality
  with the stored draft and, if the package has registered publisher keys, a signature by one of them;
  the version's `ReleasedAt` becomes the publish time when it is its first published build.
- `ci init` (`Templates/CiTemplate.cs`) writes `.github/workflows/instella-release.yml` or
  `.gitea/workflows/instella-release.yml` and prints the secrets/variables to create: on `v[0-9]*` tags a
  `build` job with no credentials (step `tag` derives VERSION and CHANNEL from the tag, `CiTemplate.TagParseBash`
  / `TagParsePwsh`: `v1.3.0` → 1.3.0 on stable, `v1.4.0-beta[.2]` → 1.4.0 on beta; a bad version or channel fails
  the job; app publish → online installer with `InstellaEnabled=false` → offline installer, copied from the
  installer's assembly name to `{name}-WebSetup-{v}` / `{name}-Setup-{v}`; pwsh on Windows runners, bash
  elsewhere; artifact v4 with `include-hidden-files` on GitHub, v3 on Gitea) and a `publish` job on ubuntu that
  takes VERSION/CHANNEL from the build job's outputs, installs `instella-cli` at the CLI's own version and runs
  `upload --channel "$CHANNEL"` with both installers. Signing: KMS variants set
  `INSTELLA_SIGN_COMMAND` + `INSTELLA_SIGNING_PUBLIC_KEY` and sign in with OIDC (`id-token: write`,
  `environment: release`) on GitHub, with secrets on Gitea (no OIDC); `draft` adds `--draft` and no key;
  `secret` passes `INSTELLA_SIGNING_KEY` from secrets. Guide: `docs/publishing.md`.
- `list` uses `GET api/v1/packages[/{id}/versions]` (`list versions` sends `--api-key` /
  `INSTELLA_API_KEY` when set, for private packages); `delete` uses `DELETE
  api/v1/packages/{id}/versions/{v}`, falls back to `INSTELLA_API_KEY`, prompts unless `--yes`
  (non-interactive stdin without `--yes` fails).
- Every server URL goes through `ServerUrlPolicy` (`--allow-insecure` for non-loopback http).
- Exit codes: 0 success, 1 usage/refused, 2 server error or unreachable, 3 auth (401/403), 4 signing failed
  (`upload` and `publish` sign before anything is sent: `upload` hashes every file and installer locally, builds and
  signs the release manifest, and only then starts the session).
- All routes come from `Instella.Core.Wire.ApiRoutes` (prefix `api/v1`), shared with the server.

## Platform specifics

| Feature | Windows | Linux | macOS |
|---|---|---|---|
| Default install path | per-user `%LOCALAPPDATA%\Programs\{App}`; machine `%ProgramFiles%\{Publisher}\{App}` | `~/.local/share/{app}` | `~/Applications/{App}` |
| Shortcuts | .lnk via COM `IShellLinkW` (STA); machine: `CommonPrograms`/`CommonDesktopDirectory` | `.desktop` (freedesktop, quoted `Exec=`) | desktop symlink only (start menu disabled) |
| File associations | Registry `HKCU\Software\Classes` / `HKLM\Software\Classes` | MIME xml + `xdg-mime` | `duti` |
| PATH | HKCU `Environment` / HKLM session-manager `Environment` + `WM_SETTINGCHANGE` | `.profile`/`.bashrc`/`.zshrc` | `.zshrc` |
| Auto-start | `HKCU`/`HKLM` `Run` key | `~/.config/autostart/*.desktop` | `~/Library/LaunchAgents/*.plist` |
| Elevation | `runas` relaunch (UAC) | none (experimental) | none (experimental) |
| Widget backend | Win32 API (GDI+ PNG) | GTK 3 (GdkPixbuf; preview only, `gtk_init_check` falls back headless) | AppKit/Cocoa (NSImage; preview only) |

`IPlatformServices` (`Instella.Core.Platform`, internal impls `Windows/Linux/MacOSPlatformServices`):
install-path, shortcut/file-assoc/PATH/auto-start/uninstall-entry CRUD (each taking `perUser`),
process enum/terminate, and registry write/delete (no-op off Windows). Operations return
`Task<PlatformResult>` (`record PlatformResult(bool Success, string? Error)`, `PlatformResult.Ok` /
`.Fail(reason)`); queries (`RegistryKeyExistsAsync`, `ReadRegistryValueAsync`) return their value.
Enums: `TargetPlatform{Windows=1,Linux=2,MacOS=3}`, `Architecture{X64,X86,ARM64,ARM32}`,
`RegistryHive`, `InstellaRegistryValueKind`. Windows uses direct vtable dispatch via
`delegate* unmanaged[Stdcall]` (not `[ComImport]`, which silently fails under NativeAOT).

## Preview mode (opt-in)

Dev-time UX review. `.EnablePreview()` lets the built installer accept `--preview` to walk the
wizard without touching the host. The step pipeline is swapped for `SimulatedStepExecutor`
(synthetic 5-tick/step progress; never touches Platform/FileSystem/Ledger), driven by
`PreviewModeRunner`. `--preview-mode=install|upgrade|repair|update|uninstall|manage|cleanup`
(install/upgrade/repair/update → wizard + simulated pipeline; uninstall → synthetic confirm +
pipeline; manage/cleanup → stdout report). `--preview-speed=fast|normal|slow`,
`--preview-fail=<step-name>` (validated against step names → exit 40 on unknown). Title-bar suffix
" — PREVIEW" + per-page banner mark the state.

## Server

`Instella.Server` is an ASP.NET Core REST API (`api/v1`, routes from `ApiRoutes`) + Blazor admin
UI with content-addressed (SHA-256-deduplicated) storage, durable background patch generation,
API-key auth with a permission matrix, IP bans, rate limiting, EF Core migrations, and
local/S3-compatible storage. It stores signed release manifests verbatim (`GET
api/v1/release/{packageId}/{version}/{os}/{arch}`; `check-update` embeds the target's
`SignedRelease`), checks at upload completion that the release names the session's
app/version/platform/channel and exactly the uploaded `(path, sha256, size)` set, and — when a
package has registered publisher keys (admin UI, package properties) — rejects unsigned or
wrongly signed uploads. Clients never rely on the server for integrity. Full reference:
**`src/Instella.Server/llm.md`**; deployment: `docs/server-deployment.md`.

## Compatibility before 1.0

Instella is 0.x: formats, the updater command line, the wire protocol and the public API may change
between releases (`docs/compatibility.md`). The current readers accept only footer v3, installed
manifest version 4 and journal version 1. When a release breaks one of them, its CHANGELOG entry says
so, and installations are reinstalled: uninstall with the old installation's own stub, then install
with the new installer.
