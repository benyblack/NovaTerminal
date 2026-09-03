# Linux Publishing (AppImage + .deb) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship NovaTerminal on Linux as an auto-updating AppImage plus a system-integrated `.deb`, for `linux-x64` and `linux-arm64`, replacing today's single broken portable zip.

**Architecture:** `vpk pack` (Velopack 1.2.0, already used for Windows and macOS) builds the AppImage and a per-architecture update feed; a plain `dpkg-deb` script builds the `.deb` from the same NativeAOT publish directory. Both are gated by a two-container smoke test that installs into a pristine `ubuntu:22.04` and proves the artifacts launch. One C# file changes, to give each architecture its own update channel.

**Tech Stack:** .NET 10 NativeAOT, Avalonia 12, Velopack 1.2.0 / `vpk`, `dpkg-deb`, `lintian`, Docker, GitHub Actions, bash.

**Spec:** `docs/superpowers/specs/2026-09-02-linux-packaging-design.md`

## Global Constraints

Every task's requirements implicitly include these. Values are copied verbatim from the spec.

- **glibc floor is 2.35.** All Linux release publishing happens on `ubuntu-22.04` (x64) and `ubuntu-22.04-arm` (arm64). Never `ubuntu-latest` in a release publishing lane.
  > **SUPERSEDED IN IMPLEMENTATION — the mechanism, not the floor.** `actions/runner-images#14254` deprecates the `ubuntu-22.04` runner image from 2026-09-17 (fully unsupported 2027-04-17), so the constraint above was inverted during implementation: every Linux publishing job runs `runs-on: ubuntu-latest` / `ubuntu-24.04-arm` **with `container: ubuntu:22.04`**, which is what now pins the floor. Read the "Never `ubuntu-latest`" rule as "never without `container: ubuntu:22.04`". **The glibc 2.35 floor itself is unchanged**, along with every downstream value in this plan (`libc6 (>= 2.35)`, the supported-distro list, the derived `Depends:`). The one job that cannot be containerised is the smoke gate (`release_linux` / ci.yml's `linux_packaging_smoke`) — a `container:` job has no docker daemon — and it compiles nothing, so it sets no floor. Every `ubuntu-22.04`/`ubuntu-22.04-arm` runner label elsewhere in this plan, including the step snippets, is superseded the same way.
- **`vpk` is pinned to `1.2.0`** and must stay in lockstep with the `Velopack` `PackageVersion` in `Directory.Packages.props` and the two existing `dotnet tool install -g vpk --version 1.2.0` call sites.
- **Update channels are `linux-x64` and `linux-arm64`.** Never the platform-default `linux`. Both `vpk pack` and `vpk download github` must be passed `--channel`.
- **Debian package name is `novaterminal`**; bundle installs to `/usr/lib/novaterminal/`; PATH entry is `/usr/bin/nova`.
- **`.deb` has no maintainer scripts.** No `postinst`, `prerm`, `postrm`. dpkg triggers from `desktop-file-utils` and `hicolor-icon-theme` handle cache refreshes.
- **`dpkg-deb --build --root-owner-group`** — never `fakeroot`, never `dpkg-buildpackage`.
- **Archives use `tar`, never `Compress-Archive`.** `Compress-Archive` cannot write Unix mode bits, which is the bug being fixed.
- **Builds go through the wrapper scripts** (`scripts/build.ps1` / `scripts/build.sh`), never raw `dotnet build`, per `CLAUDE.md`. `dotnet publish` in CI is the documented exception and already carries `-nodeReuse:false` via workflow-level env.
- **Windows and macOS behaviour must not change.** Their assets, channels, and update paths are regression surfaces, not deliverables.
- **Icons derive from `src/NovaTerminal.App/Assets/nova_icon.png`** at packaging time. Never commit pre-scaled PNGs.
- **No new `TerminalSettings` field is introduced.** (If a later change adds one, it must also be added to `TerminalPane.ApplySettings`'s `effectiveSettings` whitelist and registered in `McpServer` `SettingsTools`, or gating drift-guard tests fail.)

---

### Task 0: Spike — resolve the three unknowns before building on them

This task writes no shippable code. It answers three questions the rest of the plan depends on, and records the answers. Stop and report if answer (1) is "no".

**Files:**
- Modify: `docs/superpowers/specs/2026-09-02-linux-packaging-design.md` (append a "Spike findings" section)

**Interfaces:**
- Consumes: nothing.
- Produces: three recorded facts — (a) whether `vpk` runs on linux-arm64, (b) the channel a packed client actually resolves, (c) the exact `--vt-report` invocation that exits 0. Tasks 3, 4 and 5 read these.

- [ ] **Step 1: Establish whether `vpk` runs on linux-arm64**

This must run on arm64. Push a scratch branch with a `workflow_dispatch` job, or run it in an arm64 container locally:

```bash
docker run --rm --platform linux/arm64 mcr.microsoft.com/dotnet/sdk:10.0 bash -c '
  dotnet tool install -g vpk --version 1.2.0 &&
  export PATH="$PATH:/root/.dotnet/tools" &&
  vpk --version && vpk pack -h
'
```

Expected if supported: `vpk` prints `Velopack CLI 1.2.0` and the Linux `pack` help ("Create a Linux .AppImage bundle from application files"). Record the verbatim output.

- [ ] **Step 2: If Step 1 failed, test the cross-pack fallback**

On x64:

```bash
dotnet tool install -g vpk --version 1.2.0
vpk [linux] pack -h
```

Record whether the `[linux]` directive accepts `--runtime linux-arm64`. If **both** Step 1 and Step 2 fail, **stop and report to the user** — the spec names this as a decision to bring back, not to take silently. The fallback position is that arm64 ships `.deb` + tarball with no AppImage and no auto-update.

- [ ] **Step 3: Determine which channel a packed client resolves**

On x64, pack a throwaway release and inspect what the client would ask for:

```bash
mkdir -p /tmp/vpkprobe/app && cp /bin/true /tmp/vpkprobe/app/NovaTerminal
vpk pack --packId NovaTerminalApp --packVersion 0.0.1-ci \
  --packDir /tmp/vpkprobe/app --mainExe NovaTerminal \
  --channel linux-x64 -o /tmp/vpkprobe/out
ls /tmp/vpkprobe/out
```

Expected: `releases.linux-x64.json` and `NovaTerminalApp-0.0.1-ci-linux-x64-full.nupkg` exist. Then unpack the nupkg and find where the channel is recorded:

```bash
cd /tmp/vpkprobe && unzip -o out/NovaTerminalApp-0.0.1-ci-linux-x64-full.nupkg -d unpacked
grep -ri "linux-x64" unpacked/ | head -20
```

Record whether the channel is baked into the package metadata. This determines whether Task 1's `ExplicitChannel` is a no-op safety net (expected) or a load-bearing fix.

- [ ] **Step 4: Determine the exact `--vt-report` invocation that exits 0**

The smoke test needs a headless probe. `VtReportCommand.Execute` does its own argument parsing, so do not assume the bare flag suffices:

```bash
sed -n '1,80p' src/NovaTerminal.App/Shell/VtReportCommand.cs
```

Read `TryParse`/the `seenReportFlag` logic, then confirm against a real build:

```bash
./scripts/build.sh build src/NovaTerminal.App
# then run the built binary with the flag shape the source requires, e.g.
#   <bin>/NovaTerminal --vt-report
# and record the exit code
```

Record the exact argument list and expected exit code. Task 3 hardcodes it.

- [ ] **Step 5: Record findings in the spec**

Append to the spec:

```markdown
## Spike findings (Task 0, YYYY-MM-DD)

1. **`vpk` on linux-arm64:** <supported / not supported — verbatim output>
2. **Cross-pack fallback:** <needed? / accepts --runtime linux-arm64?>
3. **Channel metadata:** <baked into the package? where?> — so `ExplicitChannel` is
   <a no-op safety net / a load-bearing fix>.
4. **Headless probe:** `nova <exact args>` exits <code>.
```

- [ ] **Step 6: Commit**

```bash
git add docs/superpowers/specs/2026-09-02-linux-packaging-design.md
git commit -m "docs(linux): record packaging spike findings

Answers the three unknowns Task 0 exists to close: whether vpk runs on
linux-arm64, whether the update channel is baked into the packed client,
and the exact --vt-report invocation the smoke test can rely on."
```

---

### Task 1: Per-architecture update channel

**Files:**
- Modify: `src/NovaTerminal.App/Update/VelopackUpdateService.cs`
- Modify: `src/NovaTerminal.App/Update/IUpdateService.cs` (doc comment only)
- Test: `tests/NovaTerminal.App.Tests/Update/VelopackUpdateServiceTests.cs`

**Interfaces:**
- Consumes: Task 0 finding (3).
- Produces: `internal static string? VelopackUpdateService.ResolveExplicitChannel(bool isLinux, Architecture architecture)` — returns `"linux-x64"`, `"linux-arm64"`, or `null`. Tasks 4 and 5 must pack with channel names matching this exactly.

`NovaTerminal.App` already declares `<InternalsVisibleTo Include="NovaTerminal.App.Tests" />` (`NovaTerminal.App.csproj:508`), so an `internal static` member is directly testable. `App.Tests` has `<Using Include="Xunit" />` as a global using, so test files need no `using Xunit;`.

- [ ] **Step 1: Write the failing tests**

Append to `tests/NovaTerminal.App.Tests/Update/VelopackUpdateServiceTests.cs`:

```csharp
    // Channel resolution is a pure function taking isLinux and architecture explicitly,
    // rather than reading RuntimeInformation directly, so these cases are assertable on any
    // CI leg regardless of the host OS and CPU. That is the whole reason for the seam.
    [Theory]
    [InlineData(true, Architecture.X64, "linux-x64")]
    [InlineData(true, Architecture.Arm64, "linux-arm64")]
    public void Resolves_a_per_architecture_channel_on_linux(
        bool isLinux, Architecture architecture, string expected)
    {
        Assert.Equal(expected, VelopackUpdateService.ResolveExplicitChannel(isLinux, architecture));
    }

    /// <summary>
    /// Null means "let Velopack use the channel this release was packed with". Windows and
    /// macOS have shipped installed clients against their platform-default channels (win, osx)
    /// since #91, so returning anything but null off Linux would repoint existing installs at
    /// a feed that does not exist.
    /// </summary>
    [Theory]
    [InlineData(false, Architecture.X64)]
    [InlineData(false, Architecture.Arm64)]
    public void Resolves_no_explicit_channel_off_linux(bool isLinux, Architecture architecture)
    {
        Assert.Null(VelopackUpdateService.ResolveExplicitChannel(isLinux, architecture));
    }

    /// <summary>
    /// An architecture we publish no feed for must degrade to "no update available", not to a
    /// wrong feed and not to a throw. Null falls back to the packed default, which finds
    /// nothing on a channel we never published.
    /// </summary>
    [Theory]
    [InlineData(Architecture.X86)]
    [InlineData(Architecture.Arm)]
    public void Resolves_no_explicit_channel_for_unpublished_architectures(Architecture architecture)
    {
        Assert.Null(VelopackUpdateService.ResolveExplicitChannel(true, architecture));
    }
```

Add `using System.Runtime.InteropServices;` to the file's using block.

- [ ] **Step 2: Run the tests to verify they fail**

```bash
./scripts/build.sh test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~VelopackUpdateServiceTests"
```

Expected: compile failure — `'VelopackUpdateService' does not contain a definition for 'ResolveExplicitChannel'`.

- [ ] **Step 3: Implement the resolution and wire it into the constructor**

In `src/NovaTerminal.App/Update/VelopackUpdateService.cs`, add `using System.Runtime.InteropServices;` and insert:

```csharp
        /// <summary>
        /// The Velopack channel this process should read updates from, or null to use whatever
        /// channel the running release was packed with.
        /// </summary>
        /// <remarks>
        /// Linux is the only platform that needs this. Windows publishes one architecture and
        /// macOS one, so each resolves its platform-default channel (win, osx) unambiguously.
        /// Linux publishes x64 and arm64 into the SAME GitHub release, and a Velopack feed is
        /// per-channel, not per-architecture - so a single `linux` channel would put both
        /// architectures' packages in one releases.linux.json and hand an arm64 client an x64
        /// update. Hence a channel per architecture, matching the --channel passed to `vpk pack`
        /// in release.yml.
        ///
        /// Taking isLinux and architecture as parameters rather than reading RuntimeInformation
        /// inline is what makes this assertable on any CI leg (see VelopackUpdateServiceTests).
        ///
        /// Returning null off Linux is load-bearing, not tidiness: Windows and macOS have
        /// installed clients in the field on their default channels, and naming a channel here
        /// would repoint them at a feed that does not exist.
        /// </remarks>
        internal static string? ResolveExplicitChannel(bool isLinux, Architecture architecture)
        {
            if (!isLinux)
            {
                return null;
            }

            return architecture switch
            {
                Architecture.X64 => "linux-x64",
                Architecture.Arm64 => "linux-arm64",
                // Any architecture we publish no feed for: fall back to the packed default,
                // which finds nothing rather than offering a package for the wrong CPU.
                _ => null,
            };
        }

        private static UpdateOptions? BuildUpdateOptions()
        {
            var channel = ResolveExplicitChannel(
                RuntimeInformation.IsOSPlatform(OSPlatform.Linux),
                RuntimeInformation.ProcessArchitecture);

            return channel is null ? null : new UpdateOptions { ExplicitChannel = channel };
        }
```

Then change the constructor's last line from:

```csharp
            _manager = new UpdateManager(new GithubSource(repoUrl, null, false), null, locator);
```

to:

```csharp
            _manager = new UpdateManager(new GithubSource(repoUrl, null, false), BuildUpdateOptions(), locator);
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
./scripts/build.sh test tests/NovaTerminal.App.Tests --filter "FullyQualifiedName~VelopackUpdateServiceTests"
```

Expected: PASS, including the three pre-existing tests in that class (they assert an unsupported host stays inert, which the new options argument must not disturb).

The spec also calls for a test that a `.deb` install surfaces no updater UI. The
pre-existing `Is_not_supported_when_the_process_is_not_a_velopack_install` already *is*
that test: `App.Tests` runs on the ubuntu CI leg, where the test host is a non-Velopack
Linux layout — exactly the `.deb` case. No new test is needed; confirm it still passes on
the Linux leg rather than duplicating it.

- [ ] **Step 5: Extend the `IUpdateService` doc comment to name system packages**

In `src/NovaTerminal.App/Update/IUpdateService.cs`, change:

```csharp
        /// False when this process was not installed by Velopack - a portable zip, a winget
        /// portable install, or a dev run. Those must never see update UI or errors.
```

to:

```csharp
        /// False when this process was not installed by Velopack - a portable zip, a winget
        /// portable install, a Linux system package (.deb), or a dev run. Those must never see
        /// update UI or errors. A .deb install is updated through the user's package manager,
        /// so the in-app updater staying silent there is correct, not a gap.
```

- [ ] **Step 6: Commit**

```bash
git add src/NovaTerminal.App/Update/VelopackUpdateService.cs \
        src/NovaTerminal.App/Update/IUpdateService.cs \
        tests/NovaTerminal.App.Tests/Update/VelopackUpdateServiceTests.cs
git commit -m "feat(update): resolve a per-architecture Velopack channel on Linux

Linux publishes x64 and arm64 into the same GitHub release, but a
Velopack feed is per-channel, not per-architecture. On the default
'linux' channel both architectures' packages land in one
releases.linux.json and an arm64 client can be offered an x64 update.

Resolve linux-x64 / linux-arm64 explicitly, matching the --channel that
vpk pack will use. Null off Linux, so the win and osx feeds - which have
installed clients in the field - resolve exactly as before."
```

---

### Task 2: `build-deb.sh` — the Debian package

**Files:**
- Create: `packaging/linux/build-deb.sh`
- Create: `packaging/linux/nova.desktop`
- Create: `packaging/linux/nova.1`
- Create: `packaging/linux/test-build-deb.sh`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `packaging/linux/build-deb.sh <publish-dir> <version> <debarch> <out-dir>` writing `<out-dir>/novaterminal_<debver>_<debarch>.deb`. Tasks 3, 4 and 5 invoke it with exactly this argument order.

- [ ] **Step 1: Write the failing test**

`packaging/linux/test-build-deb.sh` — a self-contained harness that needs no real NovaTerminal build. It fabricates a publish directory whose "binary" is a real ELF (`/bin/true`), so `ldd`-derived dependency detection exercises its real code path:

```bash
#!/usr/bin/env bash
# Tests build-deb.sh without needing a real NovaTerminal publish. Run inside a
# Debian-family container (it needs dpkg-deb, dpkg-query and file):
#   docker run --rm -v "$PWD:/w" -w /w ubuntu:22.04 \
#     bash -c 'apt-get update -qq && apt-get install -y -qq file binutils >/dev/null &&
#              packaging/linux/test-build-deb.sh'
set -uo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
script="$here/build-deb.sh"
fails=0

fail() { echo "FAIL: $*"; fails=$((fails + 1)); }
pass() { echo "ok: $*"; }

# ---- version mapping ------------------------------------------------------
# dpkg reads the LAST '-' as the revision separator, so a SemVer prerelease must
# have its separator mapped to '~' or it sorts ABOVE the final release.
check_version() {
  local got
  got="$("$script" --print-debian-version "$1")" || { fail "--print-debian-version $1 errored"; return; }
  if [[ "$got" == "$2" ]]; then pass "version $1 -> $got"; else fail "version $1 -> $got (want $2)"; fi
}
check_version v0.4.0        "0.4.0-1"
check_version 0.4.0         "0.4.0-1"
check_version v0.5.0-beta.1 "0.5.0~beta.1-1"
check_version v1.0.0-rc.2   "1.0.0~rc.2-1"
check_version 0.0.1-ci      "0.0.1~ci-1"

# ---- package construction -------------------------------------------------
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
pub="$work/publish"; out="$work/out"
mkdir -p "$pub" "$out"
cp /bin/true "$pub/NovaTerminal"          # a real ELF, so ldd has something to read
echo "placeholder" > "$pub/nova_icon.png" # icon scaling must tolerate a bad PNG (warn, not die)
mkdir -p "$pub/themes" && echo '{}' > "$pub/themes/default.json"

if ! "$script" "$pub" "0.4.0" "amd64" "$out"; then
  fail "build-deb.sh exited non-zero"
else
  deb="$out/novaterminal_0.4.0-1_amd64.deb"
  [[ -f "$deb" ]] && pass "produced $(basename "$deb")" || fail "expected $deb"

  if [[ -f "$deb" ]]; then
    contents="$(dpkg-deb --contents "$deb")"
    for path in \
      ./usr/lib/novaterminal/NovaTerminal \
      ./usr/lib/novaterminal/themes/default.json \
      ./usr/bin/nova \
      ./usr/share/applications/novaterminal.desktop \
      ./usr/share/man/man1/nova.1.gz \
      ./usr/share/doc/novaterminal/copyright
    do
      grep -q -- "$path" <<<"$contents" || fail "missing from package: $path"
    done
    pass "layout checked"

    # The bundle binary must be executable, and /usr/bin/nova must be a symlink to it.
    grep -qE '^-rwxr-xr-x.* \./usr/lib/novaterminal/NovaTerminal$' <<<"$contents" \
      || fail "NovaTerminal is not 0755 in the package"
    grep -qE '^lrwxrwxrwx.* \./usr/bin/nova -> ' <<<"$contents" \
      || fail "/usr/bin/nova is not a symlink"

    # Files must be root-owned (--root-owner-group), never the CI user's uid.
    grep -q 'root/root' <<<"$contents" || fail "package files are not root-owned"

    info="$(dpkg-deb --field "$deb")"
    grep -q '^Package: novaterminal$'   <<<"$info" || fail "wrong Package field"
    grep -q '^Version: 0.4.0-1$'        <<<"$info" || fail "wrong Version field"
    grep -q '^Architecture: amd64$'     <<<"$info" || fail "wrong Architecture field"
    grep -q '^Depends: .*libc6 (>= 2.35)' <<<"$info" || fail "Depends lacks the glibc floor"
    grep -q 'libfontconfig1'            <<<"$info" || fail "Depends lacks libfontconfig1 (dlopen'd by Skia)"
    grep -q 'libx11-6'                  <<<"$info" || fail "Depends lacks libx11-6 (dlopen'd by Avalonia)"
    grep -q 'libicu'                    <<<"$info" || fail "Depends lacks an ICU alternatives list"
    pass "control fields checked"

    # No maintainer scripts, by design: dpkg triggers handle the caches.
    for s in preinst postinst prerm postrm; do
      dpkg-deb --ctrl-tarfile "$deb" | tar -t 2>/dev/null | grep -q "$s" \
        && fail "unexpected maintainer script: $s"
    done
    pass "no maintainer scripts"
  fi
fi

echo
if (( fails )); then echo "$fails check(s) failed"; exit 1; fi
echo "all checks passed"
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
chmod +x packaging/linux/test-build-deb.sh
docker run --rm -v "$PWD:/w" -w /w ubuntu:22.04 bash -c \
  'apt-get update -qq && apt-get install -y -qq file >/dev/null && packaging/linux/test-build-deb.sh'
```

Expected: fails immediately — `build-deb.sh` does not exist.

- [ ] **Step 3: Write the `.desktop` entry and man page**

`packaging/linux/nova.desktop`:

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

`packaging/linux/nova.1`:

```roff
.TH NOVA 1 "2026-09-02" "NovaTerminal" "User Commands"
.SH NAME
nova \- NovaTerminal, a modern terminal emulator
.SH SYNOPSIS
.B nova
.RI [ options ]
.SH DESCRIPTION
.B nova
launches the NovaTerminal graphical terminal emulator. Run with no arguments to
open a window with your default shell.
.PP
The same executable also serves several headless command modes, used by CI and by
agent tooling. Any argument list that does not match one of them launches the GUI.
.SH OPTIONS
.TP
.B \-\-vt-report
Print a VT/ANSI conformance report to standard output and exit.
.TP
.BI \-\-replay " FILE"
Replay a recorded session file through the deterministic terminal core and print the
final screen. Exit status is 0 on success, 1 if the file is unreadable or truncated,
and 2 on a usage error.
.TP
.B backup
Configuration backup and restore. See
.B nova backup \-\-help
for its own subcommands.
.TP
.B \-\-ssh-askpass
Act as an SSH_ASKPASS helper. Invoked by ssh, not normally by hand.
.SH FILES
.TP
.I ~/.local/share/NovaTerminal
Configuration, themes, and session state. Never modified by package upgrades or removal.
.SH NOTES
This package does not register NovaTerminal as the system
.BR x-terminal-emulator (1).
To do so yourself:
.PP
.RS
.nf
sudo update\-alternatives \-\-install /usr/bin/x\-terminal\-emulator \\
    x\-terminal\-emulator /usr/bin/nova 40
.fi
.RE
.PP
Note that callers of
.B x-terminal-emulator
pass
.BI \-e " command"
to run a single command, which
.B nova
does not yet implement.
.SH SEE ALSO
Project homepage: https://github.com/benyblack/NovaTerminal
```

- [ ] **Step 4: Write `build-deb.sh`**

```bash
#!/usr/bin/env bash
# Build a NovaTerminal .deb from a NativeAOT publish directory.
#
# Usage: build-deb.sh <publish-dir> <version> <debarch> <out-dir>
#        build-deb.sh --print-debian-version <version>
#
# There is nothing to compile here - the AOT bundle arrives prebuilt - so this is a
# file layout plus a control file, which is exactly what dpkg-deb is for. No
# debhelper, no dpkg-buildpackage, no fakeroot.
set -euo pipefail

# --- version mapping -------------------------------------------------------
# dpkg reads the LAST '-' in a version as the revision separator, so a SemVer
# prerelease passed through unchanged would parse as upstream 0.5.0 revision
# "beta.1" and sort ABOVE the eventual 0.5.0-1. '~' sorts before everything, so the
# prerelease separator becomes '~'. Mirrors release.yml's `contains('-')` prerelease
# test, which reads the same '-' the same way.
print_debian_version() {
  local v="${1#v}"
  printf '%s-1\n' "${v/-/\~}"
}

if [[ "${1:-}" == "--print-debian-version" ]]; then
  [[ $# -eq 2 ]] || { echo "usage: $0 --print-debian-version <version>" >&2; exit 2; }
  print_debian_version "$2"
  exit 0
fi

if [[ $# -ne 4 ]]; then
  echo "usage: $0 <publish-dir> <version> <debarch> <out-dir>" >&2
  exit 2
fi

publish_dir="$1"
version="$2"
debarch="$3"
out_dir="$4"
here="$(cd "$(dirname "$0")" && pwd)"
repo_root="$(cd "$here/../.." && pwd)"

[[ -d "$publish_dir" ]] || { echo "publish dir not found: $publish_dir" >&2; exit 1; }
[[ -f "$publish_dir/NovaTerminal" ]] || { echo "no NovaTerminal binary in $publish_dir" >&2; exit 1; }

debver="$(print_debian_version "$version")"
stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT

# --- dependency derivation -------------------------------------------------
# Two mechanisms, because neither alone is sufficient.
#
# 1. Linked deps: ldd every ELF and map sonames to owning packages. Catches libc6,
#    libstdc++6, libgcc-s1, libssl3 and whatever libSkiaSharp.so really pulls in.
# 2. dlopen'd deps: a hand-maintained list, because ldd CANNOT see them. Avalonia's
#    X11 backend and SkiaSharp's fontconfig lookup load these at runtime. A missing
#    entry here is precisely what smoke-test.sh exists to catch.
DLOPEN_DEPENDS=(
  libfontconfig1   # SkiaSharp font enumeration
  libx11-6         # Avalonia X11 backend
  libxrandr2       # display/DPI enumeration
  libxi6           # XInput2 pointer + keyboard
  libxcursor1      # cursor themes
  libxext6         # X extensions used by the X11 backend
  libice6          # X session management
  libsm6           # X session management
  libgl1           # GLX/OpenGL rendering path
)

derive_linked_depends() {
  local root="$1"
  local -a found=()
  local elf soname path resolved pkg

  while IFS= read -r elf; do
    while read -r soname path; do
      [[ -n "$path" && -e "$path" ]] || continue
      resolved="$(readlink -f "$path")"
      pkg="$(dpkg-query -S "$resolved" 2>/dev/null | head -1 | cut -d: -f1 || true)"
      # dpkg-query can answer with a comma-separated list; take the first name.
      pkg="${pkg%%,*}"
      [[ -n "$pkg" ]] && found+=("$pkg")
    done < <(ldd "$elf" 2>/dev/null | awk '/=>/ { print $1, $3 }')
  done < <(find "$root" -type f -exec sh -c 'file -b "$1" | grep -q "^ELF"' _ {} \; -print)

  if ((${#found[@]})); then
    printf '%s\n' "${found[@]}" | sort -u
  fi
}

# libc6 is asserted with the floor rather than taken from dpkg-query, so dpkg refuses
# the install on an older distro instead of letting the loader fail cryptically.
# ICU is an alternatives list: InvariantGlobalization is unset, so the app needs ICU,
# but the package name is version-pinned per distro (libicu70 on 22.04, libicu72 on
# Debian 12, libicu74 on 24.04). Hard-depending on one would refuse to install across
# most of the supported range.
depends="libc6 (>= 2.35), libicu74 | libicu72 | libicu71 | libicu70"
while IFS= read -r pkg; do
  [[ -z "$pkg" || "$pkg" == "libc6" ]] && continue
  depends+=", $pkg"
done < <(derive_linked_depends "$publish_dir")
for pkg in "${DLOPEN_DEPENDS[@]}"; do
  grep -q "(^|, )$pkg(,|$)" <<<"$depends" || depends+=", $pkg"
done

# --- stage the tree --------------------------------------------------------
install -d "$stage/DEBIAN"
install -d "$stage/usr/lib/novaterminal"
install -d "$stage/usr/bin"
install -d "$stage/usr/share/applications"
install -d "$stage/usr/share/man/man1"
install -d "$stage/usr/share/doc/novaterminal"

cp -a "$publish_dir/." "$stage/usr/lib/novaterminal/"
chmod 0755 "$stage/usr/lib/novaterminal/NovaTerminal"
# A .deb ships no build leftovers; the AOT publish can contain debug symbols.
find "$stage/usr/lib/novaterminal" -name '*.pdb' -delete
find "$stage/usr/lib/novaterminal" -name '*.dbg' -delete

ln -s /usr/lib/novaterminal/NovaTerminal "$stage/usr/bin/nova"
install -m 0644 "$here/nova.desktop" "$stage/usr/share/applications/novaterminal.desktop"
gzip -9nc "$here/nova.1" > "$stage/usr/share/man/man1/nova.1.gz"
chmod 0644 "$stage/usr/share/man/man1/nova.1.gz"

# --- icons -----------------------------------------------------------------
# Derived at packaging time from the one committed PNG, which stays the single
# cross-platform source of truth (same principle as packaging/macos/make-icns.sh).
icon_src="$repo_root/src/NovaTerminal.App/Assets/nova_icon.png"
if command -v magick >/dev/null 2>&1; then
  resize() { magick "$1" -resize "$2x$2" "$3"; }        # ImageMagick 7
elif command -v convert >/dev/null 2>&1; then
  resize() { convert "$1" -resize "$2x$2" "$3"; }       # ImageMagick 6 (ubuntu-22.04)
else
  resize() { return 1; }
fi

if [[ -f "$icon_src" ]]; then
  for size in 16 32 48 64 128 256; do
    dir="$stage/usr/share/icons/hicolor/${size}x${size}/apps"
    install -d "$dir"
    if resize "$icon_src" "$size" "$dir/novaterminal.png" 2>/dev/null; then
      chmod 0644 "$dir/novaterminal.png"
    else
      echo "warning: could not scale icon to ${size}x${size}; installing unscaled" >&2
      install -m 0644 "$icon_src" "$dir/novaterminal.png"
    fi
  done
else
  echo "warning: icon source not found at $icon_src; package will have no icon" >&2
fi

# --- control + docs --------------------------------------------------------
installed_kb="$(du -sk "$stage/usr" | cut -f1)"

cat > "$stage/DEBIAN/control" <<EOF
Package: novaterminal
Version: $debver
Section: utils
Priority: optional
Architecture: $debarch
Depends: $depends
Maintainer: benyblack <noreply@github.com>
Homepage: https://github.com/benyblack/NovaTerminal
Installed-Size: $installed_kb
Description: Modern terminal emulator
 NovaTerminal is a cross-platform terminal emulator with GPU-accelerated
 rendering, native SSH support, and tight shell integration.
 .
 This package installs the graphical application and the "nova" command. It does
 not register NovaTerminal as the system x-terminal-emulator; see nova(1) for how
 to do that yourself.
 .
 Updates are delivered through your package manager. The in-app updater is
 inactive for package installs, and applies only to the AppImage build.
EOF

install -m 0644 "$repo_root/LICENSE" "$stage/usr/share/doc/novaterminal/copyright"

printf 'novaterminal (%s) unstable; urgency=low\n\n  * Release %s. See %s\n\n -- %s  %s\n' \
  "$debver" "${version#v}" \
  "https://github.com/benyblack/NovaTerminal/releases/tag/${version}" \
  "benyblack <noreply@github.com>" "$(date -R)" \
  | gzip -9nc > "$stage/usr/share/doc/novaterminal/changelog.Debian.gz"
chmod 0644 "$stage/usr/share/doc/novaterminal/changelog.Debian.gz"

# --- build -----------------------------------------------------------------
# --root-owner-group so files are root-owned without fakeroot; otherwise every path
# in the package carries the CI runner's uid.
mkdir -p "$out_dir"
deb="$out_dir/novaterminal_${debver}_${debarch}.deb"
dpkg-deb --build --root-owner-group "$stage" "$deb"

echo "built $deb"
```

- [ ] **Step 5: Run the test to verify it passes**

```bash
chmod +x packaging/linux/build-deb.sh
docker run --rm -v "$PWD:/w" -w /w ubuntu:22.04 bash -c \
  'apt-get update -qq && apt-get install -y -qq file imagemagick >/dev/null && packaging/linux/test-build-deb.sh'
```

Expected: `all checks passed`.

- [ ] **Step 6: Lint the scripts**

```bash
docker run --rm -v "$PWD:/w" -w /w koalaman/shellcheck:stable \
  packaging/linux/build-deb.sh packaging/linux/test-build-deb.sh
```

Fix anything at `warning` or above. Note the tilde in `${v/-/\~}` MUST be escaped (or single-quoted): bash DOES apply tilde expansion to the replacement text of `${param/pattern/string}`, so a bare `~` becomes `$HOME` and the version turns into `0.5.0/rootbeta.1`. Verified on bash 5.1 and 5.2. (An earlier revision of this plan asserted the opposite and was wrong — the Task 2 test harness caught it on all three prerelease vectors.)

- [ ] **Step 7: Commit**

```bash
git add packaging/linux/build-deb.sh packaging/linux/test-build-deb.sh \
        packaging/linux/nova.desktop packaging/linux/nova.1
git commit -m "feat(linux): build a .deb from the AOT publish directory

dpkg-deb over a staged tree: there is nothing to compile, so debhelper and
fakeroot buy nothing. No maintainer scripts either - desktop-file-utils and
hicolor-icon-theme ship dpkg triggers that refresh the caches.

Depends is derived two ways because neither suffices alone: ldd over every
ELF for linked libraries, plus a hand-maintained list for the X11 and
fontconfig libraries Avalonia and Skia dlopen at runtime, which ldd cannot
see. libc6 asserts the 2.35 floor so dpkg refuses an old distro instead of
letting the loader fail cryptically, and ICU is an alternatives list because
its package name is version-pinned per distro.

Prerelease versions map '-' to '~' so 0.5.0~beta.1-1 sorts BELOW 0.5.0-1;
passed through unchanged, dpkg would read the last '-' as the revision
separator and sort the beta above the release."
```

---

### Task 3: `smoke-test.sh` — prove the artifacts launch

**Files:**
- Create: `packaging/linux/smoke-test.sh`

**Interfaces:**
- Consumes: Task 2's `.deb`; Task 0 finding (4) for the exact `--vt-report` arguments.
- Produces: `packaging/linux/smoke-test.sh <artifact-dir>` — exits 0 only if every artifact in that directory installs and launches. Tasks 4 and 5 call it as their gate.

- [ ] **Step 1: Write the script**

The phase ordering is the whole point and must not be rearranged — the comments say so loudly enough that a future editor cannot miss it.

```bash
#!/usr/bin/env bash
# Prove the Linux artifacts actually launch, in containers that resemble a user's
# machine rather than the build runner.
#
# Usage: smoke-test.sh <artifact-dir>
#   <artifact-dir> must contain novaterminal_*.deb and may contain *.AppImage.
#
# Requires Docker on the host. Runs two containers, and THE ORDER MATTERS - see the
# warnings below before editing.
set -euo pipefail

artifact_dir="${1:?usage: smoke-test.sh <artifact-dir>}"
artifact_dir="$(cd "$artifact_dir" && pwd)"
image="${SMOKE_IMAGE:-ubuntu:22.04}"

ls "$artifact_dir"/novaterminal_*.deb >/dev/null 2>&1 \
  || { echo "no novaterminal_*.deb in $artifact_dir" >&2; exit 1; }

echo "=== Container 1: dependency completeness (pristine $image, no X11) ==="
docker run --rm -v "$artifact_dir:/art:ro" "$image" bash -euo pipefail -c '
  export DEBIAN_FRONTEND=noninteractive

  # ---------------------------------------------------------------------------
  # PHASE A - dependency completeness. NOTHING may be installed here except the
  # .deb itself. Installing xvfb, lintian, desktop-file-utils or man-db first
  # would drag in libX11, libfontconfig and friends, SATISFYING the package own
  # missing Depends and masking the exact bug this phase exists to catch.
  # If you need a tool, put it in phase B.
  # ---------------------------------------------------------------------------
  apt-get update -qq
  apt-get install -y -qq /art/novaterminal_*.deb    # fails if Depends are incomplete
  echo "  ok: .deb installed with its declared Depends only"

  if ldd /usr/lib/novaterminal/NovaTerminal | grep "not found"; then
    echo "  FAIL: unresolved linked libraries above" >&2; exit 1
  fi
  echo "  ok: no unresolved linked libraries"

  # These are dlopen d at runtime, so ldd above cannot see them. They must resolve
  # from the package own Depends - nothing else has been installed that could
  # provide them.
  for so in libX11.so.6 libfontconfig.so.1 libXrandr.so.2 libXi.so.6 \
            libXcursor.so.1 libXext.so.6 libICE.so.6 libSM.so.6 libGL.so.1; do
    ldconfig -p | grep -q "$so" || { echo "  FAIL: $so missing (dlopen dep not in Depends)" >&2; exit 1; }
  done
  echo "  ok: every dlopen d library resolves"

  test -x /usr/lib/novaterminal/NovaTerminal || { echo "  FAIL: bundle binary not executable" >&2; exit 1; }
  test -L /usr/bin/nova                      || { echo "  FAIL: /usr/bin/nova is not a symlink" >&2; exit 1; }
  test -f /usr/share/man/man1/nova.1.gz      || { echo "  FAIL: man page not installed" >&2; exit 1; }
  echo "  ok: layout"

  # Headless CLI mode: exercises the AOT binary and the VT core with no X server.
  # Argument shape confirmed by the Task 0 spike - adjust there, not here.
  nova --vt-report > /tmp/vt-report.txt
  test -s /tmp/vt-report.txt || { echo "  FAIL: --vt-report produced no output" >&2; exit 1; }
  echo "  ok: nova --vt-report ran headless"

  # ---------------------------------------------------------------------------
  # PHASE B - validators. Tooling may be installed now: every assertion above has
  # already passed, so later installs cannot invalidate them.
  # ---------------------------------------------------------------------------
  apt-get install -y -qq lintian desktop-file-utils man-db
  desktop-file-validate /usr/share/applications/novaterminal.desktop
  echo "  ok: desktop entry validates"
  lintian --fail-on error /art/novaterminal_*.deb
  echo "  ok: lintian clean at error level"
  man nova > /dev/null
  echo "  ok: man page renders"
'

echo
echo "=== Container 2: GUI launch (xvfb permitted) ==="
docker run --rm -v "$artifact_dir:/art:ro" "$image" bash -euo pipefail -c '
  export DEBIAN_FRONTEND=noninteractive
  apt-get update -qq
  apt-get install -y -qq /art/novaterminal_*.deb
  apt-get install -y -qq xvfb xdotool

  launches() {                     # launches <label> <command...>
    local label="$1"; shift
    echo "  launching: $label"
    xvfb-run -a --server-args="-screen 0 1024x768x24" "$@" &
    local pid=$!
    for _ in $(seq 1 20); do
      sleep 1
      kill -0 "$pid" 2>/dev/null || { echo "  FAIL: $label exited early" >&2; return 1; }
      if DISPLAY=:99 xdotool search --class -- NovaTerminal >/dev/null 2>&1; then
        echo "  ok: $label mapped a window"; kill "$pid" 2>/dev/null || true; return 0
      fi
    done
    echo "  FAIL: $label never mapped a window in 20s" >&2
    kill "$pid" 2>/dev/null || true
    return 1
  }

  launches "deb install (/usr/bin/nova)" nova

  # The AppImage is tested TWICE on purpose. Ubuntu 22.04+ ships no libfuse2, so a
  # stock type-2 AppImage fails there with a confusing FUSE error - users hit the
  # mounted path, CI must cover both.
  shopt -s nullglob
  for img in /art/*.AppImage; do
    cp "$img" /tmp/nova.AppImage && chmod +x /tmp/nova.AppImage
    launches "AppImage (extracted, no FUSE)" /tmp/nova.AppImage --appimage-extract-and-run
    apt-get install -y -qq libfuse2
    launches "AppImage (FUSE-mounted)" /tmp/nova.AppImage
  done
'

echo
echo "smoke test passed"
```

- [ ] **Step 2: Run it against Task 2's synthetic package to verify it fails honestly**

A `.deb` whose binary is `/bin/true` cannot map a window, so Container 2 must fail — that is the script proving it can detect a non-launching artifact:

```bash
work=$(mktemp -d) && docker run --rm -v "$PWD:/w" -w /w -v "$work:/out" ubuntu:22.04 bash -c \
  'apt-get update -qq && apt-get install -y -qq file imagemagick >/dev/null
   mkdir -p /tmp/pub && cp /bin/true /tmp/pub/NovaTerminal
   packaging/linux/build-deb.sh /tmp/pub 0.0.1-ci amd64 /out'
chmod +x packaging/linux/smoke-test.sh
packaging/linux/smoke-test.sh "$work"; echo "exit=$?"
```

Expected: Container 1 **passes** through the layout checks, then fails at `--vt-report` (`/bin/true` produces no output) — or, if it reaches Container 2, fails at "never mapped a window". Either is correct: a non-functional artifact must not pass. Record which, then confirm `exit=1`.

- [ ] **Step 3: Verify the script's own hygiene**

```bash
docker run --rm -v "$PWD:/w" -w /w koalaman/shellcheck:stable packaging/linux/smoke-test.sh
```

Fix anything at `warning` or above.

Note what `xdotool search --class -- NovaTerminal` is doing beyond "did a window appear":
it is also the spec's `StartupWMClass` verification. `nova.desktop` declares
`StartupWMClass=NovaTerminal`, and if Avalonia sets a different WM class the search finds
nothing and the launch check fails — which is the same mismatch that would break app-menu
icon association on GNOME and KDE. If it fails here, confirm the real class before
changing the assertion:

```bash
docker run --rm -v "$PWD:/w" -w /w ubuntu:22.04 bash -c \
  'apt-get update -qq && apt-get install -y -qq /w/artifacts/linux/novaterminal_*.deb x11-utils xvfb >/dev/null
   xvfb-run -a nova & sleep 8; DISPLAY=:99 xprop -root _NET_CLIENT_LIST
   DISPLAY=:99 xprop -id $(DISPLAY=:99 xprop -root _NET_ACTIVE_WINDOW | awk "{print \$NF}") WM_CLASS'
```

Then fix `StartupWMClass` in `nova.desktop` to match what Avalonia actually sets — do not
relax the smoke assertion to paper over it.

- [ ] **Step 4: Commit**

```bash
git add packaging/linux/smoke-test.sh
git commit -m "test(linux): smoke-test the .deb and AppImage in bare containers

Nobody on the team runs Linux daily and the build runner has dev packages a
user machine will not, so 'it built' is not evidence it launches.

Two containers, and the order is load-bearing: installing xvfb or lintian
drags in libX11 and libfontconfig, which would satisfy the package own
missing Depends and mask the exact bug the test exists to catch. Phase A
therefore installs nothing but the .deb before asserting that every linked
AND dlopen'd library resolves; tooling only arrives in phase B, after those
assertions have passed.

The AppImage is launched twice - extracted and FUSE-mounted - because
Ubuntu 22.04+ ships no libfuse2 and the stock path is what users hit."
```

---

### Task 4: `ci.yml` — dry run and PR gate

**Files:**
- Modify: `.github/workflows/ci.yml` (add a `linux_packaging` job)

**Interfaces:**
- Consumes: `build-deb.sh` and `smoke-test.sh` (Tasks 2, 3); Task 0 findings.
- Produces: a `linux-packaging-dryrun` workflow artifact, and a PR gate on `packaging/linux/**`. Task 5 copies this job's `vpk pack` invocation.

x64 only: this job catches naming and layout mistakes, which are architecture-independent. The arm64 lane is exercised at release time.

- [ ] **Step 1: Add the `pull_request` path filter**

Check `ci.yml`'s existing `on:` block first:

```bash
sed -n '1,30p' .github/workflows/ci.yml
```

If `pull_request` has no `paths` filter, do **not** add one at workflow level (it would gate every job). The new job instead guards itself with an `if:` in Step 2.

- [ ] **Step 2: Append the job**

Add at the end of `.github/workflows/ci.yml`, matching the file's existing two-space indentation under `jobs:`:

```yaml
  # Linux packaging: dry run + smoke gate (#91 Linux leg). Two callers of the same
  # scripts release.yml uses, so packaging breaks land on a PR rather than on a tag.
  #
  # ubuntu-22.04, not ubuntu-latest, and that is not incidental: NativeAOT links
  # against the build machine's glibc with no runtime fallback, so the runner choice
  # IS the minimum supported distro. 22.04 sets the floor at glibc 2.35 (Ubuntu
  # 22.04+, Debian 12+, Fedora 36+). Publishing on ubuntu-latest would silently
  # exclude the most-deployed LTS - which is what it was doing before this job.
  #
  # x64 only: this lane exists to catch asset-naming and layout mistakes, which are
  # architecture-independent. release.yml covers arm64.
  linux_packaging:
    name: Linux Packaging (dry run + smoke)
    runs-on: ubuntu-22.04
    timeout-minutes: 40
    if: >-
      github.event_name == 'workflow_dispatch' ||
      github.event_name == 'push' ||
      github.event_name == 'pull_request'
    steps:
      - uses: actions/checkout@v4

      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          global-json-file: global.json

      # The AOT toolchain. Present on the x64 runner image today, installed
      # explicitly because relying on image contents is what breaks when the image
      # rolls - nightly.yml already installs clang for the same reason.
      - name: Install AOT and packaging toolchain
        run: |
          sudo apt-get update
          sudo apt-get install -y clang zlib1g-dev imagemagick file

      - name: Build Rust native
        run: |
          cd src/NovaTerminal.App/native
          cargo build --release
          mkdir -p target_linux/release
          cp target/release/librusty_pty.so target_linux/release/
          cd rusty_ssh && cargo build --release

      - name: Restore
        run: dotnet restore

      # -p:SkipCliShim=true for the same reason as release.yml's Publish AOT: the
      # shim needs a RID-less framework-dependent build, and the AOT binary
      # dispatches the CLI modes itself.
      - name: Publish AOT (linux-x64)
        env:
          SKIP_RUST_NATIVE_BUILD: "1"
        run: >-
          dotnet publish src/NovaTerminal.App/NovaTerminal.App.csproj
          -c ${{ env.CONFIGURATION }} -r linux-x64 --self-contained true
          -p:PublishAot=true -p:SkipCliShim=true
          -p:Version=0.0.1-ci -p:InformationalVersion=0.0.1-ci
          -o artifacts/publish/linux-x64

      - name: Build .deb
        run: |
          chmod +x packaging/linux/*.sh
          packaging/linux/build-deb.sh artifacts/publish/linux-x64 0.0.1-ci amd64 artifacts/linux

      - name: Test build-deb.sh
        run: |
          docker run --rm -v "$PWD:/w" -w /w ubuntu:22.04 bash -c \
            'apt-get update -qq && apt-get install -y -qq file imagemagick >/dev/null &&
             packaging/linux/test-build-deb.sh'

      # vpk pinned to 1.2.0, in lockstep with Directory.Packages.props and the two
      # other install sites in these workflows.
      - name: Install vpk
        run: dotnet tool install -g vpk --version 1.2.0

      # --channel linux-x64, NOT the platform default 'linux'. Linux publishes two
      # architectures into one GitHub release, and a Velopack feed is per-channel,
      # not per-architecture, so a shared 'linux' channel would offer an arm64
      # client an x64 package. Must match VelopackUpdateService.ResolveExplicitChannel.
      #
      # 0.0.1-ci because vpk rejects anything below 0.0.1. No --delta concerns here:
      # there is no prior release in this scratch output directory, so a full-only
      # pack is expected and correct for a dry run.
      - name: Pack AppImage (Velopack)
        run: |
          rm -f artifacts/publish/linux-x64/*.pdb
          vpk pack \
            --packId NovaTerminalApp \
            --packVersion 0.0.1-ci \
            --packDir artifacts/publish/linux-x64 \
            --mainExe NovaTerminal \
            --packTitle NovaTerminal \
            --packAuthors benyblack \
            --channel linux-x64 \
            --icon src/NovaTerminal.App/Assets/nova_icon.png \
            --exclude '.*\.pdb' \
            -o artifacts/linux

      # Mirrors release.yml's post-pack assertions, so a silent vpk behaviour change
      # surfaces here instead of on a tag.
      - name: Assert expected assets
        run: |
          cd artifacts/linux
          ls -la
          test -n "$(ls ./*.AppImage 2>/dev/null)" || { echo "vpk pack produced no .AppImage" >&2; exit 1; }
          test -f novaterminal_0.0.1~ci-1_amd64.deb || { echo "no .deb (or wrong prerelease version mapping)" >&2; exit 1; }
          test -f NovaTerminalApp-0.0.1-ci-linux-x64-full.nupkg || { echo "no linux-x64 full nupkg" >&2; exit 1; }
          test -f releases.linux-x64.json || { echo "no releases.linux-x64.json" >&2; exit 1; }
          dpkg-deb --contents novaterminal_0.0.1~ci-1_amd64.deb

      - name: Smoke test (bare containers)
        run: packaging/linux/smoke-test.sh artifacts/linux

      - name: Upload dry-run artifact
        if: always()
        uses: actions/upload-artifact@v4
        with:
          name: linux-packaging-dryrun
          retention-days: 5
          path: |
            artifacts/linux/*.AppImage
            artifacts/linux/*.deb
            artifacts/linux/*.nupkg
            artifacts/linux/releases.*.json
```

Note the `.deb` filename in the assertions is `novaterminal_0.0.1~ci-1_amd64.deb` — the `~` is the prerelease mapping from Task 2, and asserting it here is what pins that behaviour in CI.

- [ ] **Step 3: Validate the workflow parses**

```bash
python3 -c "import yaml,sys; yaml.safe_load(open('.github/workflows/ci.yml')); print('ci.yml parses')"
```

If `actionlint` is available, prefer it — it catches context and expression errors YAML parsing cannot:

```bash
docker run --rm -v "$PWD:/repo" -w /repo rhysd/actionlint:latest -color .github/workflows/ci.yml
```

- [ ] **Step 4: Run the job and confirm it goes green**

Push the branch and dispatch it, then read the result:

```bash
rtk git push -u origin feat/linux-packaging
rtk gh workflow run ci.yml --ref feat/linux-packaging
rtk gh run list --workflow ci.yml --branch feat/linux-packaging --limit 1
```

Expected: `Linux Packaging (dry run + smoke)` succeeds. If it fails in the smoke step, that is the job doing its job — fix the `Depends` list in `build-deb.sh` (Task 2's `DLOPEN_DEPENDS`) and re-run rather than weakening the assertion.

- [ ] **Step 5: Commit**

```bash
git add .github/workflows/ci.yml
git commit -m "ci(linux): add packaging dry run + smoke gate

One job serving both verification needs: workflow_dispatch for a dry run
that checks asset names and layout without cutting a tag, and PRs touching
packaging so a break lands on a PR rather than on a release.

Standalone rather than needs:[build] - the existing AOT Publish job consumes
a dotnet-build-<os> artifact that has no 22.04 equivalent.

ubuntu-22.04 rather than ubuntu-latest is the load-bearing choice: NativeAOT
links against the build machine's glibc with no runtime fallback, so the
runner IS the minimum supported distro."
```

---

### Task 5: `release.yml` — ship it

**Files:**
- Modify: `.github/workflows/release.yml` (`build_native`, `release_tests`, `publish_aot`)

**Interfaces:**
- Consumes: everything above.
- Produces: the eight release assets from the spec's artifact inventory.

- [ ] **Step 1: Move the Linux legs to `ubuntu-22.04` and add arm64**

In `build_native` (around line 97) and `release_tests` (around line 158), change:

```yaml
        os: [windows-latest, ubuntu-latest, macos-latest]
```

to:

```yaml
        # ubuntu-22.04, not ubuntu-latest: NativeAOT links against the build
        # machine's glibc with no runtime fallback, so the runner sets the minimum
        # supported distro. 22.04 = glibc 2.35 = Ubuntu 22.04+, Debian 12+,
        # Fedora 36+. ubuntu-latest (24.04, glibc 2.39) silently excluded the
        # most-deployed LTS. This deliberately differs from ci.yml's test legs,
        # which stay on ubuntu-latest - the release lane must test on its own floor.
        os: [windows-latest, ubuntu-22.04, ubuntu-22.04-arm, macos-latest]
```

In `publish_aot` (around line 226), change the `include` list to:

```yaml
        include:
          - os: windows-latest
            rid: win-x64
          - os: ubuntu-22.04
            rid: linux-x64
          - os: ubuntu-22.04-arm
            rid: linux-arm64
          - os: macos-latest
            rid: osx-arm64
```

- [ ] **Step 2: Generalise the Linux step guards**

Every `if: matrix.os == 'ubuntu-latest'` in the three jobs must become `startsWith(matrix.os, 'ubuntu')`, or the arm64 leg silently builds no Rust. Find them:

```bash
grep -n "ubuntu-latest" .github/workflows/release.yml
```

In `build_native`, the `Build Rust (Linux)` step (line ~127) becomes:

```yaml
      - name: Build Rust (Linux)
        if: startsWith(matrix.os, 'ubuntu')
        run: |
          cd src/NovaTerminal.App/native
          cargo build --release
          mkdir -p ../native/target_linux/release
          cp target/release/librusty_pty.so ../native/target_linux/release/
          cd rusty_ssh
          cargo build --release
```

`target_linux/` is an arch-agnostic staging directory and each runner builds natively for itself, so this needs no arch branch. The artifacts stay distinct because they are named `native-${{ matrix.os }}`.

- [ ] **Step 3: Add the AOT toolchain install to the Linux publish legs**

In `publish_aot`, immediately before `Restore`:

```yaml
      # The arm64 runner images are leaner than the x64 ones, so the AOT toolchain
      # is installed rather than assumed. Also brings the .deb build's tools.
      - name: Install AOT and packaging toolchain (Linux)
        if: startsWith(matrix.os, 'ubuntu')
        run: |
          sudo apt-get update
          sudo apt-get install -y clang zlib1g-dev imagemagick file
```

- [ ] **Step 4: Replace the zip with a tarball, and reorder so nothing uploads before the smoke test**

Change `Archive bundle` (line ~307) to exclude Linux, since Linux now builds its own tarball:

```yaml
      - name: Archive bundle
        if: matrix.rid == 'win-x64'
```

Same for `Upload release asset` (line ~328):

```yaml
      - name: Upload release asset
        if: matrix.rid == 'win-x64'
```

Then add the Linux lane after the existing macOS Velopack steps, so the whole Linux flow reads in order:

```yaml
      # ---- Linux: .deb + AppImage + tarball, gated on a smoke test. linux-x64 and
      # ---- linux-arm64. Nothing uploads until the artifacts have proven they launch
      # ---- in a bare ubuntu:22.04 container - the previous zip-then-pack ordering
      # ---- published assets before anything had verified them.
      - name: Build .deb (Linux)
        if: startsWith(matrix.os, 'ubuntu')
        shell: bash
        env:
          RELEASE_VERSION: ${{ needs.release_metadata.outputs.release_version }}
          RID: ${{ matrix.rid }}
        run: |
          set -euo pipefail
          chmod +x packaging/linux/*.sh
          case "$RID" in
            linux-x64)   debarch=amd64 ;;
            linux-arm64) debarch=arm64 ;;
            *) echo "unexpected rid: $RID" >&2; exit 1 ;;
          esac
          packaging/linux/build-deb.sh "artifacts/publish/$RID" "$RELEASE_VERSION" "$debarch" artifacts/linux

      - name: Archive tarball (Linux)
        if: startsWith(matrix.os, 'ubuntu')
        shell: bash
        # tar, not Compress-Archive. System.IO.Compression cannot write Unix mode
        # bits, so the zip this replaces shipped a NovaTerminal binary with no
        # executable bit and every user had to chmod +x before first launch.
        # RELEASE_TAG through env, never interpolated into script source - same
        # injection reasoning as every other run block in this file.
        env:
          RELEASE_TAG: ${{ needs.release_metadata.outputs.release_tag }}
          RID: ${{ matrix.rid }}
        run: |
          set -euo pipefail
          mkdir -p artifacts/release
          tar -czf "artifacts/release/NovaTerminal-$RID-$RELEASE_TAG.tar.gz" \
            -C "artifacts/publish/$RID" .
          tar -tvzf "artifacts/release/NovaTerminal-$RID-$RELEASE_TAG.tar.gz" \
            | grep -E '^-rwxr-xr-x.* \./NovaTerminal$' \
            || { echo "tarball lost the executable bit on NovaTerminal" >&2; exit 1; }

      - name: Install vpk (Linux)
        if: startsWith(matrix.os, 'ubuntu')
        run: dotnet tool install -g vpk --version 1.2.0

      # --channel is mandatory on both download and pack. `vpk download github`
      # resolves its channel from the runner's OS, which would fetch 'linux' - a
      # channel we never publish - so without it delta generation silently
      # degrades to full-only.
      - name: Download previous Velopack release (Linux, for delta generation)
        id: linux_download
        if: startsWith(matrix.os, 'ubuntu')
        continue-on-error: true
        shell: bash
        env:
          GH_TOKEN: ${{ secrets.GITHUB_TOKEN }}
          RID: ${{ matrix.rid }}
        run: |
          set -euo pipefail
          mkdir -p artifacts/linux
          vpk download github \
            --repoUrl https://github.com/benyblack/NovaTerminal \
            --token "$GH_TOKEN" \
            --channel "$RID" \
            --outputDir artifacts/linux

      - name: Pack AppImage (Velopack, Linux)
        if: startsWith(matrix.os, 'ubuntu')
        shell: bash
        env:
          RELEASE_VERSION: ${{ needs.release_metadata.outputs.release_version }}
          RID: ${{ matrix.rid }}
          DOWNLOAD_OUTCOME: ${{ steps.linux_download.outcome }}
        run: |
          set -euo pipefail
          ver="$RELEASE_VERSION"
          channel="$RID"

          # Same guard as the win and osx lanes: if a prior release exists on this
          # channel but the download did not succeed, packing now would publish a
          # full-only release and silently drop delta updates for everyone.
          prior="$(ls artifacts/linux/NovaTerminalApp-*-"$channel"-full.nupkg 2>/dev/null | wc -l)"
          if [ "$prior" -gt 0 ] && [ "$DOWNLOAD_OUTCOME" != "success" ]; then
            echo "A prior Velopack $channel release exists, but 'vpk download github' did not succeed (outcome: $DOWNLOAD_OUTCOME). Re-run this workflow for this tag once GitHub is reachable." >&2
            exit 1
          fi

          # Re-run idempotency: vpk hard-fails if the full nupkg for THIS version is
          # already in the output directory (as it would be after a partial re-run).
          rm -f "artifacts/linux/NovaTerminalApp-$ver-$channel-full.nupkg"

          # Strip debug symbols at the source as well as excluding them: the AOT
          # .pdb rivals the binary's own size.
          rm -f "artifacts/publish/$RID"/*.pdb

          vpk pack \
            --packId NovaTerminalApp \
            --packVersion "$ver" \
            --packDir "artifacts/publish/$RID" \
            --mainExe NovaTerminal \
            --packTitle NovaTerminal \
            --packAuthors benyblack \
            --channel "$channel" \
            --icon src/NovaTerminal.App/Assets/nova_icon.png \
            --exclude '.*\.pdb' \
            -o artifacts/linux

          test -n "$(ls artifacts/linux/*.AppImage 2>/dev/null)" \
            || { echo "vpk pack produced no .AppImage" >&2; exit 1; }
          test -f "artifacts/linux/NovaTerminalApp-$ver-$channel-full.nupkg" \
            || { echo "vpk pack produced no NovaTerminalApp-$ver-$channel-full.nupkg - the $channel update feed would be unusable." >&2; exit 1; }
          test -f "artifacts/linux/releases.$channel.json" \
            || { echo "vpk pack produced no releases.$channel.json" >&2; exit 1; }
          if [ "$prior" -gt 0 ] && [ ! -f "artifacts/linux/NovaTerminalApp-$ver-$channel-delta.nupkg" ]; then
            echo "A prior Velopack $channel release exists but vpk pack produced no delta nupkg - delta generation silently failed." >&2
            exit 1
          fi

          # Rename to read consistently on the release page, as the osx lane does.
          appimage="$(ls artifacts/linux/*.AppImage | head -1)"
          mv "$appimage" "artifacts/release/NovaTerminal-$RID-${{ needs.release_metadata.outputs.release_tag }}.AppImage"
          cp artifacts/linux/*.deb artifacts/release/

      # THE GATE. Everything above produced artifacts; nothing has been published.
      - name: Smoke test (Linux, bare containers)
        if: startsWith(matrix.os, 'ubuntu')
        run: packaging/linux/smoke-test.sh artifacts/release

      - name: Upload Linux assets and update feed
        if: startsWith(matrix.os, 'ubuntu')
        uses: softprops/action-gh-release@3bb12739c298aeb8a4eeaf626c5b8d85266b0e65 # v2
        with:
          tag_name: ${{ needs.release_metadata.outputs.release_tag }}
          files: |
            artifacts/release/NovaTerminal-${{ matrix.rid }}-${{ needs.release_metadata.outputs.release_tag }}.AppImage
            artifacts/release/NovaTerminal-${{ matrix.rid }}-${{ needs.release_metadata.outputs.release_tag }}.tar.gz
            artifacts/release/*.deb
            artifacts/linux/NovaTerminalApp-*-${{ matrix.rid }}-full.nupkg
            artifacts/linux/NovaTerminalApp-*-${{ matrix.rid }}-delta.nupkg
            artifacts/linux/releases.${{ matrix.rid }}.json
```

The channel name and the RID are intentionally identical (`linux-x64`, `linux-arm64`), so `--channel "$RID"` is correct by construction rather than by coincidence — and it must keep matching `VelopackUpdateService.ResolveExplicitChannel` from Task 1.

- [ ] **Step 5: Validate the workflow**

```bash
python3 -c "import yaml; yaml.safe_load(open('.github/workflows/release.yml')); print('release.yml parses')"
docker run --rm -v "$PWD:/repo" -w /repo rhysd/actionlint:latest -color .github/workflows/release.yml
```

- [ ] **Step 6: Confirm nothing references the retired zip**

```bash
grep -rn "linux-x64.*\.zip\|NovaTerminal-linux" --include='*.md' --include='*.yml' --include='*.yaml' . | grep -v '\.git/'
```

Update any hit to the new `.tar.gz` / `.AppImage` names. Expect hits in `README.md` — Task 6 rewrites that section, so note them and move on.

- [ ] **Step 7: Commit**

```bash
git add .github/workflows/release.yml
git commit -m "feat(release): publish Linux AppImage + .deb for x64 and arm64

Replaces the single portable zip, which was broken twice over:
Compress-Archive cannot write Unix mode bits (so the binary shipped
without its executable bit), and publishing on ubuntu-latest pinned a
glibc 2.39 floor that excluded Ubuntu 22.04 and Debian 12 outright.

- Linux legs move to ubuntu-22.04 / ubuntu-22.04-arm (glibc 2.35 floor)
- ubuntu-latest step guards become startsWith(matrix.os, 'ubuntu')
- tar replaces Compress-Archive, and asserts the exec bit survived
- vpk pack per architecture on channels linux-x64 / linux-arm64, since a
  Velopack feed is per-channel and one shared 'linux' channel would offer
  arm64 clients an x64 package
- uploads move AFTER a bare-container smoke test; the previous ordering
  published the zip before anything had verified it"
```

---

### Task 6: Documentation

**Files:**
- Create: `packaging/linux/README.md`
- Modify: `README.md` (Linux install section)

**Interfaces:**
- Consumes: the final asset names from Task 5.
- Produces: nothing consumed by later tasks.

- [ ] **Step 1: Write `packaging/linux/README.md`**

Mirror `packaging/macos/README.md`'s structure (read it first: `cat packaging/macos/README.md`).

````markdown
# Linux packaging

This folder holds the Linux packaging pieces used by the release workflow:

- `build-deb.sh` — builds `novaterminal_<ver>_<arch>.deb` from a NativeAOT publish
  directory. `dpkg-deb` over a staged tree; nothing is compiled here.
- `smoke-test.sh` — installs and launches the artifacts in bare `ubuntu:22.04`
  containers. The release gate.
- `test-build-deb.sh` — unit-ish tests for `build-deb.sh` (version mapping, layout,
  control fields), needing no real NovaTerminal build.
- `nova.desktop`, `nova.1` — the desktop entry and man page source.

Icons are derived at packaging time from `src/NovaTerminal.App/Assets/nova_icon.png`,
which stays the single cross-platform source of truth. No scaled PNGs are committed.

The AppImage and update feed are built by [Velopack](https://velopack.io) (`vpk pack`)
in `.github/workflows/release.yml`, mirroring the Windows and macOS lanes.

| Release asset | Produced by | Notes |
|---|---|---|
| `NovaTerminal-linux-<arch>-<tag>.AppImage` | `vpk pack` (renamed) | Portable, self-updating |
| `novaterminal_<ver>_<debarch>.deb` | `build-deb.sh` | System install; updates via your package manager |
| `NovaTerminal-linux-<arch>-<tag>.tar.gz` | `tar` in `release.yml` | Portable, no integration |
| `NovaTerminalApp-<ver>-linux-<arch>-full.nupkg` / `-delta.nupkg` | `vpk pack` | The update feed the in-app updater consumes |
| `releases.linux-<arch>.json` | `vpk pack` | Feed index resolved by `VelopackUpdateService` |

## Facts worth knowing

- **The glibc floor is 2.35**, set by publishing on `ubuntu-22.04`. NativeAOT links
  against the build machine's glibc with no runtime fallback, so the runner choice
  *is* the minimum supported distro: Ubuntu 22.04+, Debian 12+, Fedora 36+, current
  rolling distros. Debian 11 and RHEL 8/9 are not supported. Moving to
  `ubuntu-latest` would silently drop Ubuntu 22.04.
- **Channels are `linux-x64` and `linux-arm64`, not `linux`.** A Velopack feed is
  per-channel, not per-architecture, and both architectures publish into one GitHub
  release — a shared `linux` channel would offer arm64 clients an x64 package. The
  names must stay in sync with
  `VelopackUpdateService.ResolveExplicitChannel`.
- **`vpk download github` resolves its channel from the runner's OS** unless
  `--channel` is passed. Both Linux lanes pass it explicitly; without it, delta
  generation silently degrades to full-only.
- **User data is never touched** by updates or uninstall. It lives in
  `~/.local/share/NovaTerminal` via `AppPaths`.
- **No maintainer scripts.** `desktop-file-utils` and `hicolor-icon-theme` ship dpkg
  triggers that refresh the desktop and icon caches, so the package needs no
  `postinst`/`prerm`.

## Known traps

- **AppImage needs FUSE.** Ubuntu 22.04+ ships no `libfuse2`, so a stock AppImage
  fails with a confusing FUSE error. Either `sudo apt install libfuse2`, or run it
  as `./NovaTerminal-*.AppImage --appimage-extract-and-run`. CI tests both paths.
- **The AppImage self-updates in place**, so it must live somewhere the user can
  write. Parked in `/opt` or `/usr/local/bin` it cannot update itself. `~/Applications`
  is the right home.
- **A `.deb` install does not auto-update.** It is not a Velopack install, so
  `IUpdateService.IsSupported` is false and the in-app updater stays silent by
  design. Update through your package manager or reinstall a newer `.deb`.
- **NovaTerminal is not registered as `x-terminal-emulator`.** That is deliberate:
  callers pass `-e <command>`, which the app does not yet implement, so registering
  would make "Open in Terminal" silently discard the command. To opt in anyway:

  ```sh
  sudo update-alternatives --install /usr/bin/x-terminal-emulator \
      x-terminal-emulator /usr/bin/nova 40
  ```

## Dry run without cutting a release

The `Linux Packaging (dry run + smoke)` job in `.github/workflows/ci.yml` runs the
same `build-deb.sh` and `vpk pack` at version `0.0.1-ci`, asserts every asset name,
runs the smoke test, and uploads `linux-packaging-dryrun`. Trigger it with
`gh workflow run ci.yml`, or by touching `packaging/linux/**` in a PR.

Locally, everything but `vpk pack` runs in Docker:

```sh
docker run --rm -v "$PWD:/w" -w /w ubuntu:22.04 bash -c \
  'apt-get update -qq && apt-get install -y -qq file imagemagick >/dev/null &&
   packaging/linux/test-build-deb.sh'
```
````

- [ ] **Step 2: Rewrite the README's Linux install section**

Find it first:

```bash
grep -n -i "linux" README.md | head -20
```

Replace the Linux download instructions with the following, adjusting only the heading level to match the surrounding file:

````markdown
### Linux

Requires **glibc 2.35 or newer** — Ubuntu 22.04+, Debian 12+, Fedora 36+, or a current
rolling distro. Debian 11 and RHEL 8/9 are not supported.

**AppImage** (recommended — updates itself):

```sh
# Replace <tag> with the latest release, and x64 with arm64 on ARM machines.
curl -LO https://github.com/benyblack/NovaTerminal/releases/download/<tag>/NovaTerminal-linux-x64-<tag>.AppImage
chmod +x NovaTerminal-linux-x64-<tag>.AppImage
mkdir -p ~/Applications && mv NovaTerminal-linux-x64-<tag>.AppImage ~/Applications/
~/Applications/NovaTerminal-linux-x64-<tag>.AppImage
```

Keep it somewhere you can write, such as `~/Applications` — the app updates itself by
rewriting the AppImage, which it cannot do from a root-owned path like `/opt`.

Ubuntu 22.04 and later ship no `libfuse2`, which AppImages need. Either install it
(`sudo apt install libfuse2`) or run with `--appimage-extract-and-run`.

**Debian / Ubuntu package** (system integration; update via your package manager):

```sh
curl -LO https://github.com/benyblack/NovaTerminal/releases/download/<tag>/novaterminal_<version>_amd64.deb
sudo apt install ./novaterminal_<version>_amd64.deb
nova
```

Installs `nova` on your PATH, an app-menu entry, and `man nova`. The in-app updater is
inactive for package installs by design.

**Portable tarball** (no integration):

```sh
curl -LO https://github.com/benyblack/NovaTerminal/releases/download/<tag>/NovaTerminal-linux-x64-<tag>.tar.gz
tar -xzf NovaTerminal-linux-x64-<tag>.tar.gz && ./NovaTerminal
```

NovaTerminal is not registered as your default terminal. To do that yourself after
installing the `.deb`, see `man nova`.
````

- [ ] **Step 3: Verify no stale asset names remain**

```bash
grep -rn "linux-x64.*\.zip" --include='*.md' . | grep -v '\.git/'
```

Expected: no output.

- [ ] **Step 4: Commit**

```bash
git add packaging/linux/README.md README.md
git commit -m "docs(linux): document the AppImage, .deb, and their traps

Mirrors packaging/macos/README.md. Records the things that will otherwise
be rediscovered painfully: the glibc floor is set by the runner and not by
choice, channels are per-architecture because a Velopack feed is not,
Ubuntu 22.04+ ships no libfuse2 so a stock AppImage fails confusingly, an
AppImage in a root-owned path cannot self-update, and a .deb install
deliberately never sees update UI."
```

---

### Task 7: File the deferred follow-ups

**Files:** none — this task creates GitHub issues.

**Interfaces:**
- Consumes: the spec's "Out of scope" section.
- Produces: issue numbers to reference from `packaging/linux/README.md` if desired.

- [ ] **Step 1: File the apt repository issue**

```bash
rtk gh issue create --title "[linux] Signed APT repository on GitHub Pages" --body 'Deferred from the Linux packaging project (`docs/superpowers/specs/2026-09-02-linux-packaging-design.md`).

## Goal
`.deb` users get NovaTerminal in normal `apt upgrade` runs instead of manually reinstalling a newer `.deb`.

## Scope
- Publish a signed apt repo to the existing GitHub Pages site (`pages.yml`).
- GPG release signing key, held as a repo secret; document the rotation story, since users pin the keyring.
- Generate `dists/` + `pool/` metadata on every tag (reprepro or aptly).
- Both architectures (amd64, arm64).

## Why deferred
An apt feed is a long-term commitment: once users add it, breaking it breaks their package manager. The plain `.deb` is a complete distribution in the meantime.

## Acceptance
`apt update && apt install novaterminal` works from a clean machine after adding the keyring and source line, and a subsequent release is picked up by `apt upgrade`.'
```

- [ ] **Step 2: File the terminal-emulator contract issue**

```bash
rtk gh issue create --title "[app] nova -e <cmd> and --working-directory, then register as x-terminal-emulator" --body 'Deferred from the Linux packaging project (`docs/superpowers/specs/2026-09-02-linux-packaging-design.md`).

## Why this is app work, not packaging
`x-terminal-emulator` is a contract, not a label: callers invoke `x-terminal-emulator -e <command>`, usually with a working directory. `Program.Main` implements four CLI modes (`--vt-report`, `--ssh-askpass`, `--replay`, `backup`) and passes everything else to `StartWithClassicDesktopLifetime(args)`, which ignores unrecognised arguments. Registering today would make a file manager'"'"'s "Open in Terminal" launch Nova in `$HOME` and silently discard the command — worse than not registering.

## Scope
- `nova -e <cmd> [args...]` — run one command in a new session. Settle: exit when it exits? hold the pane open on nonzero exit? `shell -c` or direct exec?
- `nova --working-directory <dir>`.
- Tests for argument parsing and spawn behaviour.
- Then, in `packaging/linux/build-deb.sh`, add `postinst`/`prerm` maintainer scripts registering `update-alternatives --install /usr/bin/x-terminal-emulator x-terminal-emulator /usr/bin/nova 40`.

This is cross-platform app behaviour and wants its own brainstorm before implementation.

## Acceptance
"Open in Terminal" from a file manager opens Nova in the right directory, and `x-terminal-emulator -e ls` runs `ls` in a Nova session.'
```

- [ ] **Step 3: File the remaining formats issue**

```bash
rtk gh issue create --title "[linux] Additional package formats: Flatpak/Flathub, AUR, RPM, Snap" --body 'Deferred from the Linux packaging project (`docs/superpowers/specs/2026-09-02-linux-packaging-design.md`), which ships AppImage + `.deb` + tarball.

## Flatpak / Flathub
Where most desktop Linux users look, and the hardest fit for a terminal emulator: it needs `--filesystem=host` and host-spawn access (`--talk-name=org.freedesktop.Flatpak`), which reviewers push back on. Also needs AppStream `metainfo.xml` with screenshots, a manifest in a separate flathub repo, and its own release cadence and review latency.

## AUR
Cheapest of these: a `PKGBUILD` repackaging the tarball, but it needs a maintainer who watches releases.

## RPM / Snap
`rpmbuild` from the same staged tree `build-deb.sh` produces would be mostly mechanical. Snap needs its own confinement story, with the same host-access problem as Flatpak.

## Also considered and dropped
GPG-signed artifacts and a `SHA256SUMS` file; musl/Alpine; 32-bit.'
```

- [ ] **Step 4: Record the issue numbers in the spec**

Append to the spec's "Out of scope" section, replacing the bullet list's trailing text with the real numbers:

```markdown
Tracked as: #<apt> (APT repository), #<term> (terminal-emulator contract),
#<formats> (Flatpak/AUR/RPM/Snap and the dropped items).
```

- [ ] **Step 5: Commit**

```bash
git add docs/superpowers/specs/2026-09-02-linux-packaging-design.md
git commit -m "docs(linux): link the deferred packaging follow-ups to their issues"
```

---

## Verification before opening the PR

- [ ] **The `.deb` and AppImage smoke test passes in CI** — `Linux Packaging (dry run + smoke)` green on the branch.
- [ ] **Windows and macOS are untouched.** Confirm no `win-x64` or `osx-arm64` step changed behaviour:
  ```bash
  git diff main -- .github/workflows/release.yml | grep -E '^[-+].*(win-x64|osx|macos|winget)'
  ```
  Expected: only the `Archive bundle` / `Upload release asset` guard changes (`matrix.rid != 'osx-arm64'` → `matrix.rid == 'win-x64'`), which is behaviour-preserving for both — Linux is what was removed from that path.
- [ ] **The full gating unit lane passes**, run per project rather than solution-wide (a whole-solution run takes ~20–30 minutes):
  ```bash
  for p in VT Rendering Architecture Platform McpServer; do
    ./scripts/build.sh test "tests/NovaTerminal.$p.Tests" \
      --filter "Category!=Replay&Category!=RenderMetrics&Category!=PtySmoke&Category!=Stress&Category!=GoldenSharedPng"
  done
  ```
- [ ] **`App.Tests` passes** with the hang guard, logged to a file, never concurrently with another run:
  ```bash
  ./scripts/build.sh test tests/NovaTerminal.App.Tests --blame-hang-timeout 5m > D:/tmp/apptests.log 2>&1
  grep -c '\[FAIL\]' D:/tmp/apptests.log
  ```
- [ ] **A real tag produces all eight assets.** Cut a prerelease tag (e.g. `v0.4.0-rc.1`) and confirm the release page carries, per architecture: `.AppImage`, `.deb`, `.tar.gz`, full nupkg, delta nupkg (second release onward), and `releases.linux-<arch>.json`.
- [ ] **In-app update N → N+1 works on Linux.** Requires two releases on a channel: install the older AppImage, launch, and confirm it offers and applies the newer one. Confirm an arm64 client is never offered an x64 package.
- [ ] **A `.deb`-installed app surfaces no updater UI.**

---

## Notes for the executor

- **You are on branch `feat/linux-packaging`, worktree `D:\tmp\nova2-linux-packaging`.** The top-level `D:\projects\nova2` checkout is shared with other sessions that commit into it and switch its branch. Do not commit there; verify HEAD before any commit here.
- **Never run raw `dotnet build`.** Use `scripts/build.ps1` / `scripts/build.sh`. A raw invocation spawns MSBuild daemons that inherit stdout and hang the harness — the hang looks like it is stuck in `BuildCliShim`, which is a symptom, not the cause. `dotnet publish` inside CI is the documented exception; workflow-level env already disables node reuse.
- **Prefix shell commands with `rtk`** per `CLAUDE.md`, including inside `&&` chains.
- **Do not weaken a failing smoke assertion.** If Container 1 reports a missing library, the fix is a new entry in `DLOPEN_DEPENDS` in `build-deb.sh`. That assertion failing is the system working.
- **Task 0 gates Task 5's arm64 rows.** If `vpk` cannot pack for linux-arm64 by either route, stop and ask the user rather than quietly shipping arm64 without an AppImage.
