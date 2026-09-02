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

  # stock ubuntu:22.04 ships /etc/dpkg/dpkg.cfg.d/excludes, which path-excludes
  # /usr/share/man/* and /usr/share/doc/* (keeping only */copyright and
  # */changelog.*) at unpack time. A real users machine has no such exclude file,
  # so leaving it in place would make our own man-page/doc assertions below fail
  # on a CORRECTLY BUILT package - a false CI gate, which is exactly the failure
  # class this smoke test exists to prevent. Deleting it here makes the container
  # faithful to the machine we are actually testing for. Do not "clean this up" -
  # it is load-bearing, not a leftover.
  rm -f /etc/dpkg/dpkg.cfg.d/excludes

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

  # These are dlopen loaded at runtime, so ldd above cannot see them. They must
  # resolve from the package own Depends - nothing else has been installed that
  # could provide them.
  for so in libX11.so.6 libfontconfig.so.1 libXrandr.so.2 libXi.so.6 \
            libXcursor.so.1 libXext.so.6 libICE.so.6 libSM.so.6 libGL.so.1; do
    ldconfig -p | grep -q "$so" || { echo "  FAIL: $so missing (dlopen dep not in Depends)" >&2; exit 1; }
  done
  echo "  ok: every dlopen loaded library resolves"

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

  # Same dpkg excludes fix as Container 1 - see the comment there. Not load-bearing
  # for the assertions in this container today, but applied unconditionally so this
  # container never silently diverges from a real users machine either.
  rm -f /etc/dpkg/dpkg.cfg.d/excludes

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
