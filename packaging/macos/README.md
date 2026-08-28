# macOS packaging

This folder holds the macOS packaging pieces used by the release workflow:

- `make-icns.sh` — derives `nova_icon.icns` from `src/NovaTerminal.App/Assets/nova_icon.png`
  at packaging time (`sips` + `iconutil`). The PNG stays the single source of truth across
  platforms; no `.icns` binary is committed.

The actual bundling is done by [Velopack](https://velopack.io) (`vpk pack`) in
`.github/workflows/release.yml`, mirroring the Windows lane:

| Release asset | Produced by | Notes |
|---|---|---|
| `NovaTerminal-Setup-osx-arm64-<tag>.pkg` | `vpk pack` (renamed from `*-osx-Setup.pkg`) | Standard macOS installer; offers /Applications or ~/Applications |
| `NovaTerminal-osx-arm64-<tag>.zip` | `vpk pack` (renamed from `*-osx-Portable.zip`) | Same file name the raw loose-files zip always used; now contains the `NovaTerminal.app` bundle |
| `NovaTerminalApp-<ver>-osx-full.nupkg` / `-osx-delta.nupkg` | `vpk pack` | The osx update channel consumed by the in-app updater |
| `releases.osx.json` | `vpk pack` | The osx feed index (`GithubSource` in `VelopackUpdateService` resolves it) |

Facts worth knowing (all verified against the vpk 1.2.0 source, see
`docs/superpowers/specs/2026-08-28-macos-installer-velopack-design.md`):

- The default macOS channel is `osx`, and its assets carry an `-osx` suffix
  (`NovaTerminalApp-<ver>-osx-full.nupkg`). Only the *win* default channel omits the
  suffix, so the two lanes never collide inside one GitHub release.
- `vpk download github` resolves its channel from the runner's OS, so each lane only ever
  downloads its own channel's prior full package for delta generation.
- The app bundle installs as `/Applications/NovaTerminal.app`; Velopack's update cache
  lives at `~/Library/Caches/velopack/NovaTerminalApp`. User data stays where it always
  was (`~/.local/share/NovaTerminal` via `AppPaths`) and is never touched by updates or
  uninstall.

## Dry run without cutting a release

The `AOT Publish` job in `.github/workflows/ci.yml` is `workflow_dispatch`-only and runs
the same `vpk pack` (version `0.0.0-ci`) plus assertions, uploading
`macos-packaging-dryrun` as a workflow artifact. Use it to verify bundle structure, the
pkg, and asset names before tagging. None of this is runnable from a Windows or Linux
dev box.

## Current state: unsigned (the "signing seam")

No Apple Developer Program membership exists today, so releases ship **unsigned and
un-notarized**. `vpk pack` is invoked without signing options; it logs
`Package will not be signed or notarized` and packages everything anyway. The app still
runs: on arm64, `ld` ad-hoc-signs the NativeAOT binary and the Rust dylibs at build time,
and the SkiaSharp/HarfBuzz dylibs ship signed by their maintainers.

The first-launch Gatekeeper flow for users is documented in the main `README.md`
(System Settings → Privacy & Security → "Open Anyway" on macOS 15+, or
`xattr -cr /Applications/NovaTerminal.app` in Terminal).

### Turning signing + notarization on later

This is deliberately **not** pre-wired with secret-gated workflow steps: an untestable
dead path in a release pipeline is worse than a documented diff. When a Developer ID
certificate exists, the change is exactly this:

1. Create a "Developer ID Application" and a "Developer ID Installer" certificate
   (appleid.apple.com → Certificates), export both as one `.p12`.
2. Store repo secrets: `MAC_CERT_P12_BASE64` (base64 of the p12), `MAC_CERT_PASSWORD`,
   `MAC_SIGN_APP_IDENTITY` (e.g. `Developer ID Application: Beny Black (TEAMID)`),
   `MAC_SIGN_INSTALL_IDENTITY` (e.g. `Developer ID Installer: Beny Black (TEAMID)`),
   `MAC_NOTARY_PROFILE` (a notarytool profile name), plus `MAC_APPLE_ID`, `MAC_TEAM_ID`,
   `MAC_APP_SPECIFIC_PASSWORD`.
3. In the osx-arm64 leg of `publish_aot` in `release.yml`, add a step before `vpk pack`
   that imports the keychain and stores the notarytool profile:

   ```bash
   cert_path=$(mktemp -d)/cert.p12
   echo "$MAC_CERT_P12_BASE64" | base64 --decode > "$cert_path"
   security create-keychain -p "$MAC_CERT_PASSWORD" build.keychain
   security default-keychain -s build.keychain
   security unlock-keychain -p "$MAC_CERT_PASSWORD" build.keychain
   security import "$cert_path" -k build.keychain -P "$MAC_CERT_PASSWORD" -T /usr/bin/codesign
   security set-key-partition-list -S apple-tool:,apple: -k "$MAC_CERT_PASSWORD" build.keychain
   xcrun notarytool store-credentials "$MAC_NOTARY_PROFILE" \
     --apple-id "$MAC_APPLE_ID" --team-id "$MAC_TEAM_ID" --password "$MAC_APP_SPECIFIC_PASSWORD" \
     --keychain build.keychain
   ```

4. Add these flags to `vpk pack`:

   ```
   --signAppIdentity "$MAC_SIGN_APP_IDENTITY" \
   --signInstallIdentity "$MAC_SIGN_INSTALL_IDENTITY" \
   --notaryProfile "$MAC_NOTARY_PROFILE" \
   --keychain "$HOME/Library/Keychains/build.keychain"
   ```

vpk then signs the app bundle and dylibs (deep signing), signs the pkg, submits for
notarization, staples the ticket, and runs `spctl` assessments — verified in vpk's
`OsxPackCommandRunner`/`CodeSign`. Nothing else in the pipeline changes: same asset
names, same feed, updater-agnostic.

### Known limitations

- vpk's generated `Info.plist` sets no `LSMinimumSystemVersion`; .NET 10's own macOS floor
  governs what actually runs. If an explicit floor is ever needed, pass a fully specified
  plist via `--plist` (mutually exclusive with `--bundleId`) — note the plist must then
  carry the version strings per release itself.
- Signing cannot be back-dated onto already-published releases; users who installed an
  unsigned build keep their Gatekeeper approval and continue updating normally (updates
  written by the running app never pass through Gatekeeper).
