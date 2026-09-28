# Publishing Releases

This guide covers getting a version of your app to users: building the app and its installers,
signing the release, and uploading it to your Instella server, first by hand and then from GitHub
Actions or Gitea Actions.

## What a release is

For each version and platform you publish three things:

| What | Built by | Who uses it |
|---|---|---|
| The app's files (`dotnet publish` of the app) | your build | installed apps updating themselves; the online installer |
| The online installer (small; downloads the app while it installs) | `dotnet publish` of the installer project with `-p:InstellaEnabled=false` | new users; older installers handing over to a newer version |
| The offline installer (carries the app) | `dotnet publish` of the installer project | new users without a reliable connection |

`instella upload` sends all three and a **release manifest** that lists every file and both
installers with their sizes and SHA-256 hashes. The manifest is signed with your **publisher key**
(an ECDSA P-256 key, see [signing-and-keys.md](signing-and-keys.md)). Installed apps, the online
installer, and older installers offering a newer version all check that signature against the keys
compiled into the installer before they use anything the server sends. The server only stores and
relays; it never holds a signing key.

The installers themselves can also carry an **Authenticode** signature, which is what Windows and
SmartScreen check when someone downloads them (see
[distribution-and-signing.md](distribution-and-signing.md)). That signature is applied when you
build the installers, on your machine or in CI, never on the server.

After a release is published the server offers it:

- to installed apps, as an update (`Instella.Sdk`);
- on the public download page, `https://<server>/download/<package id>`, for packages with open
  download access;
- at stable links that always give the newest installer:
  `https://<server>/api/v1/installer/<package id>/latest/windows/x64/online` (or `offline`; add
  `?channel=beta` for another channel).

### Channels and "latest"

Every release belongs to one **channel**: `stable` unless you upload with `--channel`. A channel
is any name of 1-32 lowercase letters, digits and hyphens (`beta`, `nightly`, `insiders-2`); the
server creates it with the first upload to it. An installer or app follows the channel it was
built with (`WithChannel(...)`), and a version number belongs to exactly one channel.

**"Latest"** on a channel is the highest version number that is published (not a draft) and not
deprecated. The release date plays no part, so a hotfix for an older line (`1.2.5` after `1.3.0`)
never becomes latest. Update checks, the `latest` installer links, the download page and patch
generation all use this one definition. Versions are compared canonically: `1.3` and `1.3.0` are
the same version, and the server refuses a second spelling of a version it already has.

**Holding a rollout.** On the package's page in the admin UI, the Channels table can cap latest
at a version ("Latest is capped at"). Apps then update only up to the cap, new users get the capped
version, and nothing is deleted; clear the cap to release the newer versions. A platform that has
no build at the cap gets the highest lower version that has one.

Installers built with `WithNewerVersionPrompt()` offer a newer published version before they
install, and installers built with `WithVersionSelection()` accept `--list-versions`,
`--app-version` and `--choose-version`. Both hand over to the other version's own online installer,
so that version is installed by its own install process.

## Publishing by hand

One-time setup:

```bash
dotnet tool install --global instella-cli
instella keys generate --out publisher.key.pem --password-env KEY_PASSWORD   # keep it (and a backup key) offline
instella init --name "Quick Notes" --server https://updates.example.com --publisher-key <public key printed above>
```

`instella init` writes the installer project. Its version is the project's `<Version>`, which
`dotnet publish -p:Version=...` overrides. In the server's admin UI, create the package (its id is
the installer's app id) and an API key that may upload to it.

**API key permissions.** Give CI and publishing keys only *Can Upload*. Editing, deprecating and
deleting versions (`instella delete`, the version endpoints) needs *Manage versions (edit,
deprecate, delete)*, a separate permission, so a leaked upload key cannot remove releases.

**Private packages.** A package whose download access is *Package Key Required* answers requests
without valid credentials exactly as if it did not exist (404). For installations to install and
update it, create a **download token** in the package's "Download tokens" section of the admin UI
(an `idt_...` string, shown once; it can expire and be revoked) and compile it into the installer:

```csharp
.WithServer("https://updates.example.com")
.WithDownloadToken("idt_...")
```

The installer, the installed app (SDK), the updater and the Manage window send the token, only to
that server. The token only allows downloading that one package; it is not a secret, because anyone
with the installer can read it. It limits casual access, it does not protect the files from your
users. An app can replace it at run time with `InstellaClient.DownloadTokenOverride`. Tokens do not
work for *Master Key Required* packages.

For each release (here 1.3.0 for Windows x64):

```bash
# 1. The app's files. These are what installations receive, so publish them exactly like this.
dotnet publish src/QuickNotes -c Release -r win-x64 -p:Version=1.3.0 -o out/app

# 2. The online installer first, then the offline one (the offline build adds the app to its copy).
dotnet publish QuickNotes.Installer -c Release -r win-x64 -p:Version=1.3.0 -p:InstellaEnabled=false -o out/online
dotnet publish QuickNotes.Installer -c Release -r win-x64 -p:Version=1.3.0 -o out/offline
cp out/online/QuickNotes.Installer.exe  out/QuickNotes-WebSetup-1.3.0.exe
cp out/offline/QuickNotes.Installer.exe out/QuickNotes-Setup-1.3.0.exe

# 3. Upload and sign.
export INSTELLA_API_KEY=...
export INSTELLA_SIGNING_KEY=./publisher.key.pem
export INSTELLA_SIGNING_KEY_PASSWORD=...
instella upload --server https://updates.example.com --package com.example.quicknotes --version 1.3.0 \
    --os windows --arch x64 --path out/app \
    --installer out/QuickNotes-WebSetup-1.3.0.exe --offline-installer out/QuickNotes-Setup-1.3.0.exe \
    --changelog "Faster search; fixes a crash when a note is empty."
```

Check the result on the download page, or with `instella list versions --server ... --package ...`.
The installer file names are the names users download; they may contain letters, digits,
`. _ - + ( )` and spaces.

## Publishing from CI

`instella ci init` writes a release workflow for GitHub Actions or Gitea Actions. Pushing a tag
such as `v1.3.0` builds the app and both installers in a **build** job, which has no credentials,
and uploads them from a separate **publish** job:

```bash
instella ci init --host github --signing kms-aws \
    --app-project src/QuickNotes/QuickNotes.csproj \
    --installer-project QuickNotes.Installer/QuickNotes.Installer.csproj \
    --package com.example.quicknotes --server https://updates.example.com
```

It writes `.github/workflows/instella-release.yml` (or `.gitea/workflows/instella-release.yml`) and
prints the secrets and variables to create. `--rid` changes the platform (default `win-x64`),
`--name` the name used in the installer file names, and `--force` overwrites an existing workflow.

### Tags, versions and channels

The tag names both the version and the channel:

| Tag | Version | Channel |
|---|---|---|
| `v1.3.0` | 1.3.0 | stable |
| `v1.4.0-beta` | 1.4.0 | beta |
| `v1.4.0.1-beta.2` | 1.4.0.1 | beta (`.2` only keeps the tag unique) |

The build job works both out once and hands them to the publish job, which uploads with
`--channel`. A tag whose version is not `1.2.3` or `1.2.3.4`, or whose suffix is not a channel name
(lowercase letters, digits and hyphens), fails the build before anything is compiled.

A version number belongs to one channel. A beta and the stable release that follows it therefore
need different numbers: for example `v1.4.0-beta` followed by `v1.4.1`, or, after `v1.4.0`,
four-part betas such as `v1.4.0.1-beta` and `v1.4.0.2-beta` followed by `v1.4.1`.

### Choosing how CI signs

A pipeline that publishes automatically will always be able to cause a signature. The goal is that
it can *use* the key only while an approved run is going, cannot copy the key, and leaves a record.

| `--signing` | The key lives | CI can | Choose it when |
|---|---|---|---|
| `kms-azure`, `kms-aws`, `kms-gcp` (recommended) | in a cloud key service, created non-exportable | ask for signatures while the run's short-lived identity is valid | you want releases fully automated |
| `draft` | only with a maintainer | upload an unsigned draft that nobody sees until a maintainer publishes it | no pipeline may sign at all |
| `secret` | in a CI secret (PEM text) | read the key | you have no key service; limit who can run the job |

**KMS.** Create an ECDSA P-256 signing key in the key service, marked non-exportable, and allow
the pipeline's identity to *sign* with it, nothing else. `instella upload --sign-command` sends
only the SHA-256 digest of the manifest to the key service and checks the returned signature
against `--signing-public-key` before it uploads anything. Put the same public key in the installer
with `.WithPublisherKey(...)`. The commands for each service are in
[signing-and-keys.md](signing-and-keys.md#signing-with-a-key-that-never-leaves-a-kms-or-hsm---sign-command).

**Draft.** CI uploads with `--draft`: files and installers are stored, but installers, apps and the
download page do not see the version. A maintainer then runs, on their own machine:

```bash
instella publish --server https://updates.example.com --package com.example.quicknotes \
    --version 1.3.0 --os windows --arch x64 \
    --path <the run's app artifact> --installer <...WebSetup...> --offline-installer <...Setup...> \
    --signing-key publisher.key.pem          # or --sign-command with a hardware token or KMS
```

`publish` compares the draft with the files you pass and refuses to sign when anything differs,
shows what it will sign, asks for confirmation, and signs exactly the draft's manifest. Download
the run's artifact from the CI run page to have something to compare against.

**Secret.** The PEM text goes into `INSTELLA_SIGNING_KEY` (and its password into
`INSTELLA_SIGNING_KEY_PASSWORD`). Anyone who can change a workflow that runs with these secrets can
read the key, so keep them in a protected environment (GitHub) and prefer a KMS.

### GitHub Actions

- **Environment.** The publish job runs in the `release` environment. In Settings > Environments,
  add required reviewers and limit the environment to `v*` tags. Nothing is signed until someone
  approves the run.
- **OIDC for the key services.** The publish job has `id-token: write` and signs in without a
  stored cloud secret:
  - *Azure*: an app registration with a federated credential for
    `repo:<owner>/<repo>:environment:release`; give it the "Key Vault Crypto User" role on the one
    key (or `sign` in an access policy). Variables `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`,
    `INSTELLA_KEY_VAULT_KEY_ID` (the key's URL) and `INSTELLA_SIGNING_PUBLIC_KEY`.
  - *AWS*: an IAM role whose trust policy accepts `token.actions.githubusercontent.com` with
    `sub` = `repo:<owner>/<repo>:environment:release`, allowed only `kms:Sign` on the key.
    Variables `AWS_ROLE_ARN`, `AWS_REGION`, `INSTELLA_KMS_KEY_ID` and `INSTELLA_SIGNING_PUBLIC_KEY`.
  - *Google Cloud*: workload identity federation for the repository and a service account with
    `roles/cloudkms.signer` on the key. Variables `GCP_WORKLOAD_IDENTITY_PROVIDER`,
    `GCP_SERVICE_ACCOUNT`, `GCP_KMS_LOCATION`, `GCP_KMS_KEYRING`, `GCP_KMS_KEY`,
    `GCP_KMS_KEY_VERSION` and `INSTELLA_SIGNING_PUBLIC_KEY`.
- **`INSTELLA_API_KEY`** is a secret: a server API key limited to the package, with upload
  permission.
- **Pin actions.** Replace each `uses: owner/action@v4` with the full commit SHA of the version you
  reviewed (`@<40-hex-sha> # v4.x.y`). A tag can be moved; a SHA cannot.
- **Never on pull requests.** The workflow runs on tags only; do not add `pull_request` triggers to a
  workflow that has signing access.

### Gitea Actions

The generated workflow uses the same steps with these differences:

- **Runners.** The build job asks for a runner labelled `windows` (Native AOT builds a Windows
  installer only on Windows), which needs the .NET 10 SDK, PowerShell 7 (`pwsh`) and the Visual
  Studio Build Tools C++ workload. The publish job runs on `ubuntu-latest`. Adjust the labels to your
  `act_runner` configuration.
- **No OIDC.** Gitea Actions does not issue OIDC tokens to workflows at the time of writing (check
  the documentation of your Gitea version), so the KMS variants sign in with credentials stored as
  secrets (`AZURE_CLIENT_SECRET`, `AWS_ACCESS_KEY_ID` / `AWS_SECRET_ACCESS_KEY`, or
  `GCP_CREDENTIALS_JSON`). Give those credentials permission to sign with the one key and nothing
  else, so a leak can only sign releases, which the key service logs. The runner image needs the
  key service's CLI.
- **No environments.** Protect the `v*` tags (Settings > Tags) so only maintainers can start the
  workflow; there is no approval step, so consider `draft` for a person to approve each release.
- **Artifacts.** The workflow uses `actions/upload-artifact@v3` and `download-artifact@v3`, which
  Gitea supports; use v4 only if your Gitea version supports it.

### Authenticode signing in CI

To sign the installers, pass your signing command to both installer builds with
`-p:InstellaSignCommand="..."`; the build signs the uninstaller stub and the finished installer
([distribution-and-signing.md](distribution-and-signing.md)). New code-signing certificates live in
hardware or a cloud signing service (Azure Trusted Signing, DigiCert KeyLocker, SSL.com eSigner),
which all offer a command-line signer and OIDC sign-in, the same pattern as the publisher key
above. The server never needs, and should never hold, a code-signing certificate.

## Before you publish an installer that replaces something

If users may already have the app installed some other way (a zip, another installer, an older
layout), add an install migration so the first Instella install replaces that copy instead of
installing next to it: see [migrations.md](migrations.md#replacing-an-existing-installation).
Migrations run when an installer runs, never during an in-app update, so ship them in the installer
users download.

## After publishing

- Deprecate a bad version in the admin UI: it stops being offered as an update, as `latest`, and on
  the download page, but stays downloadable by exact version.
- To stop a rollout without deprecating anything, cap the channel's latest at the previous version
  (see [Channels and "latest"](#channels-and-latest)).
- Rotate the publisher key with `instella upload --trusted-key` (see
  [signing-and-keys.md](signing-and-keys.md#rotation)); older installers keep offering only versions
  signed by keys they trust.
