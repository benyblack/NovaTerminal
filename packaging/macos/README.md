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
the same `vpk pack` (version `0.0.1-ci`) plus assertions, uploading
`macos-packaging-dryrun` as a workflow artifact. Use it to verify bundle structure, the
pkg, and asset names before tagging — and, once the `MAC_*` secrets are set, the whole
sign/notarize/staple path too. None of this is runnable from a Windows or Linux dev box.

## Signing + notarization (secret-gated)

Signing is wired into both `vpk pack` lanes (the release lane in `release.yml` and the
dry run above) but **gated on the `MAC_CERT_P12_BASE64` repo secret**:

- Secret set → `.github/actions/setup-mac-signing` imports the Developer ID identities
  into a build keychain and stores the notarytool profile, and `vpk pack` runs with
  `--signAppIdentity`/`--signInstallIdentity`/`--notaryProfile`/`--keychain`. vpk then
  signs the app bundle and dylibs (deep signing), signs the pkg, submits for
  notarization, staples the ticket, and runs `spctl` assessments — verified in vpk's
  `OsxPackCommandRunner`/`CodeSign`.
- Secret unset → both lanes behave exactly as they did before: `vpk pack` warns
  "Package will not be signed or notarized" and packages everything unsigned. The app
  still runs, because on arm64 `ld` ad-hoc-signs the NativeAOT binary and the Rust
  dylibs at build time, and the SkiaSharp/HarfBuzz dylibs ship signed by their
  maintainers.

The gate is all-or-nothing: the action fails fast (naming the missing secret) if
`MAC_CERT_P12_BASE64` is set but any other required `MAC_*` secret is missing, the
imported keychain lacks one of the two identities, or Apple rejects the notarytool
credentials — rather than letting `vpk pack` fail minutes later inside
codesign/productbuild.

### One-time setup

Prerequisites: a **paid Apple Developer Program membership** (a free Apple ID cannot
create Developer ID certificates or submit for notarization), and an app-specific
password for notarytool (appleid.apple.com → Sign-In and Security → App-Specific
Passwords).

**1. Create the two certificates.** On developer.apple.com → Certificates, Identifiers
& Profiles → Certificates → **+**, create one **Developer ID Application** and one
**Developer ID Installer** certificate. Each needs a CSR:

- *With a Mac*: Keychain Access → Certificate Assistant → Request a Certificate from a
  Certificate Authority… (keeps the key in the login keychain), then double-click each
  downloaded `.cer` to pair it with its key.
- *Without a Mac* — openssl works everywhere and Git Bash on Windows ships it. The
  `MSYS_NO_PATHCONV=1` prefix stops Git Bash from rewriting the leading-slash `-subj`
  into a Windows path; it is inert on macOS/Linux:

  ```bash
  MSYS_NO_PATHCONV=1 openssl req -new -newkey rsa:2048 -nodes -keyout app.key -out app.csr -subj "/CN=NovaTerminal"
  MSYS_NO_PATHCONV=1 openssl req -new -newkey rsa:2048 -nodes -keyout installer.key -out installer.csr -subj "/CN=NovaTerminal"
  ```

  Upload `app.csr` for the Developer ID Application certificate, `installer.csr` for
  the Developer ID Installer certificate, and download both `.cer` files. Keep the
  `.key` files: they are the certificate private keys (re-exporting or moving the
  identities later requires them; losing them means revoking and reissuing).

**2. Produce the p12(s).** The identities may travel as one combined p12 (Mac route)
or as two files (openssl route — a p12 can only carry one private key); the CI action
imports one or two accordingly.

- *Mac route (one file)*: in Keychain Access select both new certificate+key pairs and
  export them as a single `.p12`.
- *openssl route (two files)* — Apple serves the intermediate as DER, and notarization
  requires the signature to carry it, so fold it in via `-certfile`:

  ```bash
  curl -LO https://www.apple.com/certificateauthority/DeveloperIDG2CA.cer
  openssl x509 -inform der -in DeveloperIDG2CA.cer -out DeveloperIDG2CA.pem
  # The algorithm profile matters: OpenSSL 3's defaults (AES-256 PBES2 + SHA-256
  # MAC) fail to import via macOS's `security` tool with a bogus "wrong password"
  # error (OpenRadar FB8988319). 3DES + SHA-1 is the compatible set.
  openssl pkcs12 -export -out app.p12 -inkey app.key \
    -in developerID_application.cer -certfile DeveloperIDG2CA.pem -name "Developer ID Application" \
    -certpbe PBE-SHA1-3DES -keypbe PBE-SHA1-3DES -macalg sha1
  openssl pkcs12 -export -out installer.p12 -inkey installer.key \
    -in developerID_installer.cer -certfile DeveloperIDG2CA.pem -name "Developer ID Installer" \
    -certpbe PBE-SHA1-3DES -keypbe PBE-SHA1-3DES -macalg sha1
  base64 -w 0 app.p12 > app.p12.b64        # macOS: base64 -i app.p12 -o app.p12.b64
  base64 -w 0 installer.p12 > installer.p12.b64
  ```

  The exact identity strings for the secrets below are the certificate CNs — read them
  verbatim with
  `openssl x509 -inform der -in developerID_application.cer -noout -subject`, which
  prints e.g. `subject=CN = Developer ID Application: LEGAL NAME (TEAMID), …`. That
  parenthesized 10-character value is also the team ID.

**3. Set the repo secrets:**

| Secret | Value |
|---|---|
| `MAC_CERT_P12_BASE64` | base64 of the `.p12` with the Developer ID Application identity (the combined Mac export holds both) |
| `MAC_INSTALLER_CERT_P12_BASE64` | *optional* — base64 of a separate `.p12` with the Developer ID Installer identity (the openssl route); omit on the Mac route |
| `MAC_CERT_PASSWORD` | the `.p12` export password (use the same password for both files on the openssl route) |
| `MAC_SIGN_APP_IDENTITY` | `Developer ID Application: <legal name> (TEAMID)` — the CN, verbatim |
| `MAC_SIGN_INSTALL_IDENTITY` | `Developer ID Installer: <legal name> (TEAMID)` — the CN, verbatim |
| `MAC_NOTARY_PROFILE` | any profile name, e.g. `nova-notary` |
| `MAC_APPLE_ID` | the Apple ID |
| `MAC_TEAM_ID` | the 10-character team ID (Membership page) |
| `MAC_APP_SPECIFIC_PASSWORD` | the app-specific password |

4. Dispatch the `AOT Publish` workflow once before tagging: the mac lane performs the
   same signed pack at version `0.0.1-ci`, so a broken certificate, identity string, or
   notarytool credential fails there instead of at the next release.

The first-launch Gatekeeper flow documented in the main `README.md` (System Settings →
Privacy & Security → "Open Anyway" on macOS 15+, or `xattr -cr` in Terminal) applies to
unsigned builds; drop that section from `README.md` when the first signed release ships.

### Known limitations

- vpk's generated `Info.plist` sets no `LSMinimumSystemVersion`; .NET 10's own macOS floor
  governs what actually runs. If an explicit floor is ever needed, pass a fully specified
  plist via `--plist` (mutually exclusive with `--bundleId`) — note the plist must then
  carry the version strings per release itself.
- The bundle carries ~600 KB of `NovaTerminal.*.pdb` files beside the binary (the IL
  project symbols the AOT publish emits). vpk's documented `.pdb` default exclusion does
  not strip these from the macOS bundle contents; harmless, kept rather than spending a
  release-lane iteration on it.
- Signing cannot be back-dated onto already-published releases; users who installed an
  unsigned build keep their Gatekeeper approval and continue updating normally (updates
  written by the running app never pass through Gatekeeper).
