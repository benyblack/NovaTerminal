#!/usr/bin/env bash
# Prove the AUR package builds, installs and launches on a real Arch system.
#
# Usage: smoke-test.sh <aur-dir>
#   <aur-dir> must contain the PKGBUILD written by build-arch.sh.
#
# Requires Docker on the host. Runs three containers, and THE SPLIT MATTERS - see
# the phase rules below before editing.
#
# WHY THIS EXISTS AT ALL, given packaging/linux/smoke-test.sh already gates the .deb:
# that gate runs in ubuntu:22.04, which is the FLOOR - glibc 2.35, libicu70. Arch is
# the opposite end of the supported range, and the differences are not cosmetic. The
# app dlopen's ICU, and Arch ships icu 78 where the Debian relation names
# libicu74|libicu72|libicu71|libicu70; nothing in the Debian lane can tell you
# whether .NET resolves an ICU that far ahead of the floor. (It does - verified on
# Arch with icu 78.3 on 2026-09-14, libicuuc/libicui18n/libicudata all mapped into
# the running process - which is a fact with a shelf life, so it is gated here
# rather than written down and trusted.)
set -euo pipefail

aur_dir="${1:?usage: smoke-test.sh <aur-dir>}"
aur_dir="$(cd "$aur_dir" && pwd)"
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
build_image="${SMOKE_BUILD_IMAGE:-archlinux:base-devel}"
test_image="${SMOKE_TEST_IMAGE:-archlinux:base}"

[[ -f "$aur_dir/PKGBUILD" ]] || { echo "no PKGBUILD in $aur_dir" >&2; exit 1; }

# The dlopen gate is DERIVED from build-deb.sh's table via build-arch.sh, not
# hand-maintained here - the same reasoning that made the Debian smoke test stop
# keeping its own copy. Two lists means a library missing from both is invisible to
# both, which is how libsecret/libglib survived nine review rounds there.
dlopen_sonames="$("$here/../linux/build-deb.sh" --print-dlopen-sonames)"
arch_depends="$("$here/build-arch.sh" --print-depends)"
dlopen_count="$(grep -c . <<<"$dlopen_sonames" || true)"
depends_count="$(grep -c . <<<"$arch_depends" || true)"

# Anti-vacuity, host side: an empty table would make the whole gate pass having
# asserted nothing - the exact defect class this file exists to close, one level up.
(( dlopen_count > 0 )) \
  || { echo "build-deb.sh --print-dlopen-sonames produced nothing; the dlopen gate would be vacuous" >&2; exit 1; }
(( depends_count > 0 )) \
  || { echo "build-arch.sh --print-depends produced nothing; the depends gate would be vacuous" >&2; exit 1; }
while IFS= read -r _so; do
  [[ "$_so" =~ ^lib[A-Za-z0-9_.+-]*\.so(\.[0-9]+)*$ ]] \
    || { echo "not a soname in the dlopen table: '$_so'" >&2; exit 1; }
done <<<"$dlopen_sonames"
echo "dlopen gate will assert $dlopen_count soname(s); depends gate will assert $depends_count package(s)"

work="$(mktemp -d)"
# Cleanup must never decide the exit status or shout over the real result. A
# container that leaves root-owned or stranger-owned files behind is a bug worth
# fixing at its source (see the uid mapping in container 0), not one worth turning
# every run red for - and when it did happen, the trap's "Operation not permitted"
# was the only thing printed, burying the actual failure.
trap 'rm -rf "$work" 2>/dev/null || echo "note: could not remove $work (leftover container-owned files)" >&2' EXIT
cp "$aur_dir/PKGBUILD" "$work/"
[[ -f "$aur_dir/.SRCINFO" ]] && cp "$aur_dir/.SRCINFO" "$work/"

echo
echo "=== Container 0: makepkg ($build_image) ==="
# makepkg REFUSES TO RUN AS ROOT and will not be talked out of it, so the container
# needs an unprivileged user with passwordless sudo - the standard Arch CI shape.
# --nodeps is deliberate: building this package compiles nothing, so the runtime
# depends are not needed HERE. Whether they are installable and sufficient is
# exactly what container 1 tests, in a pristine image, which is a stronger check
# than makepkg's own dep resolution would have been.
#
# THE BUILD USER TAKES THE CALLER UID, and that is not a detail. /work is a bind
# mount, so every uid the container writes is the uid on the host. A container-local
# builder (uid 1000) would leave this directory owned by a stranger - and `mktemp -d`
# is mode 700, so the caller loses ALL access to its own temp dir: the `ls` below
# fails, `set -e` kills the script with no message, and the cleanup trap fails too
# with "Operation not permitted".
#
# That is exactly how the first CI run of this lane failed, having passed locally -
# because locally it was run under sudo, where root reads and removes regardless of
# ownership. A uid-mapping bug is invisible to every root-run test.
docker run --rm -v "$work:/work" \
  -e HOST_UID="$(id -u)" -e HOST_GID="$(id -g)" \
  "$build_image" bash -euo pipefail -c '
  pacman -Syu --noconfirm --needed imagemagick >/dev/null
  uid="${HOST_UID:-1000}"
  gid="${HOST_GID:-1000}"
  # Running as root on the host is the one case that cannot be mirrored - uid 0
  # already exists and makepkg would refuse anyway. Any uid works there, because
  # root can read and clean up whatever the container leaves behind.
  if [ "$uid" = "0" ]; then uid=1000; gid=1000; fi
  groupadd -g "$gid" builder 2>/dev/null || true
  useradd -m -u "$uid" -g "$gid" builder 2>/dev/null || useradd -m -u "$uid" builder
  chown -R "$uid":"$gid" /work
  su builder -c "cd /work && makepkg -f --nodeps --noconfirm"
  ls -l /work/*.pkg.tar.zst
'
# `|| true` on the pipeline: without it a failed/unreadable `ls` takes the whole
# script down through `set -e` BEFORE the diagnostic below can print, which is how
# the uid bug above presented - a bare "Operation not permitted" from the exit trap
# and nothing about what actually went wrong.
pkg="$(ls "$work"/*.pkg.tar.zst 2>/dev/null | head -1 || true)"
[[ -n "$pkg" ]] \
  || { echo "makepkg produced no package in $work, or it is not readable by $(id -un) (uid $(id -u)) - check the uid mapping above" >&2; exit 1; }
echo "  ok: built $(basename "$pkg")"

echo
echo "=== Container 1: dependency completeness (pristine $test_image, no X11) ==="
docker run --rm -v "$work:/art:ro" \
  -e DLOPEN_SONAMES="$dlopen_sonames" \
  -e DLOPEN_COUNT="$dlopen_count" \
  -e ARCH_DEPENDS="$arch_depends" \
  -e DEPENDS_COUNT="$depends_count" \
  "$test_image" bash -euo pipefail -c '
  # ---------------------------------------------------------------------------
  # PHASE A - dependency completeness. NOTHING may be installed here except the
  # package and what pacman pulls in to satisfy its OWN depends. Installing xorg,
  # namcap or man-db first would drag in libx11, fontconfig and friends, satisfying
  # the package own missing dependencies and masking the exact bug this phase
  # exists to catch. If you need a tool, put it in phase B.
  # ---------------------------------------------------------------------------
  # The official archlinux images ship NoExtract lines in /etc/pacman.conf that
  # drop /usr/share/man, /usr/share/doc and most locales at unpack time to keep the
  # image small. A real Arch machine has no such lines, so leaving them in place
  # makes our own man-page assertion below fail on a CORRECTLY BUILT package - a
  # false gate, and precisely the trap packaging/linux/smoke-test.sh documents for
  # the ubuntu:22.04 excludes file at /etc/dpkg/dpkg.cfg.d/excludes. Not
  # hypothetical: the first run of this script failed on exactly that, against a
  # package whose own bsdtar listing showed usr/share/man/man1/nova.1.gz present.
  # Removing them makes the container faithful to the machine being tested for.
  # Do not "clean this up" - it is load-bearing, not a leftover.
  sed -i "/^[[:space:]]*NoExtract/d" /etc/pacman.conf
  pacman -Syu --noconfirm >/dev/null
  pacman -U --noconfirm /art/*.pkg.tar.zst      # fails if a depends name is wrong
  echo "  ok: package installed with its declared depends only"

  # Every ELF under the bundle, not just NovaTerminal - the only place an undeclared
  # LINKED dependency can be caught honestly, because container 2 installs Xvfb
  # before launching anything and would provide those libraries regardless.
  #
  # ELF discovery is by MAGIC BYTES, not by `file`: `file` is not installed here and
  # must not be, per the phase A rule. A `file`-based loop would print "not found"
  # per candidate, match nothing, run the body zero times, and still print ok - a
  # check that cannot fail. ELF starts with 7f 45 4c 46.
  #
  # ldd is captured before grepping: ldd EXITS 0 while reporting "=> not found", so
  # the grep is the only thing that can catch an unresolved soname, and under
  # pipefail a piped form would conflate the two failures.
  # ldd prints "you do not have execution permission" for each bundled .so here.
  # That is EXPECTED and must stay: package() normalises the bundle to 0644 because
  # the dynamic loader only ever mmaps a shared library, and SkiaSharp/HarfBuzzSharp
  # ship 0744 upstream. The warning is cosmetic; ldd still resolves and reports.
  elf_count=0
  main_checked=0
  while IFS= read -r f; do
    [ "$(head -c4 "$f" | od -An -tx1 | tr -d " \n")" = "7f454c46" ] || continue
    elf_count=$((elf_count + 1))
    [ "$f" = "/usr/lib/novaterminal/NovaTerminal" ] && main_checked=1
    ldd_out="$(ldd "$f")" || { echo "  FAIL: ldd failed on $f" >&2; exit 1; }
    if grep "not found" <<<"$ldd_out"; then
      echo "  FAIL: unresolved linked libraries in $f above" >&2; exit 1
    fi
  done < <(find /usr/lib/novaterminal -type f)

  so_total="$(find /usr/lib/novaterminal -type f -name "*.so" | wc -l)"
  expected=$((so_total + 1))
  [ "$main_checked" = 1 ] \
    || { echo "  FAIL: ELF discovery never reached the main binary - the check did not run" >&2; exit 1; }
  [ "$elf_count" -ge "$expected" ] \
    || { echo "  FAIL: checked $elf_count ELF(s) but the bundle has $so_total *.so plus the main binary" >&2; exit 1; }
  echo "  ok: no unresolved linked libraries in any of the $elf_count bundled ELFs"

  # dlopen ed at runtime, so ldd above cannot see them. They must resolve from the
  # package own depends - nothing else has been installed that could provide them.
  [ -n "${DLOPEN_SONAMES:-}" ] \
    || { echo "  FAIL: DLOPEN_SONAMES is empty - the dlopen gate would assert nothing" >&2; exit 1; }
  soname_count=0
  while IFS= read -r so; do
    [ -n "$so" ] || continue
    soname_count=$((soname_count + 1))
    ldconfig -p | grep -q -- "$so" \
      || { echo "  FAIL: $so missing (dlopen dep not in depends)" >&2; exit 1; }
  done <<<"$DLOPEN_SONAMES"
  # Transport anti-vacuity: the host counted the table itself, so a truncated env
  # var cannot silently shrink the gate.
  [ "$soname_count" -eq "${DLOPEN_COUNT:-0}" ] \
    || { echo "  FAIL: asserted $soname_count soname(s) but the table has ${DLOPEN_COUNT:-0}" >&2; exit 1; }
  echo "  ok: all $soname_count dlopen loaded libraries resolve"

  # The half that makes the table self-checking rather than merely shared: every
  # Arch package build-arch.sh derived must appear in the INSTALLED package depends.
  # The assertion above proves each soname resolves; it cannot prove this package is
  # why - a soname could resolve because some other dependency dragged it in
  # transitively. Together: a library cannot be gated without being depended on, nor
  # depended on without being gated.
  installed_depends="$(pacman -Qi novaterminal-bin | sed -n "s/^Depends On *: *//p" | tr -s " ")"
  [ -n "$installed_depends" ] \
    || { echo "  FAIL: pacman -Qi returned no Depends On - the coverage check would prove nothing" >&2; exit 1; }
  dep_checked=0
  while IFS= read -r dep; do
    [ -n "$dep" ] || continue
    dep_checked=$((dep_checked + 1))
    # Word-boundary match: a bare grep -F for "libx11" also matches "libx11-xcb",
    # which would let a wrong package name pass because a similarly named one is
    # present.
    grep -qE "(^| )${dep}( |$)" <<<"$installed_depends" \
      || { echo "  FAIL: installed depends does not cover \"$dep\"" >&2
           echo "        got: $installed_depends" >&2; exit 1; }
  done <<<"$ARCH_DEPENDS"
  [ "$dep_checked" -eq "${DEPENDS_COUNT:-0}" ] \
    || { echo "  FAIL: checked $dep_checked depend(s) but build-arch.sh declares ${DEPENDS_COUNT:-0}" >&2; exit 1; }
  echo "  ok: installed depends covers all $dep_checked derived package(s)"

  # Layout. The icon assertion is the acceptance criterion "NovaTerminal appears in
  # the app menu with its icon" - an iconless package is not degraded, it is broken,
  # so this counts the buckets rather than checking that some icon exists.
  test -x /usr/lib/novaterminal/NovaTerminal || { echo "  FAIL: bundle binary not executable" >&2; exit 1; }
  test -L /usr/bin/nova                      || { echo "  FAIL: /usr/bin/nova is not a symlink" >&2; exit 1; }
  test -f /usr/share/man/man1/nova.1.gz      || { echo "  FAIL: man page not installed (or not gzipped by zipman)" >&2; exit 1; }
  test -f /usr/share/licenses/novaterminal-bin/LICENSE || { echo "  FAIL: licence not installed" >&2; exit 1; }
  test -f /usr/share/applications/novaterminal.desktop || { echo "  FAIL: desktop entry not installed" >&2; exit 1; }
  icon_count=0
  for s in 16 32 48 64 128 256; do
    test -f "/usr/share/icons/hicolor/${s}x${s}/apps/novaterminal.png" || {
      echo "  FAIL: missing ${s}x${s} icon" >&2; exit 1; }
    icon_count=$((icon_count + 1))
  done
  [ "$icon_count" -eq 6 ] || { echo "  FAIL: expected 6 icon sizes, counted $icon_count" >&2; exit 1; }
  echo "  ok: layout, including all 6 hicolor icon sizes"

  # Headless CLI mode: exercises the AOT binary and the VT core with no X server,
  # and - the reason it matters most on Arch - forces .NET globalization to resolve
  # ICU. A libicu too far ahead of the floor fails HERE, loudly, rather than on a
  # users machine.
  nova --vt-report > /tmp/vt-report.txt
  test -s /tmp/vt-report.txt || { echo "  FAIL: --vt-report produced no output" >&2; exit 1; }
  grep -q "NovaTerminal VT Report" /tmp/vt-report.txt \
    || { echo "  FAIL: --vt-report output is not a VT report" >&2; exit 1; }
  echo "  ok: nova --vt-report ran headless (ICU resolved)"

  # ---------------------------------------------------------------------------
  # PHASE B - validators. Tooling may be installed now: every assertion above has
  # already passed, so later installs cannot invalidate them.
  # ---------------------------------------------------------------------------
  pacman -S --noconfirm --needed desktop-file-utils man-db namcap >/dev/null
  desktop-file-validate /usr/share/applications/novaterminal.desktop
  echo "  ok: desktop entry validates"
  man nova > /dev/null
  echo "  ok: man page renders"
  # namcap, split by severity - and the split is the whole point.
  #
  # Its W: lines are ADVISORY and must stay that way. It reports "Dependency
  # included, but may not be needed" for every one of the eleven dlopen ed
  # libraries, because deriving from ELF links is precisely what namcap does and
  # precisely what cannot see a dlopen. Failing on those would mean deleting the
  # dependencies that make the app work. Two more are upstream facts we do not
  # control: SkiaSharp and HarfBuzzSharp ship prebuilt without FULL RELRO, and still
  # link libpthread/libdl that glibc 2.34 folded into libc.
  #
  # Its E: lines are NOT advisory. The first green run of this script printed
  # "E: Dependency hicolor-icon-theme detected and not included" and still reported
  # every phase ok, because nothing was reading namcap output - a real defect
  # (unowned icon hierarchy, icon-cache hook never guaranteed) surviving a smoke
  # test that had just passed. Hence the gate.
  command -v namcap >/dev/null \
    || { echo "  FAIL: namcap is not installed, so the packaging-issue gate would be vacuous" >&2; exit 1; }
  # Captured, not piped: under pipefail a non-zero namcap (it exits non-zero when it
  # has findings) would take the pipeline down before the severity split could run.
  namcap_out="$(namcap /art/*.pkg.tar.zst || true)"
  [ -n "$namcap_out" ] \
    || { echo "  FAIL: namcap produced no output at all - it did not inspect the package" >&2; exit 1; }
  echo "$namcap_out"
  if grep -E " E: " <<<"$namcap_out" >/dev/null; then
    echo "  FAIL: namcap reported error-level findings (listed above)" >&2
    grep -E " E: " <<<"$namcap_out" >&2
    exit 1
  fi
  echo "  ok: namcap reports no error-level findings"
'

echo
echo "=== Container 2: GUI launch (Xvfb permitted) ==="
docker run --rm -v "$work:/art:ro" "$test_image" bash -euo pipefail -c '
  # The official archlinux images ship NoExtract lines in /etc/pacman.conf that
  # drop /usr/share/man, /usr/share/doc and most locales at unpack time to keep the
  # image small. A real Arch machine has no such lines, so leaving them in place
  # makes our own man-page assertion below fail on a CORRECTLY BUILT package - a
  # false gate, and precisely the trap packaging/linux/smoke-test.sh documents for
  # the ubuntu:22.04 excludes file at /etc/dpkg/dpkg.cfg.d/excludes. Not
  # hypothetical: the first run of this script failed on exactly that, against a
  # package whose own bsdtar listing showed usr/share/man/man1/nova.1.gz present.
  # Removing them makes the container faithful to the machine being tested for.
  # Do not "clean this up" - it is load-bearing, not a leftover.
  sed -i "/^[[:space:]]*NoExtract/d" /etc/pacman.conf
  pacman -Syu --noconfirm >/dev/null
  pacman -U --noconfirm /art/*.pkg.tar.zst >/dev/null
  pacman -S --noconfirm --needed xorg-server-xvfb xorg-xdpyinfo xdotool >/dev/null

  Xvfb :99 -screen 0 1280x800x24 >/tmp/xvfb.log 2>&1 &
  export DISPLAY=:99
  for i in $(seq 1 30); do xdpyinfo >/dev/null 2>&1 && break; sleep 1; done
  xdpyinfo >/dev/null || { echo "  FAIL: Xvfb never came up" >&2; cat /tmp/xvfb.log >&2; exit 1; }

  nova >/tmp/nova.log 2>&1 &
  nova_pid=$!
  # Poll rather than sleep-and-hope: a fixed sleep either flakes on a slow runner or
  # wastes time on a fast one, and a crash inside the window would still be reported
  # as "no window" instead of as the crash it is.
  mapped=0
  for i in $(seq 1 45); do
    if ! kill -0 "$nova_pid" 2>/dev/null; then
      echo "  FAIL: nova exited before mapping a window" >&2; cat /tmp/nova.log >&2; exit 1
    fi
    if xdotool search --class NovaTerminal 2>/dev/null | grep -q .; then mapped=1; break; fi
    sleep 1
  done
  [ "$mapped" = 1 ] || { echo "  FAIL: no NovaTerminal window after 45s" >&2; cat /tmp/nova.log >&2; exit 1; }
  echo "  ok: window mapped with WM_CLASS NovaTerminal"
  kill "$nova_pid" 2>/dev/null || true
'

echo
echo "=== all Arch smoke phases passed ==="
