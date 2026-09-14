# Arch / AUR packaging

This folder produces **`novaterminal-bin`**, an AUR package that repackages the
published `linux-x64` release tarball. Nothing is compiled here.

- `build-arch.sh` — generates `PKGBUILD` + `.SRCINFO`, the two files an AUR
  repository contains. Also prints the pieces the gates below consume
  (`--print-pkgver`, `--print-arch-map`, `--print-depends`).
- `smoke-test.sh` — builds, installs and launches the package in three Arch
  containers. The gate.
- `test-build-arch.sh` — tests for `build-arch.sh` that need no Arch host, no
  Docker and no NovaTerminal build.

There is no committed `PKGBUILD`. It is generated per release, because the two
things that change every time — the version and the tarball's `sha256sum` — are
exactly the two things a human editing a checked-in file gets wrong.

| Produced | By | Notes |
|---|---|---|
| `PKGBUILD` | `build-arch.sh` | Pushed to the AUR; never edited in place |
| `.SRCINFO` | `makepkg --printsrcinfo`, via `build-arch.sh` | The AUR rejects a push without it |
| `PKGBUILD-<tag>`, `SRCINFO-<tag>` | `release_linux` in `release.yml` | The same two files, published as release assets |
| `novaterminal-bin-<pkgver>-1-x86_64.pkg.tar.zst` | `makepkg`, on the user's machine | Not published anywhere |

## Installing on Arch today

The AUR is closed to new accounts (see below), so `novaterminal-bin` is not on the
AUR yet. Every release publishes the two files an AUR repository would contain, so
an Arch user can build the same package from them directly:

```sh
TAG=v0.8.0
mkdir novaterminal-bin && cd novaterminal-bin
curl -LO "https://github.com/benyblack/NovaTerminal/releases/download/$TAG/PKGBUILD-$TAG"
mv "PKGBUILD-$TAG" PKGBUILD
makepkg -si
```

`makepkg` fetches the release tarball named in the PKGBUILD and verifies it against
the pinned `sha256sum` — which `release_linux` asserts is the sum of the tarball it
published in the same run — then installs through pacman with the dependencies
resolved. Updating means repeating this at the new tag; there is no `pacman -Syu`
integration without a repository, and the in-app updater is inert for package
installs by design.

`SRCINFO-<tag>` is published alongside it. It is metadata for the AUR, not something
`makepkg` needs, and is there so the AUR push is a copy rather than a regeneration.

## Why `-bin`, and why the tarball

A source-built `novaterminal` would need SDK 10.0.400 (pinned in `global.json`,
which is not what Arch's `dotnet-sdk` tracks), the Rust natives, and a NuGet
restore inside `build()` — and makepkg builds are meant to be network-free after
`source=()`. The published tarball is the artifact `README.md` already offers as
the portable install, so the AUR package hands users the same bytes with system
integration on top.

The `.deb` is not reused: it is a Debian container format, and unpacking one to
re-pack it would inherit `build-deb.sh`'s Debian-specific `Depends:` derivation
(see below for why that list is wrong for Arch).

## Facts worth knowing

- **The dependency list is gated against `build-deb.sh`, not written by hand.**
  `ARCH_DLOPEN_MAP` in `build-arch.sh` maps each soname in `build-deb.sh`'s
  `DLOPEN_LIBS` table to an Arch package, and the generator **fails** if the two
  disagree in either direction — an unmapped soname, or a mapping for a soname
  that is no longer declared. Adding a runtime dependency and gating it on Arch
  are therefore the same edit, which is the property `smoke-test.sh` in
  `packaging/linux/` was reworked to get.

  This matters more on Arch than on Debian: `namcap` derives dependencies from
  ELF links, and **eight of the eleven** dlopen'd libraries are invisible to
  `ldd`. A namcap-derived `depends=()` would be missing all eight.

- **The Arch `depends=()` is deliberately shorter than the Debian `Depends:`.**
  `build-deb.sh` derives via `ldd`, which walks the full *transitive* closure and
  so surfaces `libbrotli1`, `libfreetype6`, `libpng16-16` and `libuuid1` as if
  they were ours. On Arch those arrive through `fontconfig` and `glib2`; listing
  them again is redundant and namcap flags it. Verified against the running
  process on Arch (2026-09-14): every one of them was mapped, none of them by us.

- **The version mapping is NOT the Debian one, and copying it would be a bug.**
  dpkg gives `~` a sorts-before-everything meaning; pacman's `vercmp` is
  rpmvercmp-derived and has no such rule. Verified on pacman 7.1.0:

  ```
  $ vercmp 0.8.0~rc.1 0.8.0   ->  1    # prerelease sorts ABOVE the release
  $ vercmp 0.8.0.rc.1 0.8.0   ->  1    # same
  $ vercmp 0.8.0rc1   0.8.0   -> -1    # correct
  ```

  So `v0.8.0-rc.1` maps to `0.8.0rc1` — label glued on, separators stripped.
  `test-build-arch.sh` asserts both the mapping and the resulting order, and
  asserts that the `~` form still sorts wrong, so if pacman ever gains tilde
  semantics the tests say so instead of quietly going stale.

- **`_tag` is emitted verbatim, never rebuilt from `pkgver`.** The mapping is
  lossy and one-way: nothing turns `0.8.0rc1` back into `v0.8.0-rc.1`. A PKGBUILD
  building its URLs from `"v$pkgver"` would 404 on every prerelease.

- **The AUR repo carries no binary blobs.** `nova.desktop`, `nova.1`, the icon and
  `LICENSE` are fetched from the tag over HTTPS with pinned `sha256sums`, so
  `packaging/linux/` stays the single source of truth for both Linux packages and
  the 662 KB icon is not re-committed per release. Incidentally that icon is a
  JPEG with a `.png` extension — ImageMagick sniffs content, so both lanes work,
  but it is mislabeled on disk.

- **`--source-ref <git-ref>` is what keeps those four sums honest.** With it the
  sums are read from that ref's blobs via `git show`, so they always describe the
  bytes the `raw.githubusercontent` URLs serve. Without it the working tree is
  used, which is right for a release built at its own tag. CI passes the release
  tag, because a PR that edits `nova.desktop` would otherwise pin the branch's sum
  against the tag's URL and fail an integrity check unrelated to the change.

- **The in-app updater is inert**, exactly as for the `.deb`:
  `VelopackUpdateService.IsSupported => _manager.IsInstalled`
  (`src/NovaTerminal.App/Update/VelopackUpdateService.cs`), and a pacman-installed
  tree is not a Velopack install. Update through pacman.

- **User data is never touched** by install, upgrade or removal. It lives at
  `~/.local/share/NovaTerminal` via `AppPaths`, independent of install method.

- **No `.install` scriptlet.** `desktop-file-utils` and `hicolor-icon-theme` ship
  pacman hooks that refresh the desktop and icon caches, the same way their dpkg
  triggers do. That reasoning holds *because* `hicolor-icon-theme` is in
  `depends=()` — it owns the hierarchy the icons install into.

- **`options=('!strip' '!debug')`.** The payload is a NativeAOT binary;
  `build-deb.sh` strips the bundled native libraries but pointedly not the AOT
  binary, and makepkg's blanket strip makes no such distinction, so `package()`
  does the selective strip by hand. The tarball's AOT binary arrives stripped
  already; its four `.so` files do not.

- **x86_64 only.** The release publishes a `linux-arm64` tarball, but no arm64
  build of this project has been verified end to end. Add `aarch64` with a
  `source_aarch64`/`sha256sums_aarch64` pair once something has launched on it.

## Known traps

- **The archlinux container images set `NoExtract`.** `/etc/pacman.conf` in the
  official images drops `/usr/share/man`, `/usr/share/doc` and most locales at
  unpack time. Leaving those lines in place makes the man-page assertion fail on a
  *correctly built* package — the Arch twin of `ubuntu:22.04`'s
  `/etc/dpkg/dpkg.cfg.d/excludes`, which `packaging/linux/smoke-test.sh` removes
  for the same reason. `smoke-test.sh` strips them in both test containers. This
  is load-bearing, not a leftover; its first run failed on exactly this.

- **Each smoke-test phase is a script inside `bash -c '...'`.** One single quote
  anywhere in that body — an apostrophe in a comment, a `sed '...'` expression —
  closes the string early. `bash -n` on the outer file does **not** catch it: an
  even number of stray quotes leaves the file balanced while the container runs
  something else. `test-build-arch.sh` extracts each body, parses it standalone,
  and rejects any single quote. Write `the app own icons`, not `the app's icons`,
  inside those blocks — the Debian smoke test drops apostrophes for this reason
  too.

- **namcap's `W:` lines are advisory; its `E:` lines are not.** Eleven warnings
  say "Dependency included, but may not be needed" for exactly the dlopen'd
  libraries namcap structurally cannot see; failing on those would mean deleting
  the dependencies that make the app work. Three more are upstream facts — SkiaSharp
  and HarfBuzzSharp ship prebuilt without FULL RELRO and still link the
  `libpthread`/`libdl` that glibc 2.34 folded into libc — and one notes that
  `libgcc` is satisfied indirectly through `gcc-libs`, which is deliberate:
  `gcc-libs` installs on both sides of Arch's recent `libgcc`/`libstdc++` split.
  The `E:` gate exists because the first fully green run printed
  `E: Dependency hicolor-icon-theme detected and not included` and still reported
  every phase ok — a real defect surviving a passing smoke test.

- **The makepkg container must build as the *caller's* uid.** `/work` is a bind
  mount, so every uid the container writes is the uid on the host, and `mktemp -d`
  is mode 700 — a container-local `builder` (uid 1000) leaves the caller locked out
  of its own temp directory. The symptom is misleading: the `ls` that follows fails,
  `set -e` kills the script silently, and the only output is
  `rm: cannot remove '/tmp/tmp.XXXX': Operation not permitted` from the exit trap.
  **Running the smoke test under `sudo` hides this entirely**, because root reads and
  removes regardless of ownership — so a green local run under `sudo` is not evidence
  the uid mapping is right. It failed on the first CI run for exactly this reason.

- **`ldd` warns about the bundled `.so` files.** "you do not have execution
  permission" is expected: `package()` normalises the bundle to 0644 because the
  loader only mmaps a shared library. Do not make them executable to silence it.

- **`/usr/bin/nova` collides with `python-novaclient`** (OpenStack), which ships
  its own `/usr/bin/nova`. pacman refuses to install over it, so an affected user
  gets a clear file-conflict error rather than a silently shadowed command.

## Publishing to the AUR

> **BLOCKED, not deferred, as of 2026-09-14: the AUR is not accepting new
> accounts.** After three waves of supply-chain attacks (roughly 1,500 packages
> compromised), Arch disabled registration in June 2026, reopened it on 13 July
> with hardening, disabled package *adoption* on 31 July after a further
> RAT-injection wave, and on 11 August restored writes with adoption behind
> maintainer approval — while **new account registration stays closed "for now"**,
> so only existing verified maintainers can push. No restoration date has been
> announced.
>
> Everything below is correct and ready; it cannot be executed until registration
> reopens. Do not work around this by asking a third party to submit the package:
> handing an unrelated account the right to push arbitrary PKGBUILDs for this
> software is the exact shape of the attack that closed registration in the first
> place.
>
> Sources: [aur-general, registration reopened
> 13 Jul](https://lists.archlinux.org/archives/list/aur-general@lists.archlinux.org/message/TT3OCFFNM6SBMUBKIVTHTKA6UZJNMXIJ/),
> [LWN, adoption disabled 31 Jul](https://lwn.net/Articles/1086489/).

Manual, deliberately: the automation is worth wiring once the package shape has
survived a real version bump. Requires an AUR account with an SSH key registered,
and `novaterminal-bin` registered to it.

```sh
# 1. Generate at the tag. --source-ref makes the four auxiliary sums describe the
#    tag's blobs rather than whatever the working tree holds.
TAG=v0.8.0
curl -LO "https://github.com/benyblack/NovaTerminal/releases/download/$TAG/NovaTerminal-linux-x64-$TAG.tar.gz"
packaging/arch/build-arch.sh "$TAG" /tmp/aur \
  --tarball "NovaTerminal-linux-x64-$TAG.tar.gz" \
  --source-ref "$TAG"

# 2. Gate it. Do not skip this - it is the only thing that has ever caught a
#    defect in this lane.
packaging/arch/smoke-test.sh /tmp/aur

# 3. Push. The AUR repo contains PKGBUILD and .SRCINFO and nothing else.
git clone ssh://aur@aur.archlinux.org/novaterminal-bin.git /tmp/aur-repo
cp /tmp/aur/PKGBUILD /tmp/aur/.SRCINFO /tmp/aur-repo/
cd /tmp/aur-repo && git add PKGBUILD .SRCINFO && git commit -m "Update to $TAG" && git push
```

Prereleases are not published. The generator maps them correctly and
`test-build-arch.sh` asserts the ordering, but publishing rcs to the AUR churns
users for no benefit; `ci.yml`'s lane resolves the newest **stable** release for
the same reason.

## Dry run without cutting a release

`arch_packaging` in `.github/workflows/ci.yml` runs the whole lane on every PR
that touches `packaging/arch/**` or `packaging/linux/**` (one change-detection job
gates both, because `build-arch.sh` reads `build-deb.sh`'s table). It resolves the
newest published stable release rather than the linux dry run's `0.0.1-ci` build:
the AUR package's input *is* a published release, and the dry run produces no
tarball.

Locally, on any machine with bash:

```sh
packaging/arch/test-build-arch.sh
```

On a machine with Docker, the full gate:

```sh
packaging/arch/build-arch.sh v0.8.0 /tmp/aur --tarball <tarball> --source-ref v0.8.0
packaging/arch/smoke-test.sh /tmp/aur
```

`test-build-arch.sh` skips its prerelease-ordering assertions where `vercmp` is
absent (it ships with pacman), which is why CI runs it inside `archlinux:base`
rather than on the runner — on a bare runner those assertions would pass while
checking nothing.
