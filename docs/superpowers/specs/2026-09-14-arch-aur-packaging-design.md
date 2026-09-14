# Arch Linux publishing: a gated `novaterminal-bin` AUR package

Date: 2026-09-14
Status: implemented; gated locally on real Arch, never yet run in GitHub Actions
Companion to: `2026-09-02-linux-packaging-design.md` (the lane this extends)
Closes the AUR leg of #385 ("Additional package formats: Flatpak/Flathub, AUR, RPM, Snap")

## Summary

Arch users currently have two options, both worse than they should be: an AppImage
that needs `fuse2` (Arch ships only FUSE 3, so a stock AppImage fails with a
confusing mount error), or the portable tarball with no system integration. The
`.deb` does not apply.

This adds **`novaterminal-bin`**, an AUR package repackaging the published
`linux-x64` tarball into the same layout the `.deb` installs: `/usr/lib/novaterminal`
for the bundle, `/usr/bin/nova`, a `.desktop` entry, six hicolor icon sizes, and a
man page. Three scripts under `packaging/arch/`, one CI job, no change to any
existing lane.

The design problem is not the PKGBUILD — that is an afternoon. It is the two things
that make a `-bin` package rot: a dependency list that drifts from reality, and a
maintainer who has to notice a release happened. This addresses the first
mechanically and the second by making the publish a documented three-command
procedure that a workflow can later run unattended.

## What exists today (before this change)

`packaging/linux/` ships AppImage + `.deb` + tarball, gated by `smoke-test.sh` in
bare `ubuntu:22.04` containers, with the glibc floor (2.35) pinned by building
inside an `ubuntu:22.04` *container*. #385 recorded AUR as "cheapest of these: a
`PKGBUILD` repackaging the tarball, but it needs a maintainer who watches releases",
and noted the finding that makes naive packaging dangerous here: **eight of the nine
X11/fontconfig libraries the app needs are invisible to `ldd`**, because Avalonia
`dlopen`s them.

## Decisions

| Decision | Why |
|---|---|
| `-bin` only, no source-built `novaterminal` | Needs SDK 10.0.400 (pinned in `global.json`, not what Arch's `dotnet-sdk` tracks), the Rust natives, and a NuGet restore inside `build()` — makepkg builds are meant to be network-free after `source=()` |
| Repackage the tarball, not the `.deb` | The `.deb` is a Debian container whose `Depends:` derivation is wrong for Arch (see below). The tarball is the artifact `README.md` already offers as the portable install |
| `x86_64` only | The release publishes a `linux-arm64` tarball, but no arm64 build has been verified end to end. A package that fails on a declared architecture is worse than one that declares less |
| Keep `/usr/bin/nova` | It collides with `python-novaclient`, but pacman surfaces that as a clear file conflict rather than a shadowed command, and renaming would diverge from the `.deb` |
| No committed `PKGBUILD` | The two fields that change per release — version and tarball `sha256sum` — are exactly the two a human editing a checked-in file gets wrong |
| No local source files in the AUR repo | Keeps `packaging/linux/nova.desktop` and `nova.1` the single source of truth for both Linux packages, and avoids re-committing a 662 KB icon per release |
| Manual publish initially | The automation is worth wiring once the package shape has survived a real version bump; the procedure is three commands and is documented in `packaging/arch/README.md` |

## Dependency derivation — the core of the design

`namcap`, Arch's packaging linter, derives dependencies from ELF links. So does
`ldd`. Neither can see a `dlopen`. A `depends=()` produced by either would omit
eight libraries and yield a package that installs cleanly and then cannot open a
window — the exact defect `build-deb.sh`'s two-mechanism design exists to prevent,
reintroduced on a different distro.

So the Arch list is **not** written by hand and **not** derived by a tool. It is a
mapping, gated bidirectionally against the Debian table:

- `ARCH_DLOPEN_MAP` in `build-arch.sh` maps each soname in `build-deb.sh`'s
  `DLOPEN_LIBS` to an Arch package.
- The generator reads `build-deb.sh --print-dlopen-sonames` and **fails** if a
  soname has no mapping, or if a mapping names a soname no longer declared.
- `smoke-test.sh` asserts every soname resolves inside a container that installed
  nothing but this package, *and* that every derived package appears in the
  installed `depends`. Together: a library cannot be gated without being depended
  on, nor depended on without being gated.
- Both sides carry anti-vacuity checks, because an empty table would make the whole
  mechanism pass having compared nothing.

Adding a runtime dependency and gating it on Arch are therefore the same edit.

### The Arch list is shorter than the Debian one, on purpose

`build-deb.sh` derives via `ldd`, which walks the full *transitive* closure, so
`libbrotli1`, `libfreetype6`, `libpng16-16` and `libuuid1` land in `Depends:` as if
they were ours. On Arch they arrive through `fontconfig` and `glib2`. Listing them
again is redundant and namcap flags it.

Measured on the published v0.8.0 tarball, direct `DT_NEEDED` across the whole bundle
is only `libm`/`libc`/`ld` (the AOT binary), `libgcc_s` (the two Rust natives) and
`libfontconfig` (SkiaSharp). Everything else is reached through one of those or
dlopen'd.

Final list (15): `fontconfig libx11 libxrandr libxi libxcursor libxext libice libsm
libglvnd libsecret glib2 glibc gcc-libs icu hicolor-icon-theme`.

`icu` deserves its own note: it is dlopen'd by .NET's globalization stack, so it is
as invisible as the other eleven, but it sits outside `DLOPEN_LIBS` because Debian
must express it as a version-pinned alternatives group (`libicu74 | libicu72 | ...`)
in its `depends=` line. Arch ships one unversioned `icu`. It is listed explicitly in
`ARCH_EXTRA_DEPENDS` so that it is somewhere a gate can see it.

## Version mapping — where copying Debian would have been a bug

`build-deb.sh` maps `v0.8.0-rc.1` to `0.8.0~rc.1-1`, relying on dpkg's rule that `~`
sorts before everything. pacman's `vercmp` is rpmvercmp-derived and has no such rule.
Verified on pacman 7.1.0:

```
$ vercmp 0.8.0~rc.1 0.8.0   ->  1    # WRONG: prerelease sorts ABOVE the release
$ vercmp 0.8.0.rc.1 0.8.0   ->  1    # WRONG, same reason
$ vercmp 0.8.0rc1   0.8.0   -> -1    # correct
```

The rule that works is rpmvercmp's: where one version runs out of segments and the
other has an alphabetic one, the alphabetic is older. So the label is glued on with
its separators stripped — `v0.8.0-rc.1` → `0.8.0rc1`, `v0.9.0-beta.1` → `0.9.0beta1`.
Ordering among prereleases falls out of the same comparison.

Copying the `~` would have looked right, passed review by analogy, and offered every
rc to users as an upgrade over the release it precedes. `test-build-arch.sh` asserts
the mapping, the resulting order, *and* that the `~` form still sorts wrong — so if
pacman ever gains tilde semantics, the tests say so rather than quietly going stale.

A consequence: the mapping is lossy and one-way, so `_tag` is emitted into the
PKGBUILD verbatim. A PKGBUILD building its URLs from `"v$pkgver"` would 404 on every
prerelease, which is exactly when nobody is watching.

## Sources, and `--source-ref`

The PKGBUILD pulls five things: the release tarball from the releases endpoint, and
`nova.desktop`, `nova.1`, `nova_icon.png` and `LICENSE` from `raw.githubusercontent`
at the tag — all five `sha256`-pinned. The AUR repo therefore contains `PKGBUILD` and
`.SRCINFO` and nothing else.

The generator computes those four sums from the repository. Doing that from the
*working tree* is correct for a release built at its own tag and wrong everywhere
else: a PR that edits `nova.desktop` would pin the branch's sum against the tag's
URL, and the smoke test would fail an integrity check having nothing to do with the
change under review — a false red on the one lane meant to catch real ones.
`--source-ref <git-ref>` reads the blobs from that ref via `git show` instead. CI
passes the release tag.

## Layout

Mirrors the `.deb` exactly, so the two Linux packages are not subtly different
installs:

```
/usr/lib/novaterminal/            the AOT bundle (binary 0755, everything else 0644)
/usr/bin/nova                     symlink -> /usr/lib/novaterminal/NovaTerminal
/usr/share/applications/novaterminal.desktop
/usr/share/icons/hicolor/{16,32,48,64,128,256}x*/apps/novaterminal.png
/usr/share/man/man1/nova.1.gz     installed uncompressed; makepkg's zipman gzips it
/usr/share/licenses/novaterminal-bin/LICENSE
```

`options=('!strip' '!debug')`, with `package()` doing a selective
`strip --strip-unneeded` over the bundled `.so` files and leaving the NativeAOT
binary alone — makepkg's blanket strip makes no such distinction. No `.install`
scriptlet: `desktop-file-utils` and `hicolor-icon-theme` ship pacman hooks that
refresh the desktop and icon caches, which is only a valid argument because
`hicolor-icon-theme` is a declared dependency.

## CI topology

One new job, `arch_packaging`, on a plain runner (no `container:`) because
`smoke-test.sh` starts its own containers as its entire test premise.

Change detection reuses `linux_packaging_detect`, with its pattern widened to
`packaging/(linux|arch)/`. One detect job gating both lanes is deliberate:
`build-arch.sh` reads `build-deb.sh`'s table, so a change to either can break the
other, and a second arch-only detect job would have to restate that coupling and
could then disagree with this one.

The job tests **the newest published stable release**, not the linux dry run's
`0.0.1-ci` build. The AUR package does not build NovaTerminal; it repackages a
published release, and that tarball is its input. The dry run produces a `.deb` and
an AppImage but no tarball, and a PKGBUILD generated at `0.0.1-ci` would point at
release URLs that do not exist — so using the dry-run artifact would mean faking the
one thing the package consumes. The cost, stated plainly: this job depends on an
external, moving input and can fail for reasons outside the PR.

Two steps run inside Arch containers rather than on the runner, each for one reason:
the generator tests need `vercmp` (without it they *skip* the prerelease-ordering
assertions — the ones justifying the whole mapping), and generation needs `makepkg`
to produce `.SRCINFO`, the file the AUR rejects a push without.

## Verification

`smoke-test.sh`, three containers, phase discipline inherited from the Debian lane:

1. **`archlinux:base-devel`** — `makepkg` as an unprivileged user (`makepkg` refuses
   to run as root). `--nodeps`, because building compiles nothing; whether the
   runtime dependencies are installable and sufficient is container 2's job, which
   is a stronger check than makepkg's own resolution.
2. **`archlinux:base`, pristine** — `pacman -U`, which fails outright on a wrong
   dependency name. Then: every bundled ELF `ldd`-clean, all 11 dlopen sonames
   resolving, installed `depends` covering all 15 derived packages, full layout
   including all six icon sizes, and `nova --vt-report` headless. **Nothing may be
   installed here** except the package and what pacman pulls in for it — installing
   Xvfb or namcap first would satisfy the package's own missing dependencies and
   mask the exact bug this phase exists to catch. Phase B then adds validators and
   runs namcap.
3. **`archlinux:base` + Xvfb** — launch, poll for a window with `WM_CLASS`
   `NovaTerminal`, fail distinctly if the process exits before mapping one.

`nova --vt-report` is load-bearing on Arch specifically: it forces .NET globalization
to resolve ICU, so an ICU too far ahead of the floor fails there rather than on a
user's machine.

### Verified on real hardware (Omarchy, Arch, 2026-09-14)

The Debian lane gates at the *floor* — glibc 2.35, `libicu70`. Arch is the other end
of the range, and the differences are not cosmetic. Measured against the published
v0.8.0 tarball on a live Arch system (icu 78.3, glibc 2.42, pacman 7.1.0):

- **ICU resolves.** `libicuuc`, `libicui18n` and `libicudata` at 78.3 were all mapped
  into the running process; .NET finds the unversioned `/usr/lib/libicuuc.so` symlink
  Arch ships in the main `icu` package. No invariant-globalization fallback needed.
- **All 11 dlopen'd libraries resolved and were mapped at runtime**, `libsecret` and
  `glib2` included.
- **Asset resolution follows the real binary path, not the symlink.** Launched through
  a symlink standing in for `/usr/bin/nova`, all three bundled fonts opened from the
  bundle directory, a window mapped, and a PTY shell spawned. This was the open risk
  in the layout and it is now measured rather than assumed.
- **The full `smoke-test.sh` passes**, all three containers.

## Out of scope

- **`aarch64`** — until an arm64 build is verified end to end.
- **Publishing from CI.** An `aur_publish` job after `release_linux` needs an AUR
  account and an SSH deploy key in repo secrets. The manual procedure is documented;
  automate once the package shape has survived a real bump.
- **A source-built `novaterminal`**, `x-terminal-emulator`-style registration
  (tracked as #384), RPM, Flatpak and Snap (the rest of #385).

## Risks

| Risk | Severity | Mitigation |
|---|---|---|
| CI depends on an external moving input (the newest published release) | Medium — can red a PR for unrelated reasons | Accepted deliberately; the alternative never tests a real release until publish day |
| A future SkiaSharp/Avalonia bump adds a dlopen'd library | High — installs clean, cannot open a window | The bidirectional gate fails the build until the soname is mapped |
| Arch renames a package (`libgcc`/`libstdc++` split precedent) | Medium | `gcc-libs` chosen over the narrower `libgcc` precisely because it installs on both sides of that split |
| namcap gains a new error-level check | Low — reds the lane | Intended: `E:` is a gate, `W:` is advisory |
| The AUR package has no maintainer watching releases | Medium | The publish is three commands and gated; automating it is a documented next step |

## Acceptance criteria

1. `makepkg` builds the generated PKGBUILD against the live release URLs. **Met.**
2. The package installs on a pristine Arch system with its declared dependencies
   only. **Met.**
3. NovaTerminal appears in the app menu with its icon (all six hicolor sizes
   present). **Met.**
4. `nova` launches and maps a window; a shell runs inside it. **Met** (containerised
   under Xvfb, and on real hardware).
5. A dlopen'd dependency cannot be added on the Debian side without failing the Arch
   build until it is mapped. **Met**, asserted with a stub in `test-build-arch.sh`.
6. The lane runs unattended in CI. **Not yet** — the job exists and has never
   executed on GitHub.

## Findings during implementation

Recorded because each one passed something before it was caught.

### A smoke test that has never failed has proved nothing

Its first run failed on the man page — against a *correctly built* package. The
official archlinux images set `NoExtract` in `/etc/pacman.conf`, dropping
`/usr/share/man` at unpack time. This is the precise twin of `ubuntu:22.04`'s
`/etc/dpkg/dpkg.cfg.d/excludes`, which `packaging/linux/smoke-test.sh` already
removes with a comment saying not to clean it up. The prior art was there and was not
applied; the container was simply not faithful to the machine being tested for.

### The second green run still shipped a defect

Everything passed, and namcap printed
`E: Dependency hicolor-icon-theme detected and not included` into a phase that
reported `ok`, because namcap had been made advisory wholesale with `|| true`. The
finding was real: the PKGBUILD justified having no `.install` scriptlet by pointing at
hicolor-icon-theme's pacman hook while not depending on the package that provides it
— circular, and broken on a minimal system. namcap is now split by severity, with a
`command -v` preflight and an empty-output check so the gate cannot pass vacuously.

The general shape — output nobody reads, inside a step that reports success — is the
same defect class the Debian lane's retrospective called out as recurring.

### `bash -n` does not validate a script inside `bash -c '...'`

Each smoke phase is a script in single quotes. One apostrophe in a comment closes the
string early; an *even* number of stray quotes leaves the outer file balanced, so
`bash -n` passes while the container runs something else. This was hit while writing
the `NoExtract` fix. `test-build-arch.sh` now extracts each body, parses it
standalone, and rejects any single quote — applied to the CI job's inline scripts too.

### Running a test as root can only prove it passes as root

Two of the three CI failures were privilege artefacts invisible to every local run,
because those were run under `sudo`:

- `makepkg` **refuses to run as root**, so the generator tests died in a root
  container at the last step — reporting "generation failed" for a PKGBUILD that had
  been written correctly. A related trap surfaced with it: `makepkg --printsrcinfo >
  .SRCINFO` creates the file *before* makepkg runs, so a failure left a zero-byte
  `.SRCINFO` that every `test -f` would accept and the AUR would take.
- The makepkg container built as a container-local uid into a **bind-mounted**
  `mktemp -d` (mode 700), leaving the caller locked out of its own temp directory.
  Under `sudo` this is unobservable: root reads and removes regardless of ownership.
  The symptom was also badly misleading — `set -e` killed the script at an `ls` with
  no message, and the only output was the exit trap's `Operation not permitted`.

The general lesson is narrower than "test in CI": a privilege level is part of the
environment under test. Verification performed as root certifies behaviour as root,
and both of these defects live in the gap between that and how the thing actually
runs. The smoke test now maps the caller's uid into the build container, and its
cleanup trap can no longer determine the exit status or bury the real failure.

### Where a caveat should have been a mechanism

The working-tree-versus-tag hashing problem was written down as a documented
limitation ("this script must run at the tag it is generating for") and left there. It
would have produced false CI failures on any PR touching four specific files.
`--source-ref` replaced the caveat with a mechanism. Proving it needed a repository
where ref and tree differ — all four real files have been byte-identical across every
tag so far — so the test builds a throwaway repo rather than asserting nothing on the
real one.
