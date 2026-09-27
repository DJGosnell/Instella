# Distribution and Signing

This document covers how an installer you distribute is protected: the footer hash every
installer checks at startup, and Authenticode signing of the installer and the stub it installs.
**Updates** are protected separately, by your publisher signature on each release; see
[signing-and-keys.md](signing-and-keys.md) and [security-model.md](security-model.md).

## The embedded footer hash

Every installer carries an 80-byte footer at the logical end of the file. It holds a SHA-256
over the range `[0, footerStart)`: the native binary, the appended manifest, and the payload
archive, with the two PE fields Authenticode rewrites read as zeros.
`PayloadFooterReader.VerifyIntegrity` recomputes it at startup, before any UI or network access,
and refuses to run on a mismatch (exit 12).

This catches download corruption, bit rot, and naive tampering. It is **not** a signature: it
proves the file is internally consistent, not that it came from you. An attacker who rewrites
the payload can recompute the footer hash. Proof of origin comes from Authenticode (below).

See [footer-format.md](footer-format.md) for the full layout.

## What Instella does not do

| Commonly expected | Status |
|---|---|
| `.sha256` sidecar files | Not produced. Publish the installer's hash yourself if you want one (below). |
| A `signing` section in `instella.json` | No. `instella.json` is generated from the fluent builder; Authenticode signing is the `InstellaSignCommand` build property. |
| macOS `codesign` / notarisation | Not implemented (macOS is experimental; see [roadmap.md](roadmap.md)). |
| Linux GPG signing | Not integrated. Detached signatures work, since they do not modify the file. |

## Windows: signing an installer

Set `InstellaSignCommand` in the installer project (or pass it with `-p:`), a command with
`{0}` where the file path goes:

```xml
<PropertyGroup>
  <InstellaSignCommand>signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /n "Example Corp" "{0}"</InstellaSignCommand>
</PropertyGroup>
```

`dotnet publish` then signs twice:

1. **The stub.** Before the payload is appended, the build copies the installer exe, signs the
   copy, and puts it in the payload as `.instella/instella.exe`. Installs stage that copy as the
   installed `instella.exe` — the file Windows Settings runs to uninstall, and the updater — so
   it carries a valid signature instead of the broken one a truncated signed installer would
   have. The payload grows by the size of one stub (a NativeAOT stub is 5–10 MB before
   compression).
2. **The installer**, after the payload is appended.

Without `InstellaSignCommand` the same flow runs unsigned. The command runs through `cmd.exe`
(Windows) or `/bin/sh`; a failure stops the build with `INSTELLA0204`. You can also sign the
finished installer yourself afterwards; the installed stub is then unsigned.

Do **not** sign the exe before Instella appends its payload: the build refuses an already
signed input with `INSTELLA0201`, because appending would invalidate that signature.
(Republishing over Instella's own signed output is fine: the old payload and signature are
removed first.)

Signing works because of three properties of footer format v3 (see
[footer-format.md](footer-format.md)):

1. **The hash excludes the fields signtool rewrites.** `signtool` changes the PE
   optional-header `CheckSum` and the `IMAGE_DIRECTORY_ENTRY_SECURITY` data-directory
   entry. Writer and reader both hash those bytes as zeros — the same exclusions the
   Authenticode spec defines for its own digest — so the footer hash is identical before
   and after signing.
2. **The file is 8-byte aligned.** The writer pads before the footer, so `signtool` adds
   no padding and the certificate table starts exactly at the footer's end.
3. **The reader is PE-aware.** `PayloadFooterReader.GetPayloadEndOffset` reads the
   security directory and treats the certificate table's offset as the logical end of
   the file, so the appended signature does not hide the footer. It also tolerates up to
   7 zero bytes of padding from tools that pad anyway.

`verify.ps1 -Stage Signing` publishes an installer with `InstellaSignCommand` and a throwaway
self-signed certificate, installs it `--silent`, and checks the installed `instella.exe` with
`Get-AuthenticodeSignature`. It proves that a signed installer accepts its own payload and
installs a well-formed stub, since signing changes the file after the footer hash is written.

## The exe icon

When the builder's `WithIcon(ImageSource.FromFile("app.ico"))` names a `.ico` file and the
project does not set `<ApplicationIcon>`, the build stamps that icon into the installer (and so
into the stub) before signing. Stamping needs a Windows build host (Win32 resource APIs); other
hosts warn with `INSTELLA0205`. `<ApplicationIcon>` always works and wins: it is compiled in, so
nothing is stamped. `WithIcon` also sets the shortcut, Installed Apps and wizard icons, which
do not depend on stamping.

## Verifying a downloaded installer by hand

There is no sidecar to check against, so publish the expected hash somewhere your users
trust (release notes, your website) and let them compare:

```powershell
# Windows
(Get-FileHash installer.exe -Algorithm SHA256).Hash
```

```bash
# Linux / macOS
sha256sum installer          # or: shasum -a 256 installer
```

Note this is the hash of the **whole file**, which is not the same value as the one
embedded in the footer — the footer hash deliberately excludes the footer itself.
