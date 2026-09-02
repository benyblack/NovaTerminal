# Linux Publishing: AppImage + .deb, x64 and arm64

Date: 2026-09-02
Status: designed (not implemented)
Companion to: `2026-08-24-windows-installer-velopack-design.md`,
`2026-08-28-macos-installer-velopack-design.md` (the two lanes this mirrors)
Closes the last leg of #91 ("Consider the same for macOS (notarization) / Linux (AppImage
or .deb) later").

## Summary

Give Linux the install and update experience Windows and macOS already have, on both
`linux-x64` and `linux-arm64`:

- an **AppImage** built by `vpk pack`, with full/delta nupkgs and a per-arch update feed,
  so the in-app updater works on Linux for the first time;
- a **`.deb`** with a `.desktop` entry, hicolor icons, `/usr/bin/nova`, and a man page, for
  users who want a system-integrated install;
- a **`tar.gz`** replacing today's portable zip, which is defective (see below).

The glibc floor is **2.35** (Ubuntu 22.04), set by publishing on the `ubuntu-22.04` runner.
Update channels are **`linux-x64`** and **`linux-arm64`** — not the platform-default
`linux` — because two architectures in one GitHub release must not share one feed.

## What exists today (before this change)

- `release.yml`'s `publish_aot` runs a NativeAOT self-contained publish for `linux-x64` on
  `ubuntu-latest`, then zips the raw publish directory as
  `NovaTerminal-linux-x64-<tag>.zip` with PowerShell `Compress-Archive`. That is the
  entire Linux distribution: no installer, no update feed, no desktop entry, no icon, no
  `nova` on PATH, no distro package.
- **The zip is defective.** `Compress-Archive` (System.IO.Compression) does not write Unix
  mode bits, so the extracted `NovaTerminal` binary arrives without its executable bit and
  every user must `chmod +x` before first launch. Fixing this is in scope here and is
  independent of everything else.
- **The current build silently excludes the most-deployed LTS.** `ubuntu-latest` is
  24.04 (glibc 2.39), and NativeAOT links against the build machine's glibc with no
  runtime fallback, so today's asset cannot start on Ubuntu 22.04 or Debian 12 — the
  loader refuses the binary outright. Unnoticed only because Linux download volume is low.
- The app is otherwise Linux-ready at runtime: `VelopackApp.Build().Run()` runs in
  `Program.Main` on all OSes, `VelopackUpdateService` + `GithubSource` are cross-platform,
  `librusty_pty.so` / `librusty_ssh.so` are built on the ubuntu runner, and secrets use
  the Secret Service.
- CLI modes (`--vt-report`, `--ssh-askpass`, `--replay`, `backup`) dispatch off `args` in
  `Program.Main`, **not** off `argv[0]`. A single `/usr/bin/nova` symlink therefore serves
  both the GUI and every CLI mode.
- `native/target_linux/release/` is an arch-agnostic staging directory and each runner
  builds natively for itself, so the arm64 lane needs no `.csproj` change.

## Decisions

| Decision | Choice | Rationale |
|---|---|---|
| Formats | AppImage + `.deb` + `tar.gz` | AppImage is the auto-updating path; `.deb` is the system-integrated path; tarball for everyone else |
| glibc floor | 2.35, via `ubuntu-22.04` | Covers Ubuntu 22.04/24.04, Debian 12+, Fedora 36+, Arch. One-line change, no container. Excludes Debian 11 and RHEL 8/9 |
| Architectures | `linux-x64` + `linux-arm64` | Native arm64 hosted runners are free for public repos |
| Update channels | `linux-x64`, `linux-arm64` | Two arches in one release must not share one feed |
| `.deb` updates | Manual reinstall; apt repo deferred | A signed APT feed is a long-term commitment; breaking it breaks users' package manager |
| Desktop integration | Standard (`.desktop`, icons, PATH, man) | `x-terminal-emulator` is a contract Nova cannot yet honour — see below |
| ICU | Keep it; alternatives `Depends:` | `InvariantGlobalization` is unset, and flipping it changes behaviour on all three platforms |
| Verification | Dry-run job + bare-container smoke gate | Nobody on the team runs Linux daily; the build runner cannot prove a user's machine works |

### Why not `x-terminal-emulator`

Registering via `update-alternatives` was considered and rejected **for now**, because it
is a contract rather than a label: callers invoke `x-terminal-emulator -e <command>`,
usually with a working directory. `Program.Main` implements exactly four CLI modes
(`--vt-report`, `--ssh-askpass`, `--replay`, `backup`) and passes everything else to
`StartWithClassicDesktopLifetime(args)`, which ignores unrecognised arguments. Registering
today would make a file manager's "Open in Terminal" launch Nova in `$HOME` and silently
discard the command — a broken feature is worse than an absent one.

What is *not* lost by deferring: `/usr/bin/nova` is on PATH, so a user who wants Nova as
their default can run `update-alternatives --install` themselves; the `.desktop` entry
already carries `Categories=System;TerminalEmulator;`, which is the discovery mechanism
the newer `xdg-terminal-exec` convention uses; and the major desktops do not consult
alternatives anyway (GNOME's Nautilus has no built-in "Open in Terminal", and the common
extension carries its own hardcoded terminal list — VS Code and the JetBrains IDEs each
use their own setting).

The prerequisite is app work, not packaging: `nova -e <cmd>` and
`nova --working-directory <dir>`. Deferred to its own issue, with the alternatives
registration as that issue's acceptance criterion.

## Facts verified against Velopack 1.2.0 docs

Read out of the Velopack documentation rather than assumed, because they are load-bearing
for a two-architecture release page:

1. **`vpk pack` on Linux produces an AppImage.** The Linux CLI help describes `pack` as
   "Create a Linux .AppImage bundle from application files" — one tool covers bundle,
   feed, and deltas, exactly as on the other two platforms. No separate `appimagetool`.
2. **Channel resolution.** "The default channel will be whatever channel was specified on
   the command line when building this release... If no channel is specified, it defaults
   to a channel named after the current operating system (e.g. 'win', 'osx', 'linux')."
   The channel is baked into the installed package's metadata.
3. **`UpdateOptions.ExplicitChannel`** overrides the default channel on the client.
4. **Applying an update delegates to the bundled updater binary**; check and download are
   in-process. On Linux the updater rewrites the AppImage in place, so an AppImage parked
   in a root-owned path (`/opt`, `/usr/local/bin`) cannot self-update.
5. **Delta prerequisite.** The previous version's full nupkg must be present in the output
   directory or no delta is generated. `vpk download github` fetches it, and resolves its
   channel from the runner's OS unless `--channel` is passed.

### Consequence of (2): the app-side change may be redundant

Because the channel is baked in at pack time, a client installed from a
`--channel linux-x64` package should resolve `linux-x64` on its own with no app change.
This design does **not** bet the arm64 update path on "should": the opening spike verifies it
empirically, and the explicit channel is added regardless (it is a no-op if the metadata
already carries the channel, a fix if it does not, and self-documenting either way).

## Artifact inventory

Per tag, per architecture (`<arch>` ∈ {`x64`, `arm64`}, `<debarch>` ∈ {`amd64`, `arm64`}):

| Asset | Produced by | Purpose |
|---|---|---|
| `NovaTerminal-linux-<arch>-<tag>.AppImage` | `vpk pack` (renamed) | Auto-updating portable app |
| `novaterminal_<ver>_<debarch>.deb` | `packaging/linux/build-deb.sh` | System-integrated install |
| `NovaTerminal-linux-<arch>-<tag>.tar.gz` | `tar` in `release.yml` | Portable; **replaces the broken zip** |
| `NovaTerminalApp-<ver>-linux-<arch>-full.nupkg` | `vpk pack` | Update feed (full) |
| `NovaTerminalApp-<ver>-linux-<arch>-delta.nupkg` | `vpk pack` | Update feed (delta) |
| `releases.linux-<arch>.json` | `vpk pack` | Feed index resolved by `VelopackUpdateService` |

**The zip becomes a tar.gz.** This renames a published asset, breaking anyone who scripts
the download URL. Accepted: the current file is defective (no executable bit), Linux
download volume is low, and shipping both would ship one broken artifact on purpose.

**Renaming convention** follows the macOS lane, which renames `*-osx-Setup.pkg` to
`NovaTerminal-Setup-osx-arm64-<tag>.pkg` so assets read consistently on the release page.

## CI topology

### `release.yml`

Three existing jobs change shape:

| Job | Today | After |
|---|---|---|
| `build_native` | `os: [windows-latest, ubuntu-latest, macos-latest]` | `ubuntu-latest` → `ubuntu-22.04`, plus `ubuntu-22.04-arm` |
| `release_tests` | same three | same swap, plus an arm64 leg |
| `publish_aot` | three `include` rows | `ubuntu-22.04`/`linux-x64` and `ubuntu-22.04-arm`/`linux-arm64` |

Mechanical consequences:

- `if: matrix.os == 'ubuntu-latest'` step guards become `startsWith(matrix.os, 'ubuntu')`.
- The `native-${{ matrix.os }}` artifacts become `native-ubuntu-22.04` and
  `native-ubuntu-22.04-arm`; the existing keying handles this without change.
- The arm64 runner images are leaner than the x64 ones, so the arm64 lane installs its AOT
  toolchain explicitly (`clang`, `zlib1g-dev`) rather than relying on image contents —
  `nightly.yml` already installs `clang` for the same reason.
- Release Linux tests now run on 22.04 while `ci.yml` tests stay on `ubuntu-latest`. This
  divergence is deliberate (the release lane must test on its own glibc floor) and needs a
  YAML comment so nobody "fixes" it.

`release_tests` gains an arm64 leg because it is the deterministic non-headless lane (`VT`,
`Rendering`, `Architecture`, `Platform`, `McpServer`, with the headless categories
filtered out), so it is architecture-portable — and a brand-new architecture is precisely
where that lane earns its keep.

**The smoke gate is a step, not a job.** The runner already has Docker, so the linux lane
runs `docker run --rm ubuntu:22.04` and the gate sits in the same job as the artifacts it
guards; a separate job would mean uploading, downloading and re-plumbing artifacts to gain
nothing. The lane reorders so that **nothing uploads before the smoke test passes**:

```
publish AOT → build .deb → vpk pack (AppImage + feed) → tar.gz
  → SMOKE (bare ubuntu:22.04 container)
    → upload all assets
```

Today `Upload release asset` runs *before* `vpk pack`; under the new order it runs last.

**Delta generation must pass `--channel` explicitly.** `vpk download github` resolves its
channel from the runner's OS, which would fetch `linux` — a channel we never publish. Both
`vpk download github` and `vpk pack` take `--channel linux-<arch>`. The existing
assertion pattern carries over: if a prior release exists on this channel but no delta
nupkg was produced, fail the job rather than publish a full-only release that silently
drops delta updates.

### `ci.yml`

One new job, **`linux_packaging`**, satisfying both verification needs at once:

- **Triggers**: `workflow_dispatch` (the dry run — verify asset names and layout without
  cutting a tag) and `pull_request` when `packaging/linux/**` or `.github/workflows/*.yml`
  changes (the smoke gate).
- **Standalone**, not `needs: [build]`. The existing `AOT Publish` job consumes a
  `dotnet-build-${{ matrix.os }}` artifact that has no 22.04 or arm64 equivalent.
- Publishes at version `0.0.1-ci` (vpk rejects anything below 0.0.1), builds the `.deb`,
  packs the AppImage, asserts every expected filename, runs `smoke-test.sh`, and uploads a
  `linux-packaging-dryrun` artifact.
- x64 only. The dry run exists to catch naming and layout mistakes, which are
  architecture-independent; the arm64 lane is exercised at release time.

### Shared scripts

Logic lives in `packaging/linux/`, called from both workflows, so it is reviewable,
locally runnable, and not duplicated across YAML:

- `build-deb.sh <publish-dir> <version> <debarch> <out-dir>`
- `smoke-test.sh <artifact-dir>` (runs the containers; needs only Docker)
- `nova.desktop`, `nova.1` (man page source), `README.md`

## The `.deb`

Built with **`dpkg-deb --build --root-owner-group`** from a staged tree. No `debhelper`, no
`dpkg-buildpackage`, no `fpm`: there is nothing to compile (the AOT bundle arrives
prebuilt), so the package is a file layout plus a control file, and `dpkg-deb` needs no
toolchain and no `fakeroot`.

### Layout

```
/usr/lib/novaterminal/              AOT bundle: NovaTerminal, librusty_pty.so,
                                    librusty_ssh.so, libSkiaSharp.so, Assets/, themes/
/usr/bin/nova                    -> /usr/lib/novaterminal/NovaTerminal
/usr/share/applications/novaterminal.desktop
/usr/share/icons/hicolor/16x16/apps/novaterminal.png   (also 32, 48, 64, 128, 256)
/usr/share/man/man1/nova.1.gz
/usr/share/doc/novaterminal/copyright
/usr/share/doc/novaterminal/changelog.Debian.gz
```

Icons are derived at packaging time from `src/NovaTerminal.App/Assets/nova_icon.png`, which
stays the single cross-platform source of truth — the same principle as
`packaging/macos/make-icns.sh`. No pre-scaled PNGs are committed.

### `.desktop` entry

```ini
[Desktop Entry]
Type=Application
Name=NovaTerminal
GenericName=Terminal Emulator
Comment=A modern terminal emulator
Exec=/usr/bin/nova
Icon=novaterminal
Terminal=false
Categories=System;TerminalEmulator;
Keywords=shell;prompt;command;commandline;terminal;
StartupNotify=true
StartupWMClass=NovaTerminal
```

`StartupWMClass` must match the WM class Avalonia actually sets, or the running window
will not associate with its launcher icon in GNOME and KDE. The packaging task verifies this with
`xprop` rather than assuming.

### Zero maintainer scripts

With `update-alternatives` deferred, nothing needs a `postinst`: `desktop-file-utils` and
`hicolor-icon-theme` ship dpkg triggers that refresh the desktop and icon caches on their
own. This removes the entire class of "broken postinst wedges apt" failure.

### `Depends:` is derived, not asserted

The build script computes dependencies in two parts, because neither mechanism alone is
sufficient:

1. **Linked dependencies** — `ldd` every ELF in the publish tree, map each soname to its
   owning package with `dpkg-query -S`, dedupe. Catches `libc6`, `libstdc++6`,
   `libgcc-s1`, `libssl3`, and whatever `libSkiaSharp.so` really pulls in.
2. **`dlopen`'d dependencies** — a hand-maintained list in the script, because `ldd`
   cannot see them. Avalonia's X11 backend loads `libX11`, `libXrandr`, `libXi`,
   `libXcursor`, `libXext`, `libICE`, `libSM` and `libGL` at runtime; SkiaSharp loads
   `libfontconfig1`.

A missing entry in list 2 is exactly the failure the bare-container smoke test exists to
catch, so the two mechanisms cover each other. `libc6 (>= 2.35)` is asserted explicitly to
match the build floor, so dpkg refuses the install rather than letting the loader fail
cryptically.

**ICU gets an alternatives list**: `libicu74 | libicu72 | libicu71 | libicu70`.
`InvariantGlobalization` is unset (so `false`) and the app needs ICU at runtime, but ICU
package names are version-pinned per distro (`libicu70` on 22.04, `libicu72` on Debian 12,
`libicu74` on 24.04). A hard dependency on any one of them would build a `.deb` that
refuses to install across most of the supported range, defeating the point of the glibc
floor. Ugly but standard practice for third-party debs.

### Version mapping

`v0.4.0` → `0.4.0-1`. Prereleases cannot pass through naively: dpkg reads the **last** `-`
as the revision separator, so `v0.5.0-beta.1` would parse as upstream `0.5.0` revision
`beta.1` and sort *above* the eventual `0.5.0-1`. The script maps prerelease `-` to `~`
(`0.5.0~beta.1-1`), because `~` sorts before everything — matching the `prerelease:` flag
logic already in `release.yml`.

## Update channels and the source change

One file changes: `src/NovaTerminal.App/Update/VelopackUpdateService.cs`.

```csharp
// Linux only: two architectures share one GitHub release, so each needs its own feed.
// vpk packs with --channel linux-{arch}, and Velopack resolves that from the installed
// package's own metadata — this makes it explicit rather than implicit, and gives the
// resolution a unit-testable seam. Null everywhere else keeps the win/osx feeds
// resolving exactly as they do today.
private static UpdateOptions? BuildOptions()
{
    if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return null;
    return RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64   => new UpdateOptions { ExplicitChannel = "linux-x64" },
        Architecture.Arm64 => new UpdateOptions { ExplicitChannel = "linux-arm64" },
        _ => null,
    };
}
```

Passed as the `UpdateManager` constructor's second argument, which is `null` today.
Returning `null` off Linux means Windows and macOS behaviour is provably unchanged. An
unrecognised architecture falls back to the platform default, finds no matching feed, and
reports "no update" rather than throwing.

There are **no installed Velopack clients on Linux**, so the channel naming is a free
choice with no migration path to honour.

**The `.deb` needs no updater work.** A dpkg install is not a Velopack install, so
`_manager.IsInstalled` is false and `IsSupported` is false — the updater stays silent,
which is the behaviour `IUpdateService` was already designed for. Its doc comment
(naming "portable zip, winget, dev runs") is extended to name system packages too.

This adds no `TerminalSettings` field, so no `TerminalPane.ApplySettings` whitelist entry
and no `McpServer` `SettingsTools` registration are required.

## Verification

### The contamination constraint

Installing `xvfb` drags in `libX11`, `libXext` and friends. Do that before checking the
`.deb`'s dependencies and the experiment is void: the package's missing `Depends:` get
satisfied by the test harness and the bug ships anyway. The smoke test therefore runs as
**two containers, in order**.

### Container 1 — pristine `ubuntu:22.04`, no X11

The **phase order here is load-bearing**, for the same reason `xvfb` is banned from this
container: `lintian`, `desktop-file-utils` and `man-db` are not present in a bare Ubuntu
image, and installing them pulls in transitive dependencies of their own. Every
dependency-completeness assertion therefore runs *before* any tooling is installed. After
phase A has passed, later installs cannot invalidate it.

**Phase A — dependency completeness, no new packages beyond the `.deb` itself:**

```sh
apt-get update && apt-get install -y ./novaterminal_*.deb   # fails if Depends incomplete
ldd /usr/lib/novaterminal/NovaTerminal | grep "not found" && exit 1
ldconfig -p | grep -q libX11.so.6          # dlopen'd deps must come from the .deb's
ldconfig -p | grep -q libfontconfig.so.1   #   OWN Depends, and nothing else's
nova --vt-report >/dev/null                # headless CLI mode: no X, no input file
test -x /usr/lib/novaterminal/NovaTerminal && test -L /usr/bin/nova
test -f /usr/share/man/man1/nova.1.gz
```

**Phase B — validators, tooling now permitted:**

```sh
apt-get install -y lintian desktop-file-utils man-db
desktop-file-validate /usr/share/applications/novaterminal.desktop
lintian --fail-on error novaterminal_*.deb
man nova >/dev/null                        # renders only once man-db is present
```

`--vt-report` is the headless probe because it needs no input file and exercises the AOT
binary and the VT core without touching X11. Its exact argument shape is confirmed in the
opening spike — `VtReportCommand.Execute` does its own parsing, and this design does not
assume the bare flag exits 0. `lintian` runs not for style pedantry but because it catches
genuinely broken packages: bad permissions, malformed control fields, missing copyright.

### Container 2 — GUI launch, xvfb permitted

```sh
xvfb-run -a nova &                                      # assert alive after 20s
xdotool search --class NovaTerminal                     # assert a window mapped
./NovaTerminal-*.AppImage --appimage-extract-and-run    # same checks
apt-get install -y libfuse2 && ./NovaTerminal-*.AppImage   # and again, FUSE-mounted
```

The AppImage is tested **twice on purpose**: extracted (no FUSE) and mounted. Ubuntu
22.04+ does not ship `libfuse2`, so a stock type-2 AppImage fails there with a confusing
FUSE error. That is a genuine user-facing trap and needs a line in the release notes:
install `libfuse2`, or run with `--appimage-extract-and-run`.

### Also verified

- **In-app update N → N+1** on Linux, once two releases exist on a channel. Until then,
  the opening spike's `0.0.1-ci` pack plus feed inspection is the available evidence.
- The tarball preserves the executable bit (`tar -tvf` shows `-rwxr-xr-x`).

## Documentation

- `packaging/linux/README.md`, mirroring `packaging/macos/README.md`: the asset table, the
  channel facts, where user data lives (`~/.local/share/NovaTerminal` via `AppPaths`,
  untouched by updates and uninstall), how to run the dry run, and the known traps
  (`libfuse2`, AppImage self-update needs a writable location, making Nova the default
  terminal by hand with `update-alternatives`).
- `README.md`: Linux install instructions for all three formats, with the glibc 2.35 /
  Ubuntu 22.04 floor stated plainly.

## Out of scope

Each gets a tracked issue rather than a mention:

- Signed **APT repository** on GitHub Pages (`apt upgrade` integration, GPG key custody
  and rotation). Deferred deliberately: an apt feed is a long-term commitment and breaking
  it breaks users' package manager.
- **`nova -e <cmd>` / `--working-directory`**, then `x-terminal-emulator` registration.
- **Flatpak / Flathub.** A terminal emulator needs `--filesystem=host` and host-spawn
  access, which reviewers push back on; it also needs AppStream metainfo and a separate
  repo and release cadence.
- **AUR, RPM, Snap**; **musl / Alpine**; **32-bit**.
- **GPG-signed artifacts and `SHA256SUMS`.**

## Risks

| Risk | Severity | Mitigation |
|---|---|---|
| `vpk` 1.2.0 may not run on linux-arm64 (dotnet tool carrying native updater binaries; an arm64 build may not exist) | High — blocks the arm64 AppImage | Opening spike. Fallback: cross-pack from the x64 runner via the documented `vpk [linux] pack --runtime linux-arm64` directive, with AOT publish still native on arm64. Worst case arm64 ships `.deb` + tarball only — a decision to bring back to the user, not to take silently |
| GitHub retires the `ubuntu-22.04` runner | Medium — forces a rebuild of the lane | Known one-way door. The floor then has to move to a container build (the option declined on 2026-09-02). Recorded here so the reason stays legible |
| AppImage FUSE friction on 22.04+ | Medium — first-run failure | Documented in release notes and `packaging/linux/README.md`; both launch paths smoke-tested |
| `libicu` alternatives list goes stale on a future distro | Low | One-line fix; caught if that distro joins the smoke matrix |
| AppImage cannot self-update from a root-owned path | Low | Documented: keep it in `~/Applications` |
| arm64 runner availability | Low | Free for public repos; `fail-fast: false` keeps an arm64 failure from killing the x64 release |
| `StartupWMClass` mismatch breaks icon association | Low | Verified with `xprop` during packaging rather than assumed |

## Acceptance criteria

1. A tag produces, for both `linux-x64` and `linux-arm64`: an AppImage, a `.deb`, a
   `tar.gz`, full and delta nupkgs, and `releases.linux-<arch>.json`.
2. `apt-get install ./novaterminal_*.deb` succeeds in a pristine `ubuntu:22.04` container
   and `nova` launches a window under `xvfb`.
3. The AppImage launches both extracted and FUSE-mounted.
4. The tarball's `NovaTerminal` binary carries its executable bit.
5. `nova` is on PATH, `man nova` renders, and NovaTerminal appears in the app menu with
   its icon.
6. The in-app updater offers N+1 on Linux and applies it; an arm64 client is never offered
   an x64 package.
7. A `.deb`-installed app reports no available update and does not surface updater UI.
8. `lintian --fail-on error` passes.
9. Windows and macOS release assets, channels, and update behaviour are byte-for-byte
   unchanged.

## Spike findings (Task 0, 2026-09-02)

1. **`vpk` on linux-arm64:** NOT VERIFIED — no local arm64 emulation; must be answered
   by the first CI run. This machine's Docker is linux/amd64 with no qemu binfmt
   handler registered (`docker run --platform linux/arm64 ...` fails with
   `exec format error`), and installing qemu binfmt or pushing to run CI were both
   outside what was authorised for this task, so Step 1 was skipped rather than
   guessed at.
2. **Cross-pack fallback:** the `[linux]` directive is confirmed usable from an amd64
   Windows host — `vpk [linux] pack -h` prints
   `Directive enabled for cross-compiling from Windows (current os) to Linux.` and
   shows the Linux `pack` help ("Create a Linux .AppImage bundle from application
   files."). Its `--runtime`/`-r <RID>` option is a free-form string with no
   help-text-enumerated restriction, so it syntactically accepts
   `--runtime linux-arm64`; whether that flag actually produces a working arm64
   AppImage from an amd64 host was not exercised (consistent with Step 1 being
   skipped) and remains open for the first CI run.
3. **Channel metadata:** baked into the package — `vpk pack --channel linux-x64`
   writes `<channel>linux-x64</channel>` and `<rid>linux-x64</rid>` directly into the
   `.nuspec` inside the `.nupkg`, and the channel is also encoded in the release
   manifest filenames (`releases.linux-x64.json`, `RELEASES-linux-x64`). So
   `ExplicitChannel` is a no-op safety net: a client packed with the correct
   `--channel` flag already carries and resolves its own channel from package
   metadata; `ExplicitChannel` only guards against a future pack invocation that
   omits `--channel` and silently falls back to vpk's platform-default `linux`.
4. **Headless probe:** `nova --vt-report` exits 0 (also `nova --vt-report --json`).
   Any other argument shape — a bare `--json` without `--vt-report`, a duplicated
   flag, or any unrecognised third argument — exits 2
   (`src/NovaTerminal.App/Shell/VtReportCommand.cs`, `ParseArguments`). This dispatch
   runs before Avalonia's `AppBuilder` is touched (`Program.cs`), so it needs no
   display server. Empirically confirmed via a Windows build
   (`scripts/build.sh build src/NovaTerminal.App` then
   `NovaTerminal.exe --vt-report` → exit code 0); the equivalent Linux binary was not
   built in this task, so the Linux result is source-derived (the code path is
   platform-agnostic — no Avalonia, no P/Invoke) rather than independently verified
   on Linux.
