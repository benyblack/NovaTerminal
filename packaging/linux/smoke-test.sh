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

  # Every ELF under the bundle, not just NovaTerminal: this is the ONLY place an
  # undeclared LINKED dependency can be caught honestly. Container 2 below installs
  # xvfb+xdotool before ever launching the app, which drag in libx11-6/libxext6/
  # libxi6/libxrandr2/libxkbcommon0 and Mesa/GL - exactly the libraries most likely
  # to be missing - so by the time anything launches there, those libraries are
  # already present regardless of whether the .deb declared them. A silently
  # incomplete Depends: would install cleanly and then fail at dlopen/dlsym time on
  # a real users machine with nothing here to have caught it.
  #
  # ELF DISCOVERY IS BY MAGIC BYTES, NOT BY `file`. `file` is not installed in this
  # container and MUST NOT be, per the phase A rule above. An earlier version of
  # this loop discovered ELFs with
  #   find ... -exec sh -c "file -b \"$1\" | grep -q ^ELF" _ {} \; -print
  # which printed `file: not found` once per candidate, emitted no paths at all, and
  # so ran the loop body ZERO times while still printing the ok line below - a check
  # that could not fail. `head -c4` + `od` are coreutils, present in every base
  # image, and need nothing installed. ELF files start with 7f 45 4c 46.
  #
  # ldd is captured first, not piped straight into grep: under `pipefail` a non-zero
  # ldd combined with a non-matching grep still yields a non-zero PIPELINE, so the
  # `if` would be false and this would print ok without the probe having run.
  # Checking ldd exit status separately closes that hole. Note that ldd exits 0 -
  # not non-zero - when it reports `=> not found`, so the grep is the ONLY thing
  # that can catch an unresolved soname; a non-zero ldd here means something else
  # (a non-dynamic ELF, an unreadable file) and is worth failing on too.
  elf_count=0
  main_checked=0
  while IFS= read -r f; do
    [ "$(head -c4 "$f" | od -An -tx1 | tr -d " \n")" = "7f454c46" ] || continue
    elf_count=$((elf_count + 1))
    if [ "$f" = "/usr/lib/novaterminal/NovaTerminal" ]; then main_checked=1; fi
    ldd_out="$(ldd "$f")" \
      || { echo "  FAIL: ldd failed on $f" >&2; exit 1; }
    if grep "not found" <<<"$ldd_out"; then
      echo "  FAIL: unresolved linked libraries in $f above" >&2; exit 1
    fi
  done < <(find /usr/lib/novaterminal -type f)

  # Anti-vacuity assertions, because a silently zero-iteration loop is exactly how
  # this check stopped working before. Expectation is DERIVED from the bundle, not
  # hardcoded: every *.so plus the main AOT binary must have been ldd-ed, so adding
  # or removing a native library needs no edit here, while a discovery regression
  # (or a *.so that is somehow not an ELF) fails loudly.
  so_total="$(find /usr/lib/novaterminal -type f -name "*.so" | wc -l)"
  expected=$((so_total + 1))
  if [ "$main_checked" != 1 ]; then
    echo "  FAIL: ELF discovery never reached /usr/lib/novaterminal/NovaTerminal - the check did not run" >&2; exit 1
  fi
  if [ "$elf_count" -lt "$expected" ]; then
    echo "  FAIL: ELF discovery checked $elf_count file(s) but the bundle has $so_total *.so plus the main binary ($expected)" >&2; exit 1
  fi
  echo "  ok: no unresolved linked libraries in any of the $elf_count bundled ELFs"

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
# --device /dev/fuse --cap-add SYS_ADMIN: a type-2 AppImage self-mounts via FUSE and
# has no fallback, so without the device node AND the capability the FUSE-mounted
# launch below fails on a perfectly good AppImage - the same false-gate class the
# dpkg-excludes fix in Container 1 exists to prevent. `apt-get install libfuse2`
# alone only supplies the library, not the device or the privilege to use it.
#
# --security-opt apparmor:unconfined: with `fuse` installed (setuid fusermount
# present), /dev/fuse passed, and CAP_SYS_ADMIN granted - which is normally
# sufficient for a FUSE mount - the FUSE-mounted launch on GitHub's runners still
# failed with "fuse: mount failed: Permission denied" / "Cannot mount AppImage,
# please check your FUSE setup." The remaining difference between GitHub's Ubuntu
# runners and a plain container host is AppArmor: GitHub's runners have the
# AppArmor kernel module enabled and Docker applies its `docker-default` profile,
# which denies the `mount` syscall outright regardless of capabilities. That is
# the documented cause of exactly this error when fusermount is otherwise working.
# This flag disables the AppArmor confinement Docker would otherwise apply to
# THIS container only - it is scoped to the smoke test's throwaway launch
# container, confers nothing on the shipped .deb/AppImage artifact, and does not
# touch Container 1 (which never mounts anything and must not gain it).
#
# THIS IS UNVERIFIED LOCALLY. Docker Desktop on this dev machine reports no
# AppArmor module at all (`docker info` security options show only seccomp and
# cgroupns), so the failure mode this flag targets cannot be reproduced or
# exercised here - the flag is inert on this host by construction, not proven
# effective. Confirm or refute via an actual CI run on GitHub's runners, not by
# further local testing. If this does not fix it, that is a decision for a human
# to make about the mounted-launch gate, not something to silently work around
# here (e.g. by skipping or downgrading this assertion).
docker run --rm --device /dev/fuse --cap-add SYS_ADMIN --security-opt apparmor:unconfined -v "$artifact_dir:/art:ro" "$image" bash -euo pipefail -c '
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
    local rc=0
    # The probe MUST run inside the same xvfb-run invocation as the app, not beside
    # it: xvfb-run exports DISPLAY/XAUTHORITY only into the environment of its own
    # child process, and finds the NEXT free server number on each call (:99, :100,
    # :101, ...), so a sibling process with a hardcoded DISPLAY=:99 cannot see later
    # launches at all and instead silently attaches to a server left over from an
    # earlier call - a probe that can never legitimately fail. Nesting here is what
    # makes DISPLAY/XAUTHORITY inherited and removes the hardcoded server number
    # entirely. --onlyvisible excludes windows that exist but were never mapped, so
    # "ok" only fires on a window actually drawn on screen. Exit codes distinguish
    # "process died" (2) from "process alive but never mapped a window" (3) so the
    # log keeps that distinction.
    xvfb-run -a --server-args="-screen 0 1024x768x24" bash -c '\''
      set -uo pipefail
      "$@" & app=$!
      for _ in $(seq 1 20); do
        sleep 1
        kill -0 "$app" 2>/dev/null || exit 2
        if xdotool search --onlyvisible --class -- NovaTerminal >/dev/null 2>&1; then
          kill "$app" 2>/dev/null || true; exit 0
        fi
      done
      kill "$app" 2>/dev/null || true; exit 3
    '\'' _ "$@" || rc=$?
    case "$rc" in
      0) echo "  ok: $label mapped a window"; return 0 ;;
      2) echo "  FAIL: $label exited early" >&2; return 1 ;;
      *) echo "  FAIL: $label never mapped a window in 20s" >&2; return 1 ;;
    esac
  }

  launches "deb install (/usr/bin/nova)" nova

  # The AppImage is tested TWICE on purpose. Ubuntu 22.04+ ships no FUSE 2 by
  # default, so a stock type-2 AppImage fails there with a confusing FUSE error -
  # users hit the mounted path, CI must cover both.
  #
  # `libfuse2` alone is NOT enough: on ubuntu:22.04 it supplies only the shared
  # library, not the `fusermount` helper. The AppImage mount step execs
  # `fusermount`, so without it the mount fails with "No suitable fusermount
  # binary found on the PATH" even though the library is present. `fuse` is the
  # package that ships `/usr/bin/fusermount` (setuid root), and it depends on
  # `libfuse2` so apt pulls that in too - installing `fuse` alone is both
  # necessary and sufficient. Verified empirically in a fresh ubuntu:22.04
  # container with these same --device /dev/fuse --cap-add SYS_ADMIN flags:
  # `apt-get install libfuse2` left `command -v fusermount` empty; `apt-get
  # install fuse` produced `-rwsr-xr-x 1 root root .../usr/bin/fusermount` and
  # the mounted launch below then succeeded.
  shopt -s nullglob
  for img in /art/*.AppImage; do
    cp "$img" /tmp/nova.AppImage && chmod +x /tmp/nova.AppImage
    launches "AppImage (extracted, no FUSE)" /tmp/nova.AppImage --appimage-extract-and-run
    apt-get install -y -qq fuse
    launches "AppImage (FUSE-mounted)" /tmp/nova.AppImage
  done
'

echo
echo "smoke test passed"
