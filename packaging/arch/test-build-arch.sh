#!/usr/bin/env bash
# Tests for build-arch.sh: version mapping, the dlopen coverage gate, and the shape
# of the generated PKGBUILD. Needs no Arch host, no Docker and no NovaTerminal
# build - everything here is the generator's own output, inspected.
#
# The one thing these tests CANNOT cover is whether the generated PKGBUILD actually
# builds and installs; that is smoke-test.sh's job, in real Arch containers. Keep the
# split - a test file that starts shelling out to makepkg stops being runnable on the
# machine most people edit this on.
set -uo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
script="$here/build-arch.sh"
fails=0
fail() { echo "FAIL: $*"; fails=$((fails + 1)); return 0; }
pass() { echo "ok: $*"; return 0; }

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

# ---- version mapping ------------------------------------------------------
check_pkgver() {
  local in="$1" want="$2" got
  got="$("$script" --print-pkgver "$in" 2>&1)" || { fail "--print-pkgver $in exited non-zero: $got"; return; }
  [[ "$got" == "$want" ]] && pass "pkgver $in -> $got" || fail "pkgver $in -> $got (want $want)"
}
check_pkgver "0.8.0"             "0.8.0"
check_pkgver "v0.8.0"            "0.8.0"
check_pkgver "v0.8.3"            "0.8.3"
check_pkgver "v0.8.0-rc.1"       "0.8.0rc1"
check_pkgver "v0.9.0-beta.1"     "0.9.0beta1"
check_pkgver "v1.0.0-rc.2-fix"   "1.0.0rc2fix"

# A pkgver containing '-' is not a cosmetic problem: '-' is pacman's pkgver/pkgrel
# separator, so one that slipped through would silently split the version.
for v in "v0.8.0-rc.1" "v0.9.0-beta.1" "v1.0.0-rc.2-fix"; do
  got="$("$script" --print-pkgver "$v")"
  [[ "$got" != *-* ]] && pass "pkgver $v carries no '-'" || fail "pkgver $v still contains '-': $got"
done

# ---- prerelease ORDERING, the reason the mapping differs from Debian's -----
# build-deb.sh maps prereleases with '~' because dpkg sorts '~' before everything.
# pacman's vercmp has no such rule, so the same trick would sort every rc ABOVE the
# release it precedes and offer it to users as an upgrade. These assertions are the
# proof that the mapping chosen instead actually orders correctly; skipped where
# vercmp is absent (it ships with pacman), because a silently skipped assertion is
# better than one faked with a hand-rolled comparator that shares the bug.
if command -v vercmp >/dev/null 2>&1; then
  check_order() {
    local lo="$1" hi="$2" got
    got="$(vercmp "$lo" "$hi")"
    [[ "$got" -lt 0 ]] && pass "vercmp: $lo sorts below $hi" || fail "vercmp: $lo does NOT sort below $hi (got $got)"
    # Explicit, matching fail()/pass() and test-build-deb.sh's helpers: without it the
    # function returns the status of whichever branch of the `&& ... || ...` ran last,
    # which is a fragile thing for a helper to hand back to its caller.
    return 0
  }
  check_order "$("$script" --print-pkgver v0.8.0-rc.1)"   "$("$script" --print-pkgver v0.8.0)"
  check_order "$("$script" --print-pkgver v0.9.0-beta.1)" "$("$script" --print-pkgver v0.9.0-rc.1)"
  check_order "$("$script" --print-pkgver v0.8.0)"        "$("$script" --print-pkgver v0.9.0-beta.1)"
  check_order "$("$script" --print-pkgver v0.8.0)"        "$("$script" --print-pkgver v0.8.1)"
  # The regression this whole mapping exists to prevent, asserted directly: the
  # Debian-style '~' form must NOT be what we emit, because pacman sorts it wrong.
  got="$(vercmp "0.8.0~rc.1" "0.8.0")"
  [[ "$got" -gt 0 ]] \
    && pass "vercmp confirms the Debian '~' form would sort a prerelease ABOVE its release (hence the different mapping)" \
    || fail "vercmp no longer sorts '0.8.0~rc.1' above '0.8.0' (got $got) - pacman semantics changed; revisit print_pkgver"
else
  echo "skip: vercmp not available - prerelease ordering assertions not run"
fi

# ---- the dlopen coverage gate ---------------------------------------------
# Exercised through the NOVA_DEB_SCRIPT seam with a stub standing in for
# build-deb.sh, so the gate is tested rather than assumed. A gate nobody has seen
# fire is indistinguishable from one that cannot.
real_sonames="$("$here/../linux/build-deb.sh" --print-dlopen-sonames)"

make_stub() {
  local out="$1"; shift
  printf '#!/usr/bin/env bash\ncase "$1" in --print-dlopen-sonames) cat <<'"'"'EOS'"'"'\n%s\nEOS\n;; esac\n' "$1" > "$out"
  chmod +x "$out"
  return 0
}

# 1. An unmapped soname must fail the build.
make_stub "$tmp/stub-extra.sh" "$real_sonames
libfuture-1.so.0"
out="$(NOVA_DEB_SCRIPT="$tmp/stub-extra.sh" "$script" v0.8.0 "$tmp/gate1" --sha256 "$(printf '0%.0s' {1..64})" 2>&1)"
if [[ $? -ne 0 ]] && grep -q "libfuture-1.so.0" <<<"$out"; then
  pass "gate fails on a soname with no Arch mapping, naming it"
else
  fail "gate did NOT fail on an unmapped soname; output: $out"
fi

# 2. A mapping for a soname build-deb.sh no longer declares must also fail - the
#    other direction, so a removed dependency cannot leave a stale claim behind.
make_stub "$tmp/stub-fewer.sh" "$(grep -v '^libSM\.so\.6$' <<<"$real_sonames")"
out="$(NOVA_DEB_SCRIPT="$tmp/stub-fewer.sh" "$script" v0.8.0 "$tmp/gate2" --sha256 "$(printf '0%.0s' {1..64})" 2>&1)"
if [[ $? -ne 0 ]] && grep -q "libSM.so.6" <<<"$out"; then
  pass "gate fails on a stale mapping, naming it"
else
  fail "gate did NOT fail on a stale mapping; output: $out"
fi

# 3. An empty table must fail rather than pass vacuously.
make_stub "$tmp/stub-empty.sh" ""
out="$(NOVA_DEB_SCRIPT="$tmp/stub-empty.sh" "$script" v0.8.0 "$tmp/gate3" --sha256 "$(printf '0%.0s' {1..64})" 2>&1)"
if [[ $? -ne 0 ]] && grep -qi "vacuous" <<<"$out"; then
  pass "gate fails loudly on an empty dlopen table"
else
  fail "gate accepted an empty dlopen table; output: $out"
fi

# ---- generated PKGBUILD ---------------------------------------------------
fake_tarball="$tmp/NovaTerminal-linux-x64-v0.8.0.tar.gz"
printf 'not really a tarball' > "$fake_tarball"
fake_sha="$(sha256sum "$fake_tarball" | awk '{print $1}')"

gen="$tmp/out"
if ! "$script" v0.8.0 "$gen" --tarball "$fake_tarball" >/dev/null 2>&1; then
  fail "generation failed for a stable version"
else
  pass "generated a PKGBUILD for v0.8.0"
  pkgbuild="$gen/PKGBUILD"

  grep -q "^pkgname=novaterminal-bin$" "$pkgbuild" && pass "pkgname" || fail "pkgname"
  grep -q "^pkgver=0.8.0$"             "$pkgbuild" && pass "pkgver"  || fail "pkgver"
  grep -q "^pkgrel=1$"                 "$pkgbuild" && pass "pkgrel"  || fail "pkgrel"
  grep -q "^_tag=v0.8.0$"              "$pkgbuild" && pass "_tag"    || fail "_tag"
  grep -q "^arch=('x86_64')$"          "$pkgbuild" && pass "arch is x86_64 only" || fail "arch"
  grep -q "^options=('!strip' '!debug')$" "$pkgbuild" && pass "options disable makepkg's blanket strip" || fail "options"
  grep -q "^provides=('novaterminal')$"   "$pkgbuild" && pass "provides" || fail "provides"
  grep -q "^conflicts=('novaterminal')$"  "$pkgbuild" && pass "conflicts" || fail "conflicts"
  grep -q "sha256sums_x86_64=('$fake_sha')" "$pkgbuild" && pass "tarball sha256 is the real digest" || fail "tarball sha256"

  # No placeholder may survive: makepkg would treat a leftover @TOKEN@ as a literal,
  # producing a package that installs something other than what was intended, and
  # nobody reads a generated file before it is pushed to the AUR.
  grep -q '@[A-Z_]*@' "$pkgbuild" && fail "unsubstituted placeholder left in PKGBUILD" || pass "no placeholders left"

  # Every derived dependency must actually reach depends=(). This is the link
  # between the gate above and the file users install - without it the gate could
  # pass while the generator dropped a package on the floor.
  missing=0
  while IFS= read -r dep; do
    [[ -n "$dep" ]] || continue
    grep -q "'${dep}'" "$pkgbuild" || { fail "depends=() is missing '$dep'"; missing=1; }
  done < <("$script" --print-depends)
  (( missing == 0 )) && pass "depends=() carries every derived package"

  # sha256sums must line up with source entries one-for-one, or makepkg errors out
  # at build time with a message that names neither this generator nor the release.
  src_count="$(sed -n '/^source=(/,/)$/p' "$pkgbuild" | grep -c '::')"
  sum_count="$(sed -n '/^sha256sums=(/,/)$/p' "$pkgbuild" | grep -c "'")"
  [[ "$src_count" -eq "$sum_count" && "$src_count" -gt 0 ]] \
    && pass "source=() and sha256sums=() line up ($src_count entries)" \
    || fail "source=() has $src_count entries but sha256sums=() has $sum_count"
fi

# The prerelease trap, asserted directly: pkgver is lossy (0.8.0rc1 cannot be turned
# back into v0.8.0-rc.1), so a PKGBUILD that built its URLs from "v$pkgver" would 404
# on every prerelease. _tag must carry the original tag verbatim.
gen2="$tmp/out-pre"
if "$script" v0.8.0-rc.1 "$gen2" --tarball "$fake_tarball" >/dev/null 2>&1; then
  grep -q "^_tag=v0.8.0-rc.1$" "$gen2/PKGBUILD" \
    && pass "_tag keeps the original tag for a prerelease (not rebuilt from pkgver)" \
    || fail "_tag was reconstructed from pkgver - prerelease download URLs would 404"
  grep -q "^pkgver=0.8.0rc1$" "$gen2/PKGBUILD" && pass "prerelease pkgver" || fail "prerelease pkgver"
else
  fail "generation failed for a prerelease version"
fi

# ---- argument handling ----------------------------------------------------
"$script" v0.8.0 "$tmp/bad1" --tarball "$fake_tarball" --sha256 "$fake_sha" >/dev/null 2>&1 \
  && fail "accepted both --tarball and --sha256" || pass "rejects --tarball with --sha256"
"$script" v0.8.0 "$tmp/bad2" >/dev/null 2>&1 \
  && fail "accepted neither --tarball nor --sha256" || pass "rejects missing checksum source"
"$script" v0.8.0 "$tmp/bad3" --sha256 "nothex" >/dev/null 2>&1 \
  && fail "accepted a non-digest --sha256" || pass "rejects a malformed --sha256"

# A mistyped --print-* mode must be REJECTED, not silently treated as a version.
# Without the default case in the mode dispatcher it fell through to normal
# generation, where "--print-depend" became the version string and the run failed
# several steps later complaining that it did not look like a tag.
out="$("$script" --print-depend 2>&1)"
if [[ $? -ne 0 ]] && grep -q "unknown option" <<<"$out"; then
  pass "rejects an unknown --option instead of taking it as a version"
else
  fail "a mistyped --print-* mode was not rejected; output: $out"
fi

# ---- a broken makepkg must not fail the generation -------------------------
# The PKGBUILD is the deliverable; .SRCINFO is a derived convenience this script
# produces when it can. CI caught the original getting this backwards: the
# generator tests run in a container as root, makepkg refuses to run as root, and
# BOTH full-generation tests reported "generation failed" for a PKGBUILD that had
# already been written correctly.
#
# Simulated with a failing makepkg on PATH rather than by unsetting it, because
# that also exercises the half that matters more: the `> .SRCINFO` redirection
# creates the file BEFORE makepkg runs, so a failure leaves a zero-byte .SRCINFO
# behind. Every downstream `test -f .SRCINFO` would pass on it, and the AUR would
# take a push whose metadata says nothing.
mkdir -p "$tmp/fakebin"
printf '#!/usr/bin/env bash\nexit 1\n' > "$tmp/fakebin/makepkg"
chmod +x "$tmp/fakebin/makepkg"

if PATH="$tmp/fakebin:$PATH" "$script" v0.8.0 "$tmp/nosrcinfo" --tarball "$fake_tarball" >/dev/null 2>&1; then
  pass "generation succeeds even when makepkg fails"
else
  fail "a failing makepkg killed the whole generation"
fi
[[ -f "$tmp/nosrcinfo/PKGBUILD" ]] \
  && pass "the PKGBUILD is still written when makepkg fails" \
  || fail "no PKGBUILD written when makepkg fails"
[[ -e "$tmp/nosrcinfo/.SRCINFO" ]] \
  && fail "a zero-byte .SRCINFO was left behind by the failed makepkg" \
  || pass "no empty .SRCINFO left behind when makepkg fails"

# The nastier variant of the same defect, and the one a zero-byte check misses: a
# REUSED output directory. A stale .SRCINFO from a previous version is valid and
# non-empty, so it survives `test -s`, sits beside a freshly overwritten PKGBUILD,
# and would be pushed to the AUR as metadata for a package it does not describe.
printf 'pkgbase = novaterminal-bin\npkgver = 0.0.1-stale\n' > "$tmp/nosrcinfo/.SRCINFO"
PATH="$tmp/fakebin:$PATH" "$script" v0.8.0 "$tmp/nosrcinfo" --tarball "$fake_tarball" >/dev/null 2>&1
[[ -e "$tmp/nosrcinfo/.SRCINFO" ]] \
  && fail "a stale .SRCINFO from a previous version survived regeneration" \
  || pass "a stale .SRCINFO is removed when regeneration cannot produce a new one"

# ---- --source-ref reads the REF, not the working tree ---------------------
# The failure this prevents is subtle and would look like someone else's bug: a PR
# that edits nova.desktop generates a PKGBUILD pinning the BRANCH's sum against the
# TAG's URL, and makepkg fails an integrity check unrelated to the change under
# review. Proving it needs a file that differs between ref and tree, and all four
# real ones have been byte-identical across every tag so far - so this builds a
# throwaway repo where they do differ, rather than asserting nothing on the real one.
if command -v git >/dev/null 2>&1; then
  fake="$tmp/fakerepo"
  mkdir -p "$fake/packaging/arch" "$fake/packaging/linux" "$fake/src/NovaTerminal.App/Assets"
  cp "$script" "$fake/packaging/arch/build-arch.sh"
  cp "$here/../linux/build-deb.sh" "$fake/packaging/linux/build-deb.sh"
  printf 'desktop-at-ref'  > "$fake/packaging/linux/nova.desktop"
  printf 'man-at-ref'      > "$fake/packaging/linux/nova.1"
  printf 'icon-at-ref'     > "$fake/src/NovaTerminal.App/Assets/nova_icon.png"
  printf 'license-at-ref'  > "$fake/LICENSE"
  (
    cd "$fake"
    git init -q .
    git add -A
    git -c user.email=t@t -c user.name=t commit -qm base
    git tag testref
  ) >/dev/null 2>&1

  sha_at_ref="$(printf 'desktop-at-ref' | sha256sum | awk '{print $1}')"
  printf 'desktop-in-tree' > "$fake/packaging/linux/nova.desktop"
  sha_in_tree="$(printf 'desktop-in-tree' | sha256sum | awk '{print $1}')"
  zero="$(printf '0%.0s' {1..64})"

  "$fake/packaging/arch/build-arch.sh" v0.8.0 "$tmp/ref-on"  --sha256 "$zero" --source-ref testref >/dev/null 2>&1
  "$fake/packaging/arch/build-arch.sh" v0.8.0 "$tmp/ref-off" --sha256 "$zero"                      >/dev/null 2>&1

  if [[ -f "$tmp/ref-on/PKGBUILD" && -f "$tmp/ref-off/PKGBUILD" ]]; then
    grep -q "$sha_at_ref" "$tmp/ref-on/PKGBUILD" \
      && pass "--source-ref hashes the blob at the ref" \
      || fail "--source-ref did NOT hash the ref's blob"
    grep -q "$sha_in_tree" "$tmp/ref-off/PKGBUILD" \
      && pass "without --source-ref the working tree is hashed" \
      || fail "working-tree generation did not hash the working tree"
    grep -q "$sha_in_tree" "$tmp/ref-on/PKGBUILD" \
      && fail "--source-ref leaked the working-tree sum into the PKGBUILD" \
      || pass "--source-ref ignores the dirty working tree entirely"
  else
    fail "--source-ref generation did not produce a PKGBUILD"
  fi

  # A ref that does not resolve must fail loudly, naming itself - not fall back to
  # the working tree, which would silently reintroduce the whole bug.
  out="$("$fake/packaging/arch/build-arch.sh" v0.8.0 "$tmp/ref-bad" --sha256 "$zero" --source-ref no-such-ref 2>&1)"
  if [[ $? -ne 0 ]] && grep -q "no-such-ref" <<<"$out"; then
    pass "an unresolvable --source-ref fails, naming the ref"
  else
    fail "an unresolvable --source-ref did not fail loudly; output: $out"
  fi
else
  echo "skip: git not available - --source-ref assertions not run"
fi

# ---- smoke-test.sh container bodies ---------------------------------------
# Each phase of smoke-test.sh is a script passed to `docker run ... bash -c '...'`,
# so its entire body lives inside ONE pair of single quotes. A single quote anywhere
# in that body - an apostrophe in a comment, a sed expression in '...' - closes the
# string early and silently reshapes the command. `bash -n` on smoke-test.sh does
# NOT reliably catch it: an even number of stray quotes leaves the outer file
# balanced while the container runs something other than what was written.
#
# This is not a hypothetical either; it is how the NoExtract fix was first written.
# So: extract each body and check it parses on its own, with no stray quotes.
smoke="$here/smoke-test.sh"
if [[ -f "$smoke" ]]; then
  bodies=0
  # Tracked separately from the global fail counter: without it the summary
  # below prints "all N bodies clean" in the very run that just reported a
  # stray quote, which is how a green-looking failure gets skimmed past.
  smoke_clean=1
  while IFS= read -r body_file; do
    bodies=$((bodies + 1))
    if grep -q "'" "$body_file"; then
      fail "smoke-test.sh container body $bodies contains a single quote (it would close the bash -c string early)"
      grep -n "'" "$body_file" | sed 's/^/      /'
      smoke_clean=0
    fi
    bash -n "$body_file" 2>/dev/null || { fail "smoke-test.sh container body $bodies does not parse standalone"; smoke_clean=0; }
  done < <(python3 - "$smoke" "$tmp" <<'PYEOF'
import io, re, sys
src, outdir = sys.argv[1], sys.argv[2]
text = io.open(src, encoding="utf-8").read()
for i, chunk in enumerate(text.split("bash -euo pipefail -c '")[1:], 1):
    m = re.search(r"\n'\n", chunk)
    body = chunk[:m.start()] if m else chunk
    path = f"{outdir}/smoke-body-{i}.sh"
    io.open(path, "w", encoding="utf-8").write(body)
    print(path)
PYEOF
  )
  if (( bodies == 0 )); then
    fail "found no container bodies in smoke-test.sh - the extraction pattern went stale, so this check proved nothing"
  elif (( smoke_clean == 1 )); then
    pass "smoke-test.sh: all $bodies container bodies parse standalone with no stray quotes"
  fi
else
  echo "skip: smoke-test.sh not found"
fi

echo
if (( fails == 0 )); then echo "all build-arch.sh tests passed"; else echo "$fails test(s) failed"; fi
exit $(( fails > 0 ))
