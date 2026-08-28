# macOS Installer + Auto-Update (Velopack)

Date: 2026-08-28
Status: implemented
Companion to: `2026-08-24-windows-installer-velopack-design.md` (the Windows lane this mirrors)

## Summary

Give macOS a real installer and update channel by extending the existing Velopack lane
to `osx-arm64`: `vpk pack` builds a `NovaTerminal.app` bundle from the raw NativeAOT
publish directory, a `.pkg` installer, full/delta nupkgs for an `osx` channel, and a
Portable zip of the `.app`. No Apple Developer certificate exists, so v1 ships **unsigned**
(the binaries themselves run: see "Signing" below), with the Gatekeeper path documented for
users and the exact signing diff documented for later.

## What exists today (before this change)

- `release.yml`'s `publish_aot` zips the raw osx-arm64 publish directory as
  `NovaTerminal-osx-arm64-<tag>.zip` — loose files, no `.app`, no installer.
- The app is macOS-ready at runtime: `VelopackApp.Build().Run()` runs in `Program.Main`
  on all OSes, `VelopackUpdateService` + `GithubSource` are cross-platform, chrome is
  traffic-light-aware, secrets use the Keychain, and `librusty_pty.dylib` /
  `librusty_ssh.dylib` are built on the mac runner.
- Bundled resources resolve via `AppContext.BaseDirectory` (fonts, `themes/**`), so
  everything from the publish directory must sit in `Contents/MacOS/` — which is exactly
  where `vpk pack` puts a plain `--packDir`.

## Facts verified against the vpk 1.2.0 source

These were read out of `velopack/velopack` (`develop`) rather than assumed, because they
are load-bearing for a two-channel release page:

1. **Asset naming** (`VelopackDefaults`/`DefaultName.GetUniqueAssetSuffix`): nupkgs,
   installers and portable zips get a `-{channel}` suffix **except** when the channel is
   the default `win` channel. So the win lane produces `NovaTerminalApp-<ver>-full.nupkg`
   while the osx lane produces `NovaTerminalApp-<ver>-osx-full.nupkg`,
   `NovaTerminalApp-<ver>-osx-Setup.pkg`, `NovaTerminalApp-<ver>-osx-Portable.zip` —
   no collisions inside one GitHub release.
2. **Channel resolution** (`RepositoryOptions.Channel` → `DefaultName.GetDefaultChannel`):
   `vpk download github` on a macOS runner targets the `osx` channel, on a Windows runner
   the `win` channel. Each lane's download/pack directory therefore only ever contains
   its own channel's packages even though the step body is shared.
3. **Signing default** (`OsxPackCommandRunner.CodeSign`): with no `--signAppIdentity`,
   vpk logs `Package will not be signed or notarized` and **packages everything
   unsigned**; with an identity + `--notaryProfile` it signs, notarizes, staples and
   `spctl`-assesses. There is no ad-hoc fallback inside vpk itself — none is needed (next
   point).
4. **Why unsigned still runs**: on arm64 the macOS linker applies ad-hoc signatures to
   the NativeAOT binary and cargo's `.dylib` outputs at build time, and
   SkiaSharp/HarfBuzz ship maintainer-signed dylibs. Gatekeeper's quarantine check is the
   only gate that fails, and "Open Anyway" clears it once.
5. **Bundle generation** (`OsxBundleCommandRunner`): the generated `Info.plist` sets
   CFBundleName/Executable/Identifier (`--bundleId`), version strings from
   `--packVersion`, `NSHighResolutionCapable`, and `CFBundleIconFile` from `--icon`
   (copied verbatim into `Contents/Resources` — it must be an `.icns`). It does **not**
   set `LSMinimumSystemVersion`.
6. **Updates on macOS**: packages cache to `~/Library/Caches/velopack/NovaTerminalApp`,
   the `.app` bundle is replaced in place; if the app is in `/Applications`, the updater
   elevates via an AppleScript admin prompt. App Sandbox is unsupported (not used here).

## Decisions

- **Velopack rather than hand-rolled `.app` + DMG**: the app already embeds the Velopack
  hooks and updater; vpk produces the bundle, installer and feed in one step the repo
  already trusts on Windows. A DMG would be bespoke scripting with no update benefit.
- **`--packId NovaTerminalApp`** (same as Windows): on mac it only names the update
  cache; the app installs as `/Applications/NovaTerminal.app`, so the Windows
  uninstall-deletes-config hazard cannot occur.
- **`--bundleId com.benyblack.NovaTerminal`**: vpk's default would be
  `com.benyblack.NovaTerminalApp` (derived from packAuthors+packId);
  `benyblack.NovaTerminal` matches the public identity (winget package id). The Keychain
  `kSecAttrService = "NovaTerminal"` is unrelated and unchanged.
- **Release assets** (user decision): the `.pkg`, plus the Portable `.app` zip renamed to
  the exact existing `NovaTerminal-osx-arm64-<tag>.zip` name — one obvious download, old
  links keep working, content upgrades from loose files to the bundle. The generic
  "Archive bundle"/"Upload release asset" steps skip `osx-arm64`.
- **Apple Silicon only** (user decision): no `osx-x64` lane. A second architecture would
  need explicit non-default channels and doubles mac CI time; follow-up if ever needed.
- **Unsigned v1, documented seam, nothing pre-wired**: matching the repo's established
  posture (the Windows lane's signing seam was likewise never wired). The exact flip-to-
  signed diff lives in `packaging/macos/README.md`; it is three vpk flags plus a keychain
  import step.

## Changes

- `packaging/macos/make-icns.sh` — `sips` + `iconutil` iconset generation from
  `nova_icon.png` (1024×1024 source; every size is a downscale). PNG stays the single
  source of truth; no `.icns` committed. The script forces `-s format png` because the
  source asset turned out to be JPEG data mislabeled with a `.png` extension (JFIF magic
  bytes) — without the explicit format, sips writes JPEG bytes into `.png`-named iconset
  files and `iconutil` fails with "Failed to generate ICNS".
- `release.yml` (`publish_aot`): osx-arm64-gated `vpk` install / icns / download /
  pack / upload steps, bash on the mac runner, mirroring the Windows lane's
  injection-safety conventions and its delta-expectation discipline (the gh-api count
  filters `-osx-full.nupkg`; re-run idempotency deletes `NovaTerminalApp-<ver>-osx-*.nupkg`).
  `vpk pack` runs after `rm -rf artifacts/publish/osx-arm64/NovaTerminal.dSYM` and passes
  `--exclude 'NovaTerminal\.dSYM'`: `StripSymbols=true` leaves a ~94 MB dSYM beside the
  binary (3× the binary's size), vpk's built-in `--exclude` default covers only Windows
  `.pdb` files, and exclusion alone still leaves empty dSYM directory skeletons in the
  ditto zip (verified: the nupkg dropped 46→32.5 MB once the content was excluded).
- `release.yml` Windows lane fix: the prior-full jq count now excludes `-osx-full.nupkg`
  assets — a bare `endswith("-full.nupkg")` would count both channels once mac releases
  exist and demand a win delta the win download never fetched.
- `ci.yml` (`aot_publish`, dispatch-only): same `vpk pack` at version `0.0.0-ci` with
  assertions, uploaded as the `macos-packaging-dryrun` artifact — the only way to verify
  packaging without cutting a release, since none of it runs on Windows/Linux dev boxes.
- `README.md`: macOS install section (pkg/zip, update behavior, Gatekeeper walkthrough).
- `packaging/macos/README.md`: asset table, dry-run instructions, the signing seam.

## Testing

- No unit-testable surface (workflow + shell + docs only) — consistent with how the
  Windows installer lane landed.
- The ci.yml dispatch lane is the verification gate: it asserts the Setup pkg, Portable
  zip, full nupkg and `releases.osx.json` all materialize with the expected names.
- First real tag will exercise the download/delta path (expected: full-only, asserted as
  correct for a first channel release).

## Open questions / follow-ups

- `LSMinimumSystemVersion` is absent from vpk's generated plist (see known-limitations
  note in `packaging/macos/README.md`); revisit with a `--plist` template if a floor ever
  needs enforcing.
- Signing + notarization once an Apple Developer Program membership exists (#91).
- A native macOS menu bar (`NativeMenu`) — a UX/polish concern, not packaging; the app
  draws its own chrome today.
- `osx-x64` lane and/or a universal binary (NativeAOT can't produce one directly; it
  would take `lipo` over two full publish trees — not worth it now).
