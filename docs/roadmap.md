# Roadmap

What is deliberately **not** in Instella yet, so it is not mistaken for a bug. Some of it may come
before 1.0, some after; until 1.0 a release may change formats or the public API when a feature needs
it (see [compatibility.md](compatibility.md)).

## Platforms

- **Linux and macOS installer UI.** Instella ships the Win32 wizard only; GTK and Cocoa hosts exist
  for `--preview` but the install wizard is Windows-only, so other platforms install with
  `--silent`.
- **macOS `.app` bundles, codesigning and notarization.** Payloads can already live at
  `Contents/Resources/payload.instella`, but nothing builds, signs or notarizes a bundle.
- **macOS start-menu (Launchpad) shortcuts.** Disabled for now: a symlink in place of a bundle could
  overwrite the executable. A bundle-based replacement comes with `.app` support.
- **Linux desktop integration polish:** system-wide installs to `/opt` with PATH and desktop
  entries, and AppArmor/SELinux considerations.

## Updates

- **Stub refresh through updates.** The installed stub (`instella.exe`) is replaced only when a
  user runs a newer installer, never by an auto-update. Shipping the stub inside the release
  would let updates refresh it.
- **Prompt-free machine-wide updates.** Updating a machine-wide install prompts for elevation
  every time; a service or scheduled-task helper could remove the prompt.
- **Multi-version patches.** Patches are generated only between consecutive versions; clients
  more than one version behind download changed files in full.
