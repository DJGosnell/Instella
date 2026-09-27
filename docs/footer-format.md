# Instella Payload Footer Format (v3)

The installer stub carries its payload (manifest, config, archive) appended after the native binary. An 80-byte footer at the logical end of the file describes the layout.

## Footer Layout (80 bytes)

| Offset | Size | Field | Description |
|--------|------|-------|-------------|
| 0 | 8 | `manifest_offset` | Absolute byte offset where manifest JSON starts |
| 8 | 4 | `manifest_length` | Byte length of manifest JSON |
| 12 | 4 | `config_length` | Byte length of config JSON (0 if none) |
| 16 | 8 | `archive_offset` | Absolute byte offset where ZIP archive starts (0 if none) |
| 24 | 8 | `archive_length` | Byte length of ZIP archive (0 if none) |
| 32 | 32 | `payload_sha256` | SHA-256 of bytes `[0, footer_start)`, PE signing fields read as zeros (below) |
| 64 | 4 | `format_version` | `3` |
| 68 | 4 | `flags` | bit 0: has archive; bit 1: has config; other bits reserved, must be 0 |
| 72 | 8 | magic | ASCII `INSTELLA` |

All integer fields are little-endian. Config offset is derived: `manifest_offset + manifest_length`. The flags must agree with the lengths.

The file layout is `[stub][manifest][config][archive][zero padding][footer]`. The writer pads so that the finished file length is a multiple of 8.

## Installer Types

- **Offline** (`archive_length > 0`): Self-contained. The archive is a ZIP of all application files. This is the only type the build produces.
- **Lite** (`config_length > 0`, `archive_length == 0`) — **reserved.** An installer that downloads files from the server URL in the config. `PayloadAppender.AppendLitePayload` and the reader handle the shape, but no build path calls it yet.

## Integrity Check

Every installer self-verifies at startup and exits 12 on mismatch. This catches download corruption, bit rot, and naive tampering; it is not a signature.

The hash covers `[0, footer_start)` with two PE fields treated as zeros: the optional-header `CheckSum` (4 bytes at optional header + 64) and the `IMAGE_DIRECTORY_ENTRY_SECURITY` data-directory entry (8 bytes at optional header + 128 for PE32, + 144 for PE32+). These are the fields Authenticode excludes from its own digest and the only header bytes `signtool` rewrites, so the hash is identical before and after signing. Non-PE files are hashed as they are.

## Locating the Footer

`payload_end` is the certificate table offset when the PE security directory is non-empty, otherwise the file length. The footer is `[payload_end − 80, payload_end)`. Because the writer 8-aligns the file, `signtool` appends its certificate table directly after the footer. As a fallback for tools that pad anyway, the reader skips at most 7 zero bytes before `payload_end`.

## Versioning

- `format_version` greater than 3, or any unknown flag bit: the installer was built by a newer Instella. The reader refuses it (exit 12 with that message).
- v2 (72-byte) footers are not supported. No current installer produces one.

See [compatibility.md](compatibility.md) for the policy across all versioned formats.

## Platform Notes

- **Windows**: Signing with `signtool sign /fd SHA256` after the build is supported. `verify.ps1 -Stage Signing` tests it.
- **macOS**: The payload is not appended to the Mach-O binary. It lives at `Contents/Resources/payload.instella` inside the `.app` bundle, with the same footer at its end.
- **Linux**: Footer is at the end of the file. GPG signing produces a detached `.sig` file; the binary itself is not modified.

## Source of Truth

- Writer: [`PayloadAppender.cs`](../src/Instella.Installer.Build/Tasks/PayloadAppender.cs)
- Reader and hash: [`PayloadFooterReader.cs`](../src/Instella.Core/Internal/PayloadFooterReader.cs), [`PeLayout.cs`](../src/Instella.Core/Internal/PeLayout.cs)
