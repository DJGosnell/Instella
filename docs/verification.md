# Verification

`scripts/verify.ps1` is Instella's verification gate. There is no CI (see
[CI and releases](#ci-and-releases)), so run the stages a change touches and paste the summary
into the pull request.

```powershell
./scripts/verify.ps1                          # every stage
./scripts/verify.ps1 -Stage Build,Test        # a subset (always run in the order below)
./scripts/verify.ps1 -Stage Pack -KeepTemp    # keep the scratch directory for inspection
./scripts/verify.ps1 -Stage Pack -VersionSuffix rc.1   # pack and check 0.1.0-rc.1 packages
```

Clone the repository to a short path (for example `C:\src\instella`). Build and Pack copy files into
deeply nested `obj/` folders, and above about 150 characters of clone path they fail with MSB3030
("could not copy ... because it was not found").

Run it from a PowerShell 7 prompt. From another shell use
`pwsh -Command "./scripts/verify.ps1 -Stage Build,Test"`: with `pwsh scripts/verify.ps1 ...` the
script runs as `-File`, which passes `Build,Test` as one string and fails parameter validation.

The script works in a fresh scratch directory under `%TEMP%` and deletes it afterwards unless
you pass `-KeepTemp`. `-Configuration` defaults to `Release`. The run stops at the first
`FAIL`. It ends with a summary table and exits non-zero if any stage failed, or, with `-NoSkip`
(which CI passes), if any stage was skipped.

## Stages

| Stage | What it proves | Needs |
|---|---|---|
| `Build` | The solution builds; warnings are errors (including the public API analyzers) | .NET 10 SDK |
| `Test` | Every unit, contract and integration test passes; at least one test ran. E2E and Docker tests are excluded (`TestCategory!=E2E&TestCategory!=Docker`) | .NET 10 SDK |
| `Migrations` | The server's EF model has no change without a migration (`dotnet ef migrations has-pending-model-changes`) | `dotnet tool restore` (pins `dotnet-ef` in `dotnet-tools.json`) |
| `Audit` | No known-vulnerable package in the solution, direct or transitive | network access to nuget.org |
| `Pack` | Packages pack with the right version and licence; a generated consumer publishes from the local feed with a valid footer and stub; a republish is byte-identical and drops stale files; `win-arm64`, `linux-x64` (with `INSTELLA0001`) and `UseArtifactsOutput` publishes work; the `instella-cli` tool from the feed runs `instella init` for an ordinary app and the generated installer publishes (with `-VersionSuffix`, every package must carry the pre-release version) | .NET 10 SDK |
| `Signing` | The Pack consumer published with `InstellaSignCommand` and a throwaway code-signing certificate is signed, installs `--silent`, and the installed `instella.exe` has a valid signature | Windows, Windows SDK (`signtool.exe`); run together with `Pack` |
| `Aot` | The sample installer publishes with `PublishAot`, with no IL (trim/AOT) warnings, carries a footer, and runs alone in an empty directory | Windows, Visual Studio "Desktop development with C++" (MSVC linker, `vswhere.exe`) |
| `E2E` | A real server (Kestrel), the CLI, a generated app and installer: install, in-app update with restart, a crash in the middle of a commit and `--recover`, tamper refusal, uninstall, and a leak check | Windows; installs into the current user's profile and cleans up |
| `Docker` | The server image builds, starts with empty volumes, answers `/healthz`, accepts an upload, and keeps its state across a restart | Docker with a running daemon |

A stage whose tooling is missing reports `SKIP` and names what is missing. **SKIP is never
PASS**: a release candidate needs every stage to pass on a fully provisioned machine.

## Notes per stage

- **Build and Test** use `-nodeReuse:false`. A persistent MSBuild node would keep the
  `Instella.Installer.Build` task assembly locked and fail the next build (MSB3027/MSB3021);
  `dotnet build-server shutdown` clears a stuck one.
- **Test** includes `UploadConcurrencyTests`, which takes 15–30 s (SQLite write contention).
- **Pack** restores the consumer into a private packages folder, because the package version
  does not change between runs and the user-wide NuGet cache would otherwise hide API changes.
- **Signing** creates the certificate in `Cert:\CurrentUser\My` with a one-day lifetime and
  removes it afterwards. It publishes the project the `Pack` stage generated, so run
  `-Stage Pack,Signing`.
- **Aot** deletes the sample's `bin/` and `obj/` first: switching `PublishAot` without a clean
  silently reuses a non-AOT publish.
- **E2E** builds its binaries with test hooks (`-p:InstellaTestHooks=true`) into its own
  temporary directory, so hooked binaries never reach `bin/`. Set `INSTELLA_E2E_KEEP=1` to keep
  its work directory (builds, server data, logs). The tests are `[Explicit]`, so a plain
  `dotnet test` never runs them; the stage runs them by category.
- **Docker** seeds the admin user and an API key into the volume's database from the host
  rather than driving the interactive setup page.

## Linux and macOS

`scripts/verify.sh` is the portable subset: build, test, pack and packaged consumption for the
host's own RID. The Windows-only stages (Signing, Aot, E2E) have no equivalent there.
`./scripts/verify.ps1 -Stage Build,Test,Migrations,Audit` also runs on Linux (in `pwsh`); the Windows
stages report `SKIP`.

## Provisioning a Windows machine for a full run

1. .NET 10 SDK (any 10.0 feature band; `global.json` rolls forward).
2. Visual Studio 2022 or later, or its Build Tools, with the "Desktop development with C++" workload
   (Aot). Build Tools 2026 verified on 2026-09-24. Of the workload's optional components only
   "MSVC Build Tools for x64/x86" and a "Windows 11 SDK" are needed; CMake, testing tools,
   AddressSanitizer and vcpkg can be unticked.
3. Windows SDK, for `signtool.exe` (Signing). The script uses the newest x64 `signtool.exe`
   under `%ProgramFiles(x86)%\Windows Kits\10\bin`.
4. Docker Desktop with the daemon running (Docker), or run that stage on a Linux machine.
5. PowerShell 7 (`pwsh`).

Run `./scripts/verify.ps1` with no arguments; the summary must show `PASS` for all nine
stages.

## CI and releases

GitHub Actions runs `.github/workflows/ci.yml` (`verify.ps1 -Stage Build,Test,Migrations,Audit,Pack
-NoSkip` on `windows-latest`) for every push to master and every pull request. The other stages
(Signing, Aot, E2E, Docker) run in the release workflow; run them locally for changes that touch them
and paste the summary into the pull request. Server images are published only by release tags;
`.github/workflows/server-image.yml` builds a one-off `:edge` image when run by hand. Nothing is published
from a pull request: PR runs only verify, with read-only permissions
([server-deployment.md](server-deployment.md#getting-the-image)).

### Making a release

1. Set `VersionPrefix` in `Directory.Build.props` and write the `## [x.y.z]` CHANGELOG section. For a
   release the heading carries its date (`## [0.1.0] - 2026-10-01`); a release candidate may still say
   `Unreleased`.
2. Optionally rehearse locally: tag, then `./scripts/release.ps1 -Tag v0.1.0`. It checks that the tree is
   clean and HEAD is the tag, that the tag matches `VersionPrefix` and the CHANGELOG, runs every
   `verify.ps1` stage with `-NoSkip`, packs into `artifacts/<tag>/` (every package at the tag's version,
   symbol packages for Core, Sdk, Runtime and Testing), builds the server image and prints the release
   notes. It publishes nothing.
3. Push the tag: `v0.1.0` for a release, `v0.2.0-rc.1` for a release candidate (`rc`, `beta` or
   `preview`). `.github/workflows/release.yml` then:
   - **check**: the tagged commit is on `master`, then `release.ps1 -CheckOnly` (the tag,
     `VersionPrefix` and CHANGELOG checks);
   - **verify-windows**: every Windows stage on `windows-latest`, then the packages;
   - **verify-docker**: the Docker stage on `ubuntu-latest`;
   - **publish**: waits in the `release` environment until a reviewer approves, then pushes the
     packages and symbols to nuget.org, the server image to ghcr.io (`:<version>`, plus `:latest`
     without a suffix) and creates the GitHub release with the CHANGELOG section as its notes.

nuget.org accepts the push through **trusted publishing**: the publish job's GitHub OIDC token is
exchanged (`NuGet/login`) for an API key that lives one hour, so no key is stored anywhere. The nuget.org
policy names this repository, the workflow file `release.yml` and the environment `release`, and allows
the `Instella.*` and `instella-cli` packages; the `NUGET_USER` repository secret holds the nuget.org
profile name. The publish job is the only one with credentials (`id-token`, `packages` and `contents`
write), and it runs only after both verify jobs pass. `--skip-duplicate` lets a re-run finish a
release that failed half way.

`verify.ps1 -VersionSuffix rc.1` on its own packs and checks pre-release packages without making a
release.
