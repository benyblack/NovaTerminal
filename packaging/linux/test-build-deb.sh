#!/usr/bin/env bash
# Tests build-deb.sh without needing a real NovaTerminal publish. Run inside a
# Debian-family container as root (it needs dpkg-deb, dpkg-query, file, ldd,
# binutils' strip, an ImageMagick 'magick' or 'convert', plus 'fc-match' and
# 'xdpyinfo' as donor ELFs that link real libfontconfig1/libx11-6 - see the
# "package construction" section below):
#   docker run --rm -v "$PWD:/w:ro" -w /w ubuntu:22.04 \
#     bash -c 'apt-get update -qq &&
#              apt-get install -y -qq file imagemagick fontconfig x11-utils binutils >/dev/null &&
#              packaging/linux/test-build-deb.sh'
#
# Running as root is required so this script can drop privileges (via setpriv) before
# invoking build-deb.sh: dpkg-deb --root-owner-group stamps root:root regardless of
# the *invoking* uid, so if the harness itself ran the build as root, the root-owned
# assertion below would pass even with --root-owner-group silently removed from
# build-deb.sh - it would prove nothing. Dropping to 'nobody' first is what makes that
# assertion meaningful.
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
mkdir -p "$pub/themes" && echo '{}' > "$pub/themes/default.json"

# A dummy .so so this fast harness also exercises the lintian-fix-round path
# (strip --strip-unneeded, chmod normalisation to 0644) without needing a real
# Rust/SkiaSharp build. Before this, the fixture shipped zero *.so files, so that
# path was covered by zero cheap tests. /bin/true is a real ELF; the .so
# extension is all build-deb.sh's strip/chmod loops key off, not the ELF type -
# executable-by-default (0755) so the chmod-to-0644 assertion below actually
# exercises something, matching how the real SkiaSharp/HarfBuzzSharp binaries
# ship (0744, the exact bug the chmod pass fixes).
cp /bin/true "$pub/libtest-fixture.so"
chmod 0755 "$pub/libtest-fixture.so"

# A second ELF that actually links libraries owned by DLOPEN_DEPENDS packages, so the
# ldd-derived path and the dlopen dedupe loop get exercised for real, not just "did
# not crash". /bin/true alone links nothing beyond libc6, which build-deb.sh
# deliberately skips re-adding - without a donor here, the exactly-once check below
# cannot tell `grep -q` from the correct `grep -qE` (every dlopen package would be
# appended exactly once by the plain `for` loop regardless of which one is used).
# fc-match (fontconfig) and xdpyinfo (x11-utils) are used, not ImageMagick's
# convert, because convert also links libicuuc/libicudata - which would additionally
# surface a real but out-of-scope Depends inconsistency (a hard `libicu70` dependency
# alongside the `libicu74 | libicu72 | ...` alternatives group) that has nothing to do
# with what this fixture is verifying.
donors=()
for candidate in "$(command -v fc-match 2>/dev/null || true)" "$(command -v xdpyinfo 2>/dev/null || true)"; do
  [[ -n "$candidate" ]] || continue
  if ldd "$candidate" 2>/dev/null | grep -qE 'libfontconfig\.so|libX11\.so|libXext\.so|libXi\.so'; then
    donors+=("$candidate")
  fi
done
if ((${#donors[@]} == 0)); then
  fail "no donor ELF found linking libfontconfig/libX11/libXext/libXi - install 'fontconfig' and 'x11-utils' in the test container (see header comment); the ldd-path dedupe check below would otherwise be vacuous"
else
  for i in "${!donors[@]}"; do
    cp -L "${donors[$i]}" "$pub/linked-test-binary-$i"
  done
  pass "fixture includes donor ELF(s) linking real dlopen-listed libraries: ${donors[*]}"
fi

# Build as a non-root user so --root-owner-group is actually load-bearing: run as
# root (as the header comment documents), the harness itself drops privileges via
# setpriv before invoking build-deb.sh. Without this, the root/root assertion below
# would pass even if --root-owner-group were silently removed from build-deb.sh,
# since dpkg-deb invoked BY root stamps root:root regardless of that flag.
run_script() {
  if [[ "$(id -u)" -eq 0 ]] && command -v setpriv >/dev/null 2>&1; then
    chown -R nobody:nogroup "$work"
    setpriv --reuid=nobody --regid=nogroup --clear-groups "$script" "$@"
  else
    "$script" "$@"
  fi
}

if ! run_script "$pub" "0.4.0" "amd64" "$out"; then
  fail "build-deb.sh exited non-zero"
else
  deb="$out/novaterminal_0.4.0-1_amd64.deb"
  [[ -f "$deb" ]] && pass "produced $(basename "$deb")" || fail "expected $deb"

  if [[ -f "$deb" ]]; then
    contents="$(dpkg-deb --contents "$deb")"
    # The six hicolor icon paths are the ONLY automated coverage of spec acceptance
    # criterion 5 ("NovaTerminal appears in the app menu with its icon"). Nothing else
    # in the chain catches a package built with no icons: desktop-file-validate does
    # not check that `Icon=novaterminal` resolves to an installed file, and lintian's
    # icon-size-mismatch is a WARNING, so `--fail-on error` lets it through too.
    # Paired with build-deb.sh treating a missing icon source as fatal, these two
    # changes are what make ci.yml's change-detection watch on nova_icon.png mean
    # something.
    for path in \
      ./usr/lib/novaterminal/NovaTerminal \
      ./usr/lib/novaterminal/themes/default.json \
      ./usr/lib/novaterminal/libtest-fixture.so \
      ./usr/bin/nova \
      ./usr/share/applications/novaterminal.desktop \
      ./usr/share/man/man1/nova.1.gz \
      ./usr/share/doc/novaterminal/copyright \
      ./usr/share/icons/hicolor/16x16/apps/novaterminal.png \
      ./usr/share/icons/hicolor/32x32/apps/novaterminal.png \
      ./usr/share/icons/hicolor/48x48/apps/novaterminal.png \
      ./usr/share/icons/hicolor/64x64/apps/novaterminal.png \
      ./usr/share/icons/hicolor/128x128/apps/novaterminal.png \
      ./usr/share/icons/hicolor/256x256/apps/novaterminal.png
    do
      grep -q -- "$path" <<<"$contents" || fail "missing from package: $path"
    done
    pass "layout checked"

    # The bundle binary must be executable, and /usr/bin/nova must be a symlink to it.
    grep -qE '^-rwxr-xr-x.* \./usr/lib/novaterminal/NovaTerminal$' <<<"$contents" \
      || fail "NovaTerminal is not 0755 in the package"
    grep -qE '^lrwxrwxrwx.* \./usr/bin/nova -> ' <<<"$contents" \
      || fail "/usr/bin/nova is not a symlink"

    # Lintian-fix-round assertions. The fixture .so is staged at 0755 (matching
    # how the real SkiaSharp/HarfBuzzSharp binaries actually ship) specifically so
    # this checks something: a fixture that started at 0644 would pass even if the
    # chmod-normalisation pass in build-deb.sh were deleted entirely.
    grep -qE '^-rw-r--r--.* \./usr/lib/novaterminal/libtest-fixture\.so$' <<<"$contents" \
      || fail "libtest-fixture.so is not 0644 in the package (chmod-normalisation regressed)"

    # The lintian overrides file must ship, or every future build silently loses
    # the documented embedded-library exceptions and `lintian --fail-on error`
    # starts failing every real build again with no explanation in this fast
    # harness - only the slow, real containerised run would ever catch it.
    grep -q -- './usr/share/lintian/overrides/novaterminal' <<<"$contents" \
      || fail "lintian overrides file missing from package"

    # Every entry must be root-owned (--root-owner-group), never the invoking uid -
    # checked strictly (no entry may be anything other than root/root), not just
    # "at least one root/root line appears somewhere". This only means something
    # because run_script above built the package as uid 65534 ('nobody'): built as
    # root, dpkg-deb stamps root:root regardless of --root-owner-group, so a lax
    # "contains root/root" check would stay green even with that flag removed.
    non_root="$(awk '$2 != "root/root" { print }' <<<"$contents")"
    [[ -z "$non_root" ]] || fail "package has non-root-owned entries: $non_root"

    info="$(dpkg-deb --field "$deb")"
    grep -q '^Package: novaterminal$'   <<<"$info" || fail "wrong Package field"
    grep -q '^Version: 0.4.0-1$'        <<<"$info" || fail "wrong Version field"
    grep -q '^Architecture: amd64$'     <<<"$info" || fail "wrong Architecture field"
    grep -q '^Depends: .*libc6 (>= 2.35)' <<<"$info" || fail "Depends lacks the glibc floor"
    grep -q 'libfontconfig1'            <<<"$info" || fail "Depends lacks libfontconfig1 (dlopen'd by Skia)"
    grep -q 'libx11-6'                  <<<"$info" || fail "Depends lacks libx11-6 (dlopen'd by Avalonia)"
    grep -q 'libicu'                    <<<"$info" || fail "Depends lacks an ICU alternatives list"
    # Each dlopen'd dependency must appear exactly once - the dedupe loop must actually
    # dedupe. Anchored on (^|[ ,]) ... (,|$), not a bare substring match: unanchored,
    # "libgl1" also matches inside "libgl1-mesa-dri", which the donor ELFs above could
    # plausibly pull in via ldd - a false "duplicate" that has nothing to do with
    # whether the dedupe loop actually deduped.
    for pkg in libfontconfig1 libx11-6 libxrandr2 libxi6 libxcursor1 libxext6 libice6 libsm6 libgl1; do
      count="$(grep -oE "(^|[ ,])$pkg(,|$)" <<<"$info" | wc -l)"
      [[ "$count" -eq 1 ]] || fail "Depends has $pkg $count times, want exactly 1"
    done
    pass "control fields checked"

    # No maintainer scripts, by design: dpkg triggers handle the caches.
    #
    # This is an ABSENCE assertion, and this script runs under `set -uo pipefail` with
    # no `-e`. Written as a `dpkg-deb ... | tar -t 2>/dev/null | grep -q "$s"` pipeline
    # per iteration it was VACUOUS: if dpkg-deb or tar produced nothing - with
    # 2>/dev/null hiding why - every grep would simply fail to match, no `fail` would
    # fire, and `pass "no maintainer scripts"` would print having inspected an empty
    # string. Same defect class as the `unzip | grep` .dbg leak fixed earlier on this
    # branch. So: capture once, prove the listing is real with a POSITIVE control
    # (./control is mandatory in every .deb control tarball), then check absences
    # against that captured text.
    ctrl="$(dpkg-deb --ctrl-tarfile "$deb" | tar -t)"
    if ! grep -q './control' <<<"$ctrl"; then
      fail "control tarball listing has no ./control entry - the maintainer-script check below would prove nothing. Got: $(printf '%q' "$ctrl")"
    else
      pass "control tarball listing is readable (positive control: ./control present)"
      for s in preinst postinst prerm postrm; do
        grep -q "$s" <<<"$ctrl" && fail "unexpected maintainer script: $s"
      done
      pass "no maintainer scripts"
    fi
  fi
fi

echo
if (( fails )); then echo "$fails check(s) failed"; exit 1; fi
echo "all checks passed"
