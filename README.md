<p align="center">
  <img src="assets/logo/instella-icon.svg" alt="Instella logo" width="128" />
</p>

<h1 align="center">Instella</h1>

Instella is an installer and update system for .NET applications. You describe the installer in
C#, in a small `*.Installer` project next to your app, and `dotnet publish` produces **one
self-contained executable** that carries your app. The same executable, installed next to your
app as `instella.exe`, later updates, repairs and uninstalls it.

- **Installer as code.** A fluent builder for identity, shortcuts, file associations, PATH,
  auto-start, registry values, prerequisites, wizard pages and custom install steps. No separate
  authoring tool; your compile is the installer.
- **Install migrations.** Small classes that deal with what came before: replacing a copy installed
  without Instella, moving old settings, cleaning up after an older version. They run once, under a
  condition, inside safety rules, and can be tested on fakes.
- **Signed updates.** Every release is signed with your publisher key. An installed app accepts
  an update only if the signature verifies, it is for this app, OS and architecture, and it is
  newer than what is installed. The update server cannot forge or downgrade a release.
- **Updates that cannot half-apply.** Files are staged and verified, then swapped in by
  renames under a journal. A crash or power loss mid-update is rolled back or finished on the
  next start. Binary patches (BSDiff) are used when they are smaller.
- **A server to distribute releases.** An ASP.NET Core server with an admin UI, API keys,
  content-addressed storage (local disk or S3) and background patch generation, shipped as a
  container image.

## Support

| Platform | Status |
|---|---|
| Windows 10 / 11, x64, arm64, x86 | **Supported**: interactive wizard, silent installs, per-user and machine-wide (UAC) installs |
| Linux | Experimental: silent installs only |
| macOS | Experimental: silent installs only |

On Linux and macOS the runtime prints an "experimental" banner, a non-silent run exits 40 with a
hint to use `--silent`, and publishing for a non-Windows RID warns `INSTELLA0001` (add it to
`<NoWarn>` to acknowledge). Experimental platforms are not covered by the compatibility
guarantees; the [roadmap](docs/roadmap.md) lists what is planned for them. Any RID can be
published from any build host.

Instella needs the .NET 10 SDK. A NativeAOT installer (recommended: one small native exe) also
needs the MSVC build tools on Windows, see [below](#aot-prerequisite-windows).

## Quick start

This walks through the whole lifecycle: create an installer, sign a release, run a server,
publish, and update. It assumes an app project `QuickNotes/QuickNotes.csproj`.

**1. Install the CLI and create a publisher key.** Keep the private key out of the repository
(a password manager or your CI secret store), and make a second, offline backup key now:

```bash
dotnet tool install --global instella-cli
instella keys generate --out ~/keys/quicknotes.key.pem --password-env QN_KEY_PASSWORD
instella keys generate --out ~/keys/quicknotes-backup.key.pem --password-env QN_KEY_PASSWORD
```

Each command prints the public key. Both go into the installer; see
[signing-and-keys.md](docs/signing-and-keys.md).

**2. Create the installer project** next to the app:

```bash
instella init --name QuickNotes --server https://updates.example.com --publisher-key <public key>
```

`QuickNotes.Installer/Program.cs` is where the installer is described:

```csharp
using Instella.Core.Manifest;                 // ElevationMode
using Instella.Installer.Runtime.Builders;
using Instella.Installer.Runtime.UI.Widgets;  // ImageSource

return await InstellaInstaller.Create()
    .WithApp("QuickNotes", "com.example.quicknotes", new Version(1, 0, 0))
    .WithServer("https://updates.example.com")
    .WithPublisherKey("MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE...")   // primary key
    .WithPublisherKey("MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE...")   // offline backup key
    .WithPublisher("Example Corp")
    .WithIcon(ImageSource.FromFile("../QuickNotes/Assets/icon.ico"))
    .WithShortcuts(s => s.Desktop().StartMenu())
    .WithElevation(ElevationMode.UserChoice)
    .AddPage("license", p => p
        .Heading("Licence")
        .ScrollableText("...")
        .CheckBox("agree", "I accept the licence")
        .ContinueWhen(s => s.Bool("agree")))
    .AddCliFlag<bool>("accept-license")
    .MapCliFlag("accept-license", "license.agree")   // silent installs pass --accept-license
    .Build()
    .RunAsync(args);
```

The app is attached as the payload by a project reference in `QuickNotes.Installer.csproj`
(`instella init` writes it):

```xml
<ProjectReference Include="..\QuickNotes\QuickNotes.csproj">
  <InstellaPayload>true</InstellaPayload>
  <ReferenceOutputAssembly>false</ReferenceOutputAssembly>
  <Private>false</Private>
</ProjectReference>
```

**3. Let the app update itself.** Reference `Instella.Sdk` from the app:

```csharp
using Instella.Sdk;

// At startup.
if (InstellaClient.GetInstallationHealth() == InstallationHealth.InterruptedUpdate)
{
    await InstellaClient.StartRecoveryAsync();   // puts the previous version back
    return;
}
if (InstellaClient.PostUpdate is { } update)     // the first start after an update, once per user
    MigrateUserData(update.FromVersion);

// From a "Check for updates" command.
var result = await InstellaClient.CheckForUpdateAsync();
if (result.UpdateAvailable)
    await InstellaClient.LaunchUpdaterAndExitAsync(result.Update!);
```

**4. Build the installer.**

```bash
cd QuickNotes.Installer
dotnet publish -r win-x64 -c Release
# -> bin/Release/net10.0/win-x64/publish/QuickNotes.Installer.exe
```

Set `InstellaSignCommand` to Authenticode-sign the installer and the stub it installs
([distribution-and-signing.md](docs/distribution-and-signing.md)). `dotnet run` gives a quick
JIT build of the installer without payload work, for iterating on pages and steps.

**5. Run the server.** Build the image (`scripts/build.ps1`) and start it with the repository's
`deploy/docker-compose.yml`, behind a reverse proxy that terminates TLS:

```bash
docker compose up -d
cat config/setup-token        # enter it at https://updates.example.com/setup
```

In the admin UI, create the package `com.example.quicknotes`, create an API key for it, and
register both publisher public keys on the package. See
[server-deployment.md](docs/server-deployment.md) for TLS, `KnownProxies`, backups and upgrades.

**6. Publish a release.** Upload the app's publish output, signed with the publisher key:

```bash
dotnet publish QuickNotes -c Release -r win-x64 -o out/1.0.0
export INSTELLA_API_KEY=...  INSTELLA_SIGNING_KEY_PASSWORD=...
instella upload --server https://updates.example.com --package com.example.quicknotes \
    --version 1.0.0 --path out/1.0.0 --os windows --arch x64 \
    --signing-key ~/keys/quicknotes.key.pem
```

Ship `QuickNotes.Installer.exe` to users. For an update, bump the app version, publish, and
upload 1.1.0 the same way. The server builds a binary patch in the background; installed copies
find the update on their next check, verify it, and apply it.

A complete, buildable example is in [`samples/`](samples/).

## Security model in brief

- The installer you ship is trusted through its Authenticode signature and an embedded SHA-256
  of its own contents; a corrupted or modified installer refuses to run (exit 12).
- Updates are trusted only through your publisher signature. The server is trusted for
  availability, not integrity: it can withhold an update, but not alter, forge or downgrade one.
- HTTPS is required for the update server (plain HTTP only on loopback, or by explicit opt-out).
- Downloaded prerequisites must match a SHA-256 in the manifest before they run.
- Machine-wide installs elevate through a UAC prompt; per-user installs never elevate.

Details, including what is out of scope, are in [security-model.md](docs/security-model.md).

## Packages

| Package | Reference it from | Purpose |
|---|---|---|
| `Instella.Installer.Runtime` | your **installer** | Fluent builder, wizard UI, install/update/uninstall pipeline |
| `Instella.Installer.Build` | your **installer** | MSBuild targets that append the payload during `dotnet publish` |
| `Instella.Sdk` | your **application** | `InstellaClient`: update checks, launching the updater, post-update detection |
| `Instella.Installer.Testing` | your **tests** | In-memory file system and registry, fake platform services |
| `Instella.Core` | (transitive) | Shared types: manifests, platform abstractions, exit codes |
| `instella-cli` | `dotnet tool` | `instella init \| keys \| upload \| publish \| list \| delete \| ci` |

`Instella.Server` is not a NuGet package; it ships as a container image.

## Documentation

| Document | What it covers |
|---|---|
| [`llm.md`](llm.md) | Full reference: builder API, CLI flags, exit codes, modes, SDK, file formats |
| [`src/Instella.Server/llm.md`](src/Instella.Server/llm.md) | Server reference: API, auth, configuration, storage |
| [security-model.md](docs/security-model.md) | Trust chain, what the server can and cannot do, elevation |
| [publishing.md](docs/publishing.md) | Publishing releases and installers by hand or from GitHub/Gitea Actions (`instella ci init`) |
| [migrations.md](docs/migrations.md) | Install migrations, and replacing an existing (pre-Instella) installation |
| [signing-and-keys.md](docs/signing-and-keys.md) | Publisher keys: generation, storage, rotation, loss, KMS signing |
| [distribution-and-signing.md](docs/distribution-and-signing.md) | Authenticode signing of installers and the stub |
| [server-deployment.md](docs/server-deployment.md) | Docker, reverse proxy and TLS, backups, upgrades |
| [compatibility.md](docs/compatibility.md) | Format versions and the public API policy |
| [footer-format.md](docs/footer-format.md) | The installer payload footer |
| [verification.md](docs/verification.md) | Running `scripts/verify.ps1` and its stages |
| [roadmap.md](docs/roadmap.md) | What is not in Instella yet |
| [CHANGELOG.md](CHANGELOG.md) | Release notes |

## Building from source

Requires the **.NET 10 SDK** (pinned in `global.json`, rolling forward to the latest feature band).

```bash
dotnet build Instella.sln -c Release -nodeReuse:false
dotnet test  Instella.sln -c Release
```

`-nodeReuse:false` is not optional. Persistent MSBuild nodes keep a lock on
`Instella.Installer.Build.dll` and its copy of `Instella.Core.dll`, so a second build in the
same session fails with MSB3027/MSB3021. If you hit that, run `dotnet build-server shutdown`.

`scripts/verify.ps1` is the full local gate (build, tests, migrations, audit, packaging, AOT,
signing, end-to-end, Docker); `-Stage` runs a subset. See [verification.md](docs/verification.md).

### AOT prerequisite (Windows)

Publishing an installer with `PublishAot` needs the **MSVC toolchain**, which the .NET SDK does
not install. Without it `dotnet publish -r win-x64 -c Release` fails with
`MSB3073 ... link.exe ... exited with code 123`.

Install "Desktop development with C++" (or the standalone C++ Build Tools) from the Visual
Studio Installer. If `vswhere.exe` is not on `PATH`, add it; it usually lives in
`C:\Program Files (x86)\Microsoft Visual Studio\Installer\`.

> Switching `PublishAot` on or off **without wiping `bin/` and `obj/` first** silently reuses
> the previous publish directory, producing an apphost plus loose DLLs instead of a single
> native binary. Always clean when you change it.

## License

MIT, see [LICENSE](LICENSE).
