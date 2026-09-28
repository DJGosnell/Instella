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

**"Latest"** on a channel is the highest version number that is published (not a draft, and not
pending [release approval](#publishing-tiers-and-release-approval)) and not deprecated. The release date plays no part, so a hotfix for an older line (`1.2.5` after `1.3.0`)
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
`--no-environment` is for GitHub repositories without deployment environments (see
[GitHub Actions](#github-actions)).

### Publishing tiers and release approval

Two independent settings decide how a tag becomes a release:

| Setting | Where it lives | Values | Protects against |
|---|---|---|---|
| **Signer** | the CI workflow and the installer build | `secret`, `kms-azure`, `kms-aws`, `kms-gcp`, `manual` | a compromised server, API key or storage |
| **Release approval** | the package on the server | `Automatic`, `Delayed`, `Required` | a compromised CI or account |

The signer decides what installations accept: they verify every release against the publisher
keys compiled into their installer, whatever the server says. Release approval can only **hold a
correctly signed release back**; it never changes what installations check, so a compromised
server cannot use it to weaken verification.

Together they give three tiers:

| Tier | Signer | Release approval | What happens after the tag |
|---|---|---|---|
| **Automatic** | a CI signer | `Automatic` or `Delayed` | The release goes live at once, or after the delay unless someone rejects it. No manual step. |
| **Approved** | a CI signer | `Required` | The release is uploaded signed and waits until someone approves it. |
| **Hand-signed** | `manual` | any | CI uploads an unsigned draft; a person signs it with `instella publish`. After signing, release approval applies (with `Required` it still waits for Approve). |

**Setting release approval.** In the admin UI, open **Packages**, select the package, and use the
**Release approval** section: `Automatic` (the default), `Delayed` with a delay from 1 hour to 7
days, or `Required`. `Delayed` and `Required` need at least one
[registered publisher key](signing-and-keys.md#registering-publisher-keys-on-the-server), so every
held release has a signature the server has verified. Every change is written to the security log.
A change never publishes anything by itself: releases already pending keep their terms, except that
switching to `Required` cancels running delays, so those releases wait for approval.

**Pending releases.** A held release is invisible to installers, apps, the download page and
`latest` until it is published. `instella upload` succeeds (exit 0) and prints that the release is
pending, and when it publishes automatically. Patches are generated at upload, so approval takes
effect immediately. Approve or reject in the admin UI (the **Awaiting a decision** list in the
package pane, or the build's **Approve** and **Reject** buttons) or from a person's machine:

```bash
instella pending --server https://updates.example.com --package com.example.quicknotes
instella approve --server https://updates.example.com --package com.example.quicknotes \
    --version 1.3.0 --os windows --arch x64 --path <the run's app artifact>
instella reject  --server https://updates.example.com --package com.example.quicknotes \
    --version 1.3.0 --os windows --arch x64 --reason "unexpected tag"
```

These need an API key with the **Approve releases** permission. A key cannot have both Upload and
Approve releases, and an API key never approves its own upload: keep the approve key on a person's
machine, never in CI (a CI holding both keys would approve its own releases). `approve` shows the
release, compares it with `--path`, `--installer` and `--offline-installer` when given, asks for
confirmation (`--yes` skips it), and approves exactly the manifest it showed; if the release was
replaced in the meantime, the server refuses. Approval also re-checks the signature against the
package's registered keys, so a release signed by a key you have since removed can only be
rejected. **Reject** deletes the build (and the version, if it has no other build); the same
version number can then be uploaded again, and it is held again.

**Delayed.** The server stores when each delayed release goes live and publishes it within about
30 seconds of that time. If the server was down, it publishes overdue releases right after it
starts: a restart can make a release late, never early.

**Patches and late approvals.** A release's patch is built when it is uploaded, from the highest
version published at that moment. If you approve an older pending version after a newer one was
published, the newer version has no patch from it, so installations on the approved version
download the changed files in full for that one update.

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
it can *use* the key only while a release run is going, cannot copy the key, and leaves a record.
Whichever signer you choose, the server's [release approval](#publishing-tiers-and-release-approval)
can add a person (or a delay) before installations see the release.

| `--signing` | The key lives | CI can | Choose it when |
|---|---|---|---|
| `secret` | in a CI secret (PEM text), with an offline backup key that never goes to CI | read the key | the default for a solo maintainer; no key service needed |
| `kms-azure`, `kms-aws`, `kms-gcp` | in a cloud key service, created non-exportable | ask for signatures while the run's short-lived identity is valid | you want the key to be impossible to copy |
| `manual` | only with a maintainer | upload an unsigned draft that nobody sees until a maintainer signs it | no pipeline may sign at all |

`draft` is the old name of `manual`. It still works in this release with a warning, and will be
removed in the next one.

**Why automatic signing keeps you safe.** Installations trust only the publisher keys compiled
into their installer, so a compromised server, API key or storage still cannot deliver anything you
did not sign. A person signing each release by hand adds protection only against a compromised CI or
repository account, and even then only checks that the draft matches the CI output, not that the
code is safe. Release approval `Delayed` or `Required` covers that case without a manual signing step.

**Secret with an offline backup key (the solo-maintainer default).** The PEM text goes into
`INSTELLA_SIGNING_KEY` (and its password into `INSTELLA_SIGNING_KEY_PASSWORD`). Before the first
release, also generate a backup key that never goes to CI, keep it offline, and compile **both**
public keys into the installer (`WithPublisherKey` twice). If the CI key ever leaks, one release
signed with the backup key and `--trusted-key` makes installations stop trusting the leaked key,
without reinstalling ([signing-and-keys.md](signing-and-keys.md#two-keys-an-online-key-and-an-offline-backup-key)).
This must be in place before the first release: installations trust only the keys compiled into
the installer they came from. Anyone who can change a workflow that runs with the secret can read
the key, so protect the `v*` tags, limit who can push workflows, and on GitHub use a protected
environment when your plan has one.

**KMS.** Create an ECDSA P-256 signing key in the key service, marked non-exportable, and allow
the pipeline's identity to *sign* with it, nothing else. `instella upload --sign-command` sends
only the SHA-256 digest of the manifest to the key service and checks the returned signature
against `--signing-public-key` before it uploads anything. Put the same public key in the installer
with `.WithPublisherKey(...)`, together with an offline backup key. The commands for each service
are in [signing-and-keys.md](signing-and-keys.md#signing-with-a-key-that-never-leaves-a-kms-or-hsm---sign-command).

**Manual (Hand-signed).** CI uploads with `--draft`: files and installers are stored, but
installers, apps and the download page do not see the version. A maintainer then runs, on their own
machine:

```bash
instella publish --server https://updates.example.com --package com.example.quicknotes \
    --version 1.3.0 --os windows --arch x64 \
    --path <the run's app artifact> --installer <...WebSetup...> --offline-installer <...Setup...> \
    --signing-key publisher.key.pem          # or --sign-command with a hardware token or KMS
```

`publish` compares the draft with the files you pass and refuses to sign when anything differs,
shows what it will sign, asks for confirmation, and signs exactly the draft's manifest. Download
the run's artifact from the CI run page to have something to compare against. Once signed, the
release follows the package's release approval: with `Required`, `publish` prints that it is
pending and it still waits for Approve.

### GitHub Actions

- **Environment (optional).** By default the publish job runs in the `release` environment. In
  Settings > Environments, add required reviewers and limit the environment to `v*` tags; nothing is
  signed until someone approves the run. Repositories without deployment environments (private
  repositories on the free plan) use `instella ci init --no-environment`: the workflow has no
  `environment:` line, and release approval on the server (`Delayed` or `Required`) is where a
  person can stop a release. Protect the `v*` tags (Settings > Rules) either way.
- **OIDC for the key services.** The publish job has `id-token: write` and signs in without a
  stored cloud secret. The trust policy matches the job's OIDC subject: with the environment,
  `repo:<owner>/<repo>:environment:release`; without one, the tag ref,
  `repo:<owner>/<repo>:ref:refs/tags/v*`.
  - *Azure*: an app registration with a federated credential for that subject (for the tag ref,
    a flexible federated credential whose expression matches `refs/tags/v*`; a plain credential names
    a single tag); give it the "Key Vault Crypto User" role on the one key (or `sign` in an access
    policy). Variables `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `INSTELLA_KEY_VAULT_KEY_ID` (the key's
    URL) and `INSTELLA_SIGNING_PUBLIC_KEY`.
  - *AWS*: an IAM role whose trust policy accepts `token.actions.githubusercontent.com` with
    `sub` equal to the environment subject, or `StringLike` the tag-ref subject, allowed only
    `kms:Sign` on the key. Variables `AWS_ROLE_ARN`, `AWS_REGION`, `INSTELLA_KMS_KEY_ID` and
    `INSTELLA_SIGNING_PUBLIC_KEY`.
  - *Google Cloud*: workload identity federation for the repository (without an environment, add an
    attribute condition that the ref starts with `refs/tags/v`) and a service account with
    `roles/cloudkms.signer` on the key. Variables `GCP_WORKLOAD_IDENTITY_PROVIDER`,
    `GCP_SERVICE_ACCOUNT`, `GCP_KMS_LOCATION`, `GCP_KMS_KEYRING`, `GCP_KMS_KEY`,
    `GCP_KMS_KEY_VERSION` and `INSTELLA_SIGNING_PUBLIC_KEY`.
- **`INSTELLA_API_KEY`** is a secret: a server API key limited to the package, with upload
  permission. It can never also have the Approve releases permission (the server refuses the
  combination).
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
  workflow. Gitea has no approval step of its own, so use release approval `Delayed` or `Required`
  on the server for a person to see each release before installations do.
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
- A release held by release approval is not published yet: approve or reject it in the admin UI
  (the package's **Awaiting a decision** list) or with `instella approve` / `instella reject` (see
  [Publishing tiers and release approval](#publishing-tiers-and-release-approval)). A rejected
  release is deleted, and the same version number can be uploaded again.
