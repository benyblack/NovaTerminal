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
    # Each dlopen'd dependency must appear exactly once - the dedupe loop must actually dedupe.
    for pkg in libfontconfig1 libx11-6 libxrandr2 libxi6 libxcursor1 libxext6 libice6 libsm6 libgl1; do
      count="$(grep -o "$pkg" <<<"$info" | wc -l)"
      [[ "$count" -eq 1 ]] || fail "Depends has $pkg $count times, want exactly 1"
    done
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
