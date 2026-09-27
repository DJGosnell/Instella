# Instella sample: QuickNotes

A complete example of an Instella-packaged app: `QuickNotes`, a small Avalonia note-taking app,
its installer, and the full lifecycle against a local server: install, update, uninstall.

| Path | Purpose |
|---|---|
| `SampleApp/` | The application (Avalonia WinExe `QuickNotes`). References `Instella.Sdk`: a **Check for updates** button, the post-update hook and the interrupted-update check in `Program.cs`. |
| `SampleApp.Installer/Program.cs` | The installer, described with the fluent builder: identity, shortcuts, file associations, auto-start, a licence page with `--accept-license` for silent installs. |
| `SampleApp.Installer/SampleApp.Installer.csproj` | The installer project. `SampleApp` is attached as the payload with `<InstellaPayload>true</InstellaPayload>`. |
| `SampleApp.slnx` | The two projects plus the Instella projects they reference in-tree. |

The sample references Instella's projects from this repository instead of the NuGet packages;
a real app references `Instella.Installer.Runtime` + `Instella.Installer.Build` (installer) and
`Instella.Sdk` (app).

## Prerequisites

- **.NET 10 SDK**.
- **The MSVC toolchain** for the Release publish, which is NativeAOT. Install "Desktop
  development with C++" (or the C++ Build Tools) from the Visual Studio Installer. Without it the
  publish fails with `MSB3073 ... link.exe ... exited with code 123`; the steps below show a
  non-AOT alternative. With **Build Tools 2026**, also put the Visual Studio Installer folder on
  `PATH` first: its `vcvarsall.bat` runs `vswhere.exe` by name, and without it the publish fails
  with `MSB3073 ... 'vswhere.exe' is not recognized ... link.exe ... exited with code 3`:

  ```powershell
  $env:Path = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer;$env:Path"
  ```

  (`scripts/verify.ps1` does this itself.)
- Windows. All commands below are PowerShell, run from the repository root.

> Toggling `PublishAot` without wiping `bin/` and `obj/` first silently reuses the previous
> publish directory, and you get an apphost plus loose DLLs instead of one native binary.
> Always clean when you change it.

## The lifecycle, step by step

### 1. Start a local server

```powershell
$work = "$env:TEMP\instella-sample"
$env:ASPNETCORE_ENVIRONMENT = 'Development'           # allows the admin cookie over plain http
$env:INSTELLA_CONFIG_DIR = $work                      # database, keys and setup token go here
$env:Storage__Local__BasePath = "$work\packages"
dotnet run --project src/Instella.Server -- --urls http://127.0.0.1:5080
```

In a second terminal, read the setup token and open the admin UI:

```powershell
Get-Content "$env:TEMP\instella-sample\setup-token"
start http://127.0.0.1:5080/setup
```

Create the admin account. Then, in the admin UI, create the package `com.instella.quicknotes`
and an API key that can upload to it. Plain `http` is accepted because the server is on
loopback; a real server runs behind HTTPS ([server-deployment.md](../docs/server-deployment.md)).

### 2. Create a publisher key

```powershell
$work = "$env:TEMP\instella-sample"
dotnet run --project src/Instella.CLI -- keys generate --out "$work\quicknotes.key.pem"
```

It prints the public key. Register it on the package (package properties, publisher keys) so the
server accepts only releases signed with it, and keep it for the next step. A real app also adds
an offline backup key ([signing-and-keys.md](../docs/signing-and-keys.md)).

### 3. Build the installer

The sample reads the server URL and publisher key from build properties, so it can target this
local server without editing `Program.cs` (a real installer hard-codes both):

```powershell
$key = '<public key from step 2>'
dotnet publish samples/SampleApp.Installer -r win-x64 -c Release `
    -p:SampleServerUrl=http://127.0.0.1:5080 -p:SamplePublisherKey=$key
```

Without the MSVC toolchain, add `-p:PublishAot=false -p:PublishSingleFile=true`. That changes only
the installer (a larger, self-contained single file); the payload app is always published with its
own settings, exactly like the upload in step 4.

The installer is `samples\SampleApp.Installer\bin\Release\net10.0\win-x64\publish\QuickNotes.Installer.exe`:
one file carrying the QuickNotes payload and the manifest. Ship it as is, or rename it (for
example `QuickNotes-Setup.exe`).

### 4. Publish version 1.2.0 to the server

The installer carries 1.2.0 (`<Version>` in both projects). Upload the same version, so the
server knows the release that is installed:

```powershell
dotnet publish samples/SampleApp -c Release -r win-x64 -o "$work\out\1.2.0"
$env:INSTELLA_API_KEY = '<API key from step 1>'
dotnet run --project src/Instella.CLI -- upload --server http://127.0.0.1:5080 `
    --package com.instella.quicknotes --version 1.2.0 --path "$work\out\1.2.0" `
    --os windows --arch x64 --signing-key "$work\quicknotes.key.pem"
```

### 5. Install

Run `QuickNotes.Installer.exe` for the wizard (scope choice, licence page, progress), or install
silently:

```powershell
.\QuickNotes.Installer.exe --silent --scope user --accept-license
```

Without `--accept-license` a silent install exits 14 and names the licence page: every page must
be answerable from the command line. The default location is
`%LOCALAPPDATA%\Programs\QuickNotes`; `--path` overrides it. The install folder holds the app,
`.instella-manifest.json` and **`instella.exe`**, the installed copy of the installer's stub,
which handles updates, repair, uninstall and crash recovery for this installation.

### 6. Update

Publish the app as 1.3.0 (`-p:Version` also sets the assembly and file versions) and upload it:

```powershell
dotnet publish samples/SampleApp -c Release -r win-x64 -p:Version=1.3.0 -o "$work\out\1.3.0"
dotnet run --project src/Instella.CLI -- upload --server http://127.0.0.1:5080 `
    --package com.instella.quicknotes --version 1.3.0 --path "$work\out\1.3.0" `
    --os windows --arch x64 --signing-key "$work\quicknotes.key.pem"
```

The server generates a binary patch from 1.2.0 in the background. Start the installed QuickNotes
and click **Check for updates**: the app starts `instella.exe --update` and exits; the updater
verifies the signed release, applies the patch (or downloads changed files), swaps the files in,
and restarts QuickNotes, whose status bar shows "Updated from 1.2.0".

### 7. Uninstall

From Windows Settings (Installed apps), or:

```powershell
& "$env:LOCALAPPDATA\Programs\QuickNotes\instella.exe" --uninstall --silent
```

Uninstall removes the files, shortcuts, file associations and registry entries the installation
created, and leaves nothing behind in the install folder except files the user added.

## Other things to try

```powershell
dotnet run --project samples/SampleApp.Installer -- --help        # every flag, including --accept-license
dotnet run --project samples/SampleApp.Installer -- --silent      # exits 14: licence not accepted
.\QuickNotes.Installer.exe --scope machine                        # Program Files, with a UAC prompt
& "$env:LOCALAPPDATA\Programs\QuickNotes\instella.exe"            # the Manage window: repair, update, uninstall
```

`dotnet run` builds a Debug, JIT installer with no payload (`InstellaEnabled` is `false` for
Debug), which is quick for iterating on pages and steps; it cannot actually install the app.

## How this relates to the tests

The same lifecycle (local server, CLI keys and signed uploads, silent install with
`--accept-license`, in-app update with restart, uninstall) runs automatically in
`tests/Instella.E2E.Tests` (`scripts/verify.ps1 -Stage E2E`), plus a crash in the middle of an
update and a tampered release. The E2E test generates a small console app and installer rather
than building this Avalonia sample, so it needs neither a display nor the MSVC toolchain; the
`Aot` stage publishes this sample to prove it builds with NativeAOT.
