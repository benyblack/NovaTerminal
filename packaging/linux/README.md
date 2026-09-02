# Linux packaging

This folder holds the Linux packaging pieces used by the release workflow:

- `build-deb.sh` — builds `novaterminal_<debver>_<debarch>.deb` from a NativeAOT
  publish directory. `dpkg-deb` over a staged tree; nothing is compiled here.
- `smoke-test.sh` — installs and launches the artifacts in bare `ubuntu:22.04`
  containers. The release gate.
- `test-build-deb.sh` — unit-ish tests for `build-deb.sh` (version mapping, layout,
  dependency derivation, control fields), needing no real NovaTerminal build.
- `nova.desktop`, `nova.1` — the desktop entry and man page source.

Icons are derived at packaging time from `src/NovaTerminal.App/Assets/nova_icon.png`,
which stays the single cross-platform source of truth. No scaled PNGs are committed.

The AppImage and update feed are built by [Velopack](https://velopack.io) (`vpk pack`)
in `.github/workflows/release.yml`, mirroring the Windows and macOS lanes.

| Release asset | Produced by | Notes |
|---|---|---|
| `NovaTerminal-linux-<arch>-<tag>.AppImage` | `vpk pack` (renamed) | Portable, self-updating |
| `novaterminal_<debver>_<debarch>.deb` | `build-deb.sh` | System install; updates via your package manager |
| `NovaTerminal-linux-<arch>-<tag>.tar.gz` | `tar` in `release.yml` | Portable, no integration |
| `NovaTerminalApp-<ver>-linux-<arch>-full.nupkg` / `-delta.nupkg` | `vpk pack` | The update feed the in-app updater consumes |
| `releases.linux-<arch>.json` | `vpk pack` | Feed index resolved by `VelopackUpdateService` |

`release.yml`'s Linux release-publishing steps have not landed yet (tracked
separately), so this table states the design target, not something a tagged
GitHub release has produced. `linux_packaging_build` in `ci.yml` already runs
the same `build-deb.sh`/`vpk pack` invocations and asserts every filename
pattern above, but only as a CI dry run at a fixed `0.0.1-ci` version — see "Dry
run without cutting a release" below, not a real release.

`<arch>` is `x64` or `arm64`; `<debarch>` is the Debian spelling, `amd64` or
`arm64`. `<debver>` is the Debian-mapped version from `build-deb.sh
--print-debian-version <tag>` — **not** the tag verbatim; see below.

## Facts worth knowing

- **The glibc floor is 2.35**, pinned by building inside an `ubuntu:22.04`
  *container*. NativeAOT links against the build machine's glibc with no runtime
  fallback, so the build environment — not whichever runner happens to run it —
  is the minimum supported distro: Ubuntu 22.04+, Debian 12+, Fedora 36+, current
  rolling distros. Debian 11 and RHEL 8/9 are not supported.

  This used to be `runs-on: ubuntu-22.04` — the runner image itself *was* the
  build environment, so the runner choice was the mechanism. It isn't any more:
  `actions/runner-images#14254` deprecates the `ubuntu-22.04` runner image
  starting 2026-09-17 (fully unsupported 2027-04-17), which would have retired
  this floor on GitHub's schedule instead of a deliberate decision. The
  `linux_packaging_build` job now runs `runs-on: ubuntu-latest` with `container:
  ubuntu:22.04`, so the floor is pinned to an image tag this repo controls,
  independent of the runner lifecycle — bumping it is a one-line edit to
  `ci.yml`, not something that happens to us. Moving the *container image* off
  `ubuntu:22.04` would silently drop the floor; the runner label no longer
  carries that meaning.
- **The `.deb` filename's version is not the release tag.** `build-deb.sh`
  always appends a Debian revision (`-1`) and, for a prerelease tag, replaces
  the `-` before the prerelease label with `~`. dpkg reads the *last* `-` in a
  version as the revision separator, so a prerelease left as `-beta.1` would
  parse as upstream `0.5.0` revision `beta.1` and sort *above* the eventual
  `0.5.0-1` final release; `~` sorts before everything, so `~beta.1-1` correctly
  sorts below it. `v0.5.3` → `0.5.3-1`; `v0.5.0-beta.1` → `0.5.0~beta.1-1`.
  Verified directly against the script:

  ```
  $ packaging/linux/build-deb.sh --print-debian-version 0.5.3
  0.5.3-1
  $ packaging/linux/build-deb.sh --print-debian-version v0.5.0-beta.1
  0.5.0~beta.1-1
  ```

  The AppImage and tarball use the tag untransformed — the `.deb` is the only
  asset where the filename must be looked up rather than constructed from the
  tag.
- **Channels are `linux-x64` and `linux-arm64`, never the bare `linux`.** A Velopack
  feed is per-channel, not per-architecture, and both architectures publish into one
  GitHub release — a shared `linux` channel would offer arm64 clients an x64 package.
  The names must stay in sync with `VelopackUpdateService.ResolveExplicitChannel`.
- **The channel is baked into the package at pack time.** `vpk pack --channel
  linux-x64` writes `<channel>`/`<rid>` into the `.nuspec` inside the `.nupkg` and
  into the manifest filenames, which is what actually keeps the two architectures'
  feeds separate. The app-side `ExplicitChannel` (`ResolveExplicitChannel`) is
  defense-in-depth against a future `vpk pack` invocation that omits `--channel` —
  not the primary mechanism.
- **Two dependency-derivation mechanisms, because one alone ships a package that
  installs and then can't open a window.** `build-deb.sh` runs `ldd` over every ELF
  in the bundle, then adds a hand-maintained `DLOPEN_DEPENDS` list for the libraries
  Avalonia's X11 backend and SkiaSharp's font lookup `dlopen()` at runtime instead of
  linking. Measured on a real linux-x64 AOT publish on `ubuntu-22.04`, the derived
  `Depends:` was:

  ```
  libc6 (>= 2.35), libicu74 | libicu72 | libicu71 | libicu70, libbrotli1,
  libfontconfig1, libfreetype6, libpng16-16, libuuid1, libx11-6, libxrandr2,
  libxi6, libxcursor1, libxext6, libice6, libsm6, libgl1
  ```

  Of the nine X11/fontconfig packages `DLOPEN_DEPENDS` names, only `libfontconfig1`
  was *also* found by `ldd` (it's linked directly into `libSkiaSharp.so`) — the other
  eight are invisible to `ldd` entirely. `ldd` alone would have produced a package
  that installs cleanly on a clean machine and then fails at first launch.
- **The derived `Depends:` is distro-specific by design, not a bug.** The identical
  publish built inside an Ubuntu 24.04 base yielded `libpng16-16t64` (the time64
  transition) instead of `libpng16-16`. Deriving on the same distro that sets the
  glibc floor (`ubuntu-22.04`) is correct — deriving on `ubuntu-latest` would
  silently drift the two apart.
- **ICU is an alternatives list** (`libicu74 | libicu72 | libicu71 | libicu70`)
  because the package name is version-pinned per distro and `InvariantGlobalization`
  is unset, so the app genuinely needs ICU. Hard-depending on one version would
  refuse to install across most of the supported distro range.
- **`WM_CLASS` is `"nova", "NovaTerminal"`** on the real Avalonia binary — verified
  against a genuine GUI launch, not inferred from source — so `StartupWMClass=NovaTerminal`
  in `nova.desktop` is correct.
- **`libSkiaSharp.so` embeds freetype, libjpeg and libpng, and the AOT binary embeds
  zlib.** These are inherent to a self-contained bundle and are accepted via
  `usr/share/lintian/overrides/novaterminal`, one line per tag *and* exact path —
  deliberately never a wildcard, so a genuinely new embedded library pulled in by a
  future SkiaSharp or AOT bump still trips the lintian gate instead of being waved
  through.
- **User data is never touched** by updates or uninstall. It lives at
  `~/.local/share/NovaTerminal` via `AppPaths`, independent of install method.
- **No maintainer scripts.** `desktop-file-utils` and `hicolor-icon-theme` ship dpkg
  triggers that refresh the desktop and icon caches, so the package needs no
  `postinst`/`prerm`.

## Known traps

- **AppImage needs FUSE.** Ubuntu 22.04+ ships no `libfuse2`, so a stock AppImage
  fails with a confusing FUSE error. Either `sudo apt install libfuse2`, or run it
  as `./NovaTerminal-*.AppImage --appimage-extract-and-run`. `smoke-test.sh` tests
  both paths, one per container.
- **The AppImage self-updates in place**, so it must live somewhere the user can
  write. Parked in `/opt` or `/usr/local/bin` it cannot update itself. `~/Applications`
  is the right home.
- **A `.deb` install does not auto-update.** It is not a Velopack install, so
  `IUpdateService.IsSupported` is false and the in-app updater stays silent by
  design. Update through your package manager or reinstall a newer `.deb`.
- **NovaTerminal is not registered as `x-terminal-emulator`.** That is deliberate:
  callers of `x-terminal-emulator` pass `-e <command>`, which the app does not
  implement, so registering would make "Open in Terminal"-style callers silently
  discard the command they meant to run. To opt in anyway, knowing `-e` will not
  work:

  ```sh
  sudo update-alternatives --install /usr/bin/x-terminal-emulator \
      x-terminal-emulator /usr/bin/nova 40
  ```

## Dry run without cutting a release

Three jobs in `.github/workflows/ci.yml` cover this without cutting a release:

- **`linux_packaging_detect`** (runner, no container) — the change-detection
  gate. Always signals "run" on `push` and `workflow_dispatch`; on a
  `pull_request` it only does when the diff against the PR's merge base touches
  `packaging/linux/**`, a workflow file, `LICENSE`, or the app icon (its
  `Detect packaging changes` step).
- **`linux_packaging_build`** (runner, `container: ubuntu:22.04` — the glibc-floor
  pin described above) — builds the Rust natives, publishes AOT, runs
  `build-deb.sh` and `vpk pack` at version `0.0.1-ci`, asserts every asset name,
  and uploads the `linux-packaging-dryrun` artifact.
- **`linux_packaging_smoke`** (runner, no container) — downloads that artifact
  and runs `test-build-deb.sh` and `smoke-test.sh` against it. It has to run
  outside the container: both scripts start their own bare `docker run`
  containers as their entire test premise, and a `container:` job has no Docker
  daemon or socket to do that with.

Trigger the lane manually with `gh workflow run ci.yml`, or by touching a file
under `packaging/linux/` in a PR.

Locally, everything but `vpk pack` runs in Docker. `test-build-deb.sh` needs
`dpkg-deb`, `dpkg-query`, `file`, `ldd`, `strip` (from `binutils`), an ImageMagick
`magick`/`convert`, plus `fc-match` and `xdpyinfo` as donor ELFs that link real
`libfontconfig1`/`libx11-6` for the dependency-derivation checks:

```sh
docker run --rm -v "$PWD:/w" -w /w ubuntu:22.04 bash -c \
  'apt-get update -qq && apt-get install -y -qq file imagemagick fontconfig x11-utils binutils >/dev/null &&
   packaging/linux/test-build-deb.sh'
```

`smoke-test.sh <artifact-dir>` needs Docker with a real `novaterminal_*.deb` (and
optionally an `.AppImage`) already built — see `.github/workflows/ci.yml` for how the
dry-run job assembles that directory before calling it.

No CI run has exercised any of this on this branch yet; every claim above about the
derived `Depends:`, the lintian result, and the smoke gate passing was verified with
local containerized runs, not a GitHub Actions run.
