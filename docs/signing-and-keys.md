# Publisher Signing Keys

Every update an Instella installation accepts must be a release manifest signed with a
**publisher key** that the installation trusts. This document covers creating those keys, getting
the public half into your installer, signing releases, registering keys on the server, and what to
do when a key has to change or is lost.

For the overall trust model see [security-model.md](security-model.md). Publisher keys are not
Authenticode certificates; see [Publisher keys versus Authenticode](#publisher-keys-versus-authenticode).

## Key facts

- Algorithm: ECDSA P-256 with SHA-256. Signatures use the IEEE P1363 encoding (64 bytes).
- Private key: a PKCS#8 PEM file, optionally encrypted with a password. It never leaves your
  machine or CI runner; the server never sees it.
- Public key: base64 of the SubjectPublicKeyInfo DER bytes. This is the string you pass to
  `WithPublisherKey(...)` and paste into the server's admin UI.
- Key id: the first 16 hex characters of SHA-256 over the SubjectPublicKeyInfo DER bytes. It
  appears in CLI output, in signed releases and in the admin UI.

## Generating a key

```bash
instella keys generate --out publisher.key.pem
```

Options:

| Option | Meaning |
|---|---|
| `--out`, `-o <file>` | Required. Where to write the private key. |
| `--password-env <VAR>` | Name of an environment variable holding a password. When given, the key is written encrypted. The variable must be set and non-empty, or the command fails. |
| `--force` | Overwrite an existing file. Without it, an existing file is an error. |

With a password the PEM is an `ENCRYPTED PRIVATE KEY` (AES-256-CBC, PBKDF2 with SHA-256 and
600,000 iterations). Without one it is a plain `PRIVATE KEY` and the command says "(unencrypted)".
The file is readable only by you from the moment it exists: on Linux and macOS it is created with
mode `0600`, and on Windows with a protected access list that grants only your account and SYSTEM
(nothing is inherited from the folder). When the file lands inside a git work tree, the command
warns: keep private keys out of source control, or at least list them in `.gitignore`
(`instella init` does so for the key it suggests).

Example with a password:

```powershell
$env:QN_KEY_PASSWORD = '<a long passphrase>'
instella keys generate --out publisher.key.pem --password-env QN_KEY_PASSWORD
```

The command prints the key id, the public key, and the line to add to your installer:

```
Private key written to C:\keys\publisher.key.pem (encrypted).
Key id:     3f9a0c1e5b7d2a64
Public key: MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE...

Add it to the installer:
    .WithPublisherKey("MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE...")

Keep the private key secret (a CI secret store, not the repository).
Generate a second, offline backup key now and add it with a second WithPublisherKey(...):
if this key is ever lost with no trusted successor, installations can no longer be updated.
```

To print the public key and key id of an existing private key:

```bash
instella keys show --key publisher.key.pem [--password-env VAR]
```

An encrypted key needs `--password-env`; without it the command fails with "The signing key is
encrypted; a password is required."

## Two keys: an online key and an offline backup key

Generate **two** keys before your first release: an online key that signs every release (a CI
secret, a KMS key, or a key on your machine), and a backup key that you store offline (encrypted,
in a password manager or a safe) and that **never goes to CI**. Put both public keys into the
installer:

```bash
instella keys generate --out publisher.key.pem --password-env QN_KEY_PASSWORD
instella keys generate --out publisher-backup.key.pem --password-env QN_BACKUP_PASSWORD
```

Installations trust the keys of the installer they were installed with. A key that was not in
the installer cannot later be added by an ordinary update (see
[Rotation](#rotation)). The backup key is what lets you recover from losing or leaking the online
key without asking every user to download a new installer.

This is the recommended default for a solo maintainer who wants fully automated releases: the
online key as a CI secret (`instella ci init --signing secret`), the backup key offline. If the CI
key leaks, one release signed with the backup key and `--trusted-key` listing only the keys you
keep makes installations stop trusting the leaked key, without reinstalling (see
[Revocation](#revocation)). Set it up **before the first release**: an installation trusts only
the keys compiled into the installer it came from. For oversight on top, set the package's
[release approval](publishing.md#publishing-tiers-and-release-approval) on the server to `Delayed`
or `Required`.

## Putting the public key into the installer

Call `WithPublisherKey` once per key in the installer's `Program.cs`:

```csharp
return await InstellaInstaller.Create()
    .WithApp("QuickNotes", "com.example.quicknotes", new Version(1, 3, 0))
    .WithServer("https://updates.example.com")
    .WithPublisherKey("MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE...")   // primary
    .WithPublisherKey("MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE...")   // offline backup
    // ...
    .Build()
    .RunAsync(args);
```

- `WithPublisherKey` throws immediately if the string is not a base64 ECDSA P-256 public key.
  Adding the same key twice has no effect.
- `Build()` throws when `WithServer(...)` is set and no publisher key was given. The only way
  around that is `AllowUnsignedUpdates()`, which turns off update verification entirely and is for
  development only.
- `instella init --publisher-key <base64>` writes the `WithPublisherKey(...)` line into a new
  project; without the option the template contains a placeholder that you must replace.

The keys are public; committing them to source control is fine. At publish time they are written
into the build manifest (`publisherKeys`). At install time the installer copies them into the
installed manifest (`.instella-manifest.json`, `trustedKeys`). Updates are verified against that
installed list only.

## Signing releases at upload time

`instella upload` builds a release manifest from exactly the files it uploads (path, size,
SHA-256), signs it with your private key, and sends the signed bytes when it completes the upload
session. The server stores and serves those bytes unmodified.

The key is taken from, in order:

1. `--signing-key <value>`, or
2. the `INSTELLA_SIGNING_KEY` environment variable.

Either value may be the PEM text itself (anything containing `-----BEGIN`) or a path to a PEM
file. The password for an encrypted key comes from `INSTELLA_SIGNING_KEY_PASSWORD`. There is no
command-line option for the password. PEM text passed to `--signing-key` works but prints a warning:
other users on the machine can read a process's command line. Pass a path, or put the PEM text in
`INSTELLA_SIGNING_KEY`.

```bash
export INSTELLA_API_KEY=...                        # server API key (upload permission)
export INSTELLA_SIGNING_KEY=./publisher.key.pem    # or the PEM text from a secret store
export INSTELLA_SIGNING_KEY_PASSWORD=...
instella upload --server https://updates.example.com \
    --package com.example.quicknotes --version 1.3.0 \
    --path ./QuickNotes/bin/Release/net10.0/win-x64/publish
```

The command signs before it uploads anything and prints `Signed: yes, key <key id>`; a signing
failure (a failing `--sign-command`, or a signature that does not match the public key) exits
**4** without sending a file. Without a key, `upload` refuses to
run unless you pass `--unsigned` (development only; installations built without
`AllowUnsignedUpdates()` reject unsigned releases, and the server rejects them when the package has
registered keys).

The release records the app id (`--package`), version, OS, architecture and channel. Upload the
same files you shipped in the installer for that version: the lite installer and repair verify
installed files against the release of the installed version.

### Signing with a key that never leaves a KMS or HSM (`--sign-command`)

A CI pipeline should be able to *use* the publisher key without *having* it. Keep the key in a
cloud key service (Azure Key Vault, AWS KMS, Google Cloud KMS) or on a hardware token, create it as
an **ECDSA P-256** key that cannot be exported, and let `instella upload` ask it for a signature:

```bash
instella upload ... \
    --signing-public-key <base64 SubjectPublicKeyInfo of that key> \
    --sign-command "<command that signs the digest>"
```

`instella upload` builds the release manifest, computes the SHA-256 digest of what is signed, and
runs the command (through `cmd /c` on Windows, `/bin/sh -c` elsewhere) with these placeholders
replaced. The file placeholders are inserted already quoted for that shell (the temporary folder can
contain spaces), so do not add quotes around them:

| Placeholder | Value |
|---|---|
| `{digest-hex}` | the digest as 64 lower-case hex characters |
| `{digest-base64}` / `{digest-base64url}` | the digest as base64 / base64url |
| `{digest-file}` | a file holding the 32 raw digest bytes, inserted as a quoted path |
| `{message-file}` | a file holding the whole signed message, for tools that hash it themselves (SHA-256), inserted as a quoted path |

The same two files are also in the environment of the command, as `INSTELLA_DIGEST_FILE` and
`INSTELLA_MESSAGE_FILE`. Use the variables when the path is part of a larger argument, such as AWS's
`fileb://…`: a quoted placeholder there would put the quotes inside the argument.

The command prints the signature on standard output: base64, base64url or hex; raw (r‖s, 64 bytes)
or DER; or a JSON object whose `result`, `signature` or `Signature` property holds it. Instella checks
the signature against `--signing-public-key` before it uploads anything, so a wrong key or a signer
that signed the wrong bytes fails the upload instead of publishing a release nobody can install.
The environment variables `INSTELLA_SIGN_COMMAND` and `INSTELLA_SIGNING_PUBLIC_KEY` work as defaults.
`--sign-command` and `--signing-key` cannot be combined.

Put the same public key into the installer with `.WithPublisherKey("<base64>")`.

**Azure Key Vault** (key type EC, curve P-256; the pipeline signs in with OIDC, see the CI guide):

```bash
# Public key once: az keyvault key download --id <key id> --encoding DER --file pub.der; base64 -w0 pub.der
--sign-command "az keyvault key sign --id https://<vault>.vault.azure.net/keys/instella-publisher --algorithm ES256 --digest {digest-base64} --query result -o tsv"
```

**AWS KMS** (key spec `ECC_NIST_P256`, usage `SIGN_VERIFY`):

```bash
# Public key once: aws kms get-public-key --key-id alias/instella-publisher --query PublicKey --output text
--sign-command 'aws kms sign --key-id alias/instella-publisher --message-type DIGEST --signing-algorithm ECDSA_SHA_256 --message "fileb://$INSTELLA_DIGEST_FILE" --query Signature --output text'
```

**Google Cloud KMS** (algorithm `EC_SIGN_P256_SHA256`; POSIX shell):

```bash
--sign-command 'gcloud kms asymmetric-sign --location global --keyring instella --key publisher --version 1 --digest-algorithm sha256 --input-file "$INSTELLA_MESSAGE_FILE" --signature-file "$INSTELLA_MESSAGE_FILE.sig" && base64 -w0 "$INSTELLA_MESSAGE_FILE.sig"'
```

The command times out after two minutes. Its standard error is shown when it fails.

### Hand-signed releases: CI uploads a draft, a person signs (`upload --draft` + `instella publish`)

When no pipeline may sign at all (the Hand-signed tier, `instella ci init --signing manual`), CI
uploads without any key and a person who holds the key signs. "Draft" is the name of such an
unsigned upload:

```bash
# CI (no signing key anywhere):
instella upload --server https://updates.example.com --package com.example.quicknotes \
    --version 1.3.0 --path ./publish --installer ./QuickNotes-WebSetup-1.3.0.exe --draft

# A maintainer, on their own machine, with the files they reviewed (for example the CI artifact):
instella publish --server https://updates.example.com --package com.example.quicknotes \
    --version 1.3.0 --os windows --arch x64 --path ./publish \
    --installer ./QuickNotes-WebSetup-1.3.0.exe --signing-key ./publisher.key.pem
```

A draft is invisible to installers and apps (no update, no listing, no download, not on the
download page) until it is published. `instella publish` downloads the draft's release manifest,
compares it with `--path` and the installers you pass, and refuses to sign when anything differs;
without `--path` it can only sign the list the server holds, and it says so. It signs exactly the
stored bytes (with `--signing-key` or `--sign-command`), and the server accepts the signature only
for those bytes and, when the package has registered publisher keys, only from one of them. Without
registered keys the server cannot check the signature (it holds no public key to check it with);
installations still do.

After signing, the release follows the package's
[release approval](publishing.md#publishing-tiers-and-release-approval): with `Automatic` it is
published at once; with `Delayed` the delay starts at signing; with `Required` it waits for Approve,
and `publish` says so.

### CI secrets

If the key has to be a file (`--signing-key`), store the PEM text (or a path to a file your
pipeline materialises from a secret) in `INSTELLA_SIGNING_KEY`, and the password in
`INSTELLA_SIGNING_KEY_PASSWORD`, using your CI system's secret store; on GitHub, in a protected
environment that only release jobs can use when your plan has environments, otherwise as repository
secrets with the `v*` tags protected. Prefer the environment variables over
`--signing-key <PEM text>`: a command-line argument can show up in process listings and build logs.
Keep the backup key out of CI entirely: it is what makes a leaked CI key recoverable. A key in a KMS
(`--sign-command`) goes further: the pipeline can use it only while it runs, cannot copy it, and
every signature is in the key service's audit log.

## Registering publisher keys on the server

The server can refuse uploads that are not signed by a key you registered. This catches CI
misconfiguration (a wrong key, or a forgotten `--unsigned`) at upload time. It is defence in depth;
installations never rely on it.

1. Sign in to the admin UI and open **Packages**.
2. Select the package. Its properties pane has a **Publisher keys** section.
3. Paste the public key (from `instella keys show`), optionally add a label, and choose
   **Add key**. The server rejects a value that is not an ECDSA P-256 public key, and a key already
   registered for the package.

While a package has no registered keys, both signed and unsigned uploads are accepted. Once at
least one key is registered, an upload whose release is unsigned, or does not verify against a
registered key, is rejected. Register your backup key too, so switching to it does not require a
server change. **Remove** takes a key off the list; releases already published are not affected.

Registered keys also back [release approval](publishing.md#publishing-tiers-and-release-approval):

- Release approval `Delayed` or `Required` needs at least one registered key, so every held release
  has a signature the server has verified.
- Approving a held release, and the automatic publish at the end of a delay, check its signature
  again against the keys registered **now**. Removing a leaked key therefore stops its pending
  releases: they can only be rejected.
- The last key cannot be removed while release approval is `Delayed` or `Required`, or while a
  release is pending.

Adding and removing keys is recorded in the security log (Settings > Security) with the admin who
did it.

## Rotation

There are two ways a set of trusted keys can change on an installation:

1. **A signed release that carries `trustedKeys`.** Pass `--trusted-key <public key>` to
   `instella upload`, once per key (the base64 value `instella keys show` prints). After that
   release is installed, the installation trusts exactly the listed keys. The new list is accepted
   only because the release verified against a key the installation already trusted. The list
   *replaces* the old one, so include every key you want to keep, including your backup.
2. **Running an installer.** A first install, and an installer for a *newer* version than the one
   installed, write the installer's compiled-in keys to `trustedKeys`. A repair or downgrade (an
   installer whose version is not newer) keeps the installation's recorded list, so an old
   installer cannot bring back a key that a release revoked.

A planned rotation from key A to key B:

```bash
instella keys generate --out b.key.pem
# Release N, signed by A, tells installations to trust A and B.
instella upload ... --signing-key a.key.pem --trusted-key <A public key> --trusted-key <B public key>
# Release N+1, signed by B, drops A.
instella upload ... --signing-key b.key.pem --trusted-key <B public key> --trusted-key <backup public key>
```

Build installers from then on with `WithPublisherKey` for the keys you now trust, so a fresh
install starts from the current list. An installation that skips release N (for example, it was
offline) is offered N+1 directly and refuses it, because it does not trust B yet. Keep release N
available, or keep signing with A, until your installations have taken it.

Without rotation releases:

- **Switching from the primary key to the backup key.** Every installation already trusts both,
  so start signing uploads with the backup key.
- **Retiring an old key.** Stop signing with it and remove it from the server's registered keys.
  Installations still trust it until a release rotates it out or the user runs a newer installer
  without it.

## Revocation

There is no revocation list and no online key check. A client trusts whatever is in its
installed manifest's `trustedKeys`.

If a private key leaks:

1. Stop using it. Sign every further release with your backup key.
2. Remove the leaked key from the package's registered keys on the server, so the server refuses
   uploads signed with it. This matters if the attacker also obtains an API key for your server.
3. Publish a release signed by the backup key (or by the leaked key, if nothing else is trusted)
   with `--trusted-key` listing only the keys you keep. Installations that take it stop trusting
   the leaked key.
4. Build new installers that no longer include the leaked key, so fresh installs never trust it.

An attacker holding the leaked key can deliver an update only by controlling what an installation
downloads, which means controlling your server (or its storage). Installations that have not yet
taken the rotation release remain exposed to the leaked key for as long as that is possible. This is inherent to
any design where keys are distributed offline inside the installer.

**Installers already downloaded.** A web installer with `WithNewerVersionPrompt()` or
`WithVersionSelection()` downloads and runs another version's installer. It checks that installer
against the signed release (publisher key, app, platform, version and channel), and, when the running
installer is Authenticode-signed, also requires the downloaded one to pass `WinVerifyTrust` and carry
the **same signer subject**. So a leaked publisher key alone is not enough to hand such an installer
over to an attacker's: they would also need your code-signing certificate, which is harder to steal
and can be revoked through its CA. The subject is compared, not the thumbprint, because thumbprints
change at every renewal. Revocation is checked from the local cache only, so an offline machine never
waits minutes for a CRL. A mismatch refuses the handoff with exit code 12 and deletes the file.

The residual risk is a leaked publisher key together with **unsigned** installers: an installer that
is not Authenticode-signed skips the signer check (it has no signer to compare with). The playbook:

1. Rotate the publisher key (above).
2. Delete the releases signed with the compromised key on the server.
3. Keep Authenticode signing (`InstellaSignCommand`) on for every installer you publish.

## Key loss

If you lose a key but still hold another key that installations trust (your backup), switch to it
as described under Rotation and generate a new backup.

If you lose **every** key that an installation trusts, that installation can never accept another
update: no release you can produce will verify. Users must download and run a new installer
(signed with Authenticode, containing your new keys). A newer installer upgrades the existing
installation in place and replaces its `trustedKeys`; no uninstall is needed.

This is why `instella keys generate` tells you to create an offline backup key and add it with a
second `WithPublisherKey(...)` from the first release on.

## Publisher keys versus Authenticode

Instella uses two independent kinds of signature:

| | Publisher key | Authenticode |
|---|---|---|
| Protects | Updates (release manifests) | The installer `.exe` and the installed stub `instella.exe` |
| Who checks it | Instella (SDK and updater), against the installed `trustedKeys` | Windows, SmartScreen and the user; Instella does not check it |
| Key material | ECDSA P-256 key you generate with `instella keys generate` | A code-signing certificate from a certificate authority |
| Where it is applied | `instella upload` | `dotnet publish`, through the `InstellaSignCommand` MSBuild property |

Authenticode proves the installer came from you, and the installer is what delivers your publisher
keys to the machine. Sign installers with Authenticode and sign releases with a publisher key; one
does not replace the other. See [distribution-and-signing.md](distribution-and-signing.md) for
`InstellaSignCommand` and how the installed stub gets its own signature.
