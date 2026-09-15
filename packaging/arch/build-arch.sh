#!/usr/bin/env bash
# Generate the ntilde-bin AUR package sources from a published GitHub release.
#
# Usage: build-arch.sh <version> <out-dir> [--tarball <path>] [--sha256 <sum>]
#                      [--source-ref <git-ref>]
#        build-arch.sh --print-pkgver <version>
#        build-arch.sh --print-arch-map        (one "<soname>|<Arch package>" per line)
#        build-arch.sh --print-depends         (the depends array, one per line)
#
# Emits <out-dir>/PKGBUILD and, where makepkg exists, <out-dir>/.SRCINFO - exactly
# the two files an AUR repository contains. Nothing is compiled here and nothing is
# vendored: the package repackages the published linux-x64 tarball, the same
# artifact README.md offers as the portable install, and pulls its desktop entry,
# man page, icon and licence straight from the tag over HTTPS. That is why the AUR
# repo this produces carries no binary blobs - see "Why no local source files".
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$here/../.." && pwd)"
deb_script="${NTILDE_DEB_SCRIPT:-$here/../linux/build-deb.sh}"   # NTILDE_DEB_SCRIPT: test seam, see test-build-arch.sh

# --- version mapping -------------------------------------------------------
# NOT the Debian mapping, and the difference is load-bearing. dpkg gives '~' a
# sorts-before-everything meaning, so build-deb.sh maps v0.8.0-rc.1 to
# 0.8.0~rc.1-1 and gets correct prerelease ordering for free. pacman's vercmp has
# no such rule - it is rpmvercmp-derived and treats '~' as just another separator.
# Verified on pacman 7.1.0 (Arch, 2026-09-14):
#
#   $ vercmp 0.8.0~rc.1 0.8.0   ->  1    # WRONG: prerelease sorts ABOVE the release
#   $ vercmp 0.8.0.rc.1 0.8.0   ->  1    # WRONG, same reason
#   $ vercmp 0.8.0rc1   0.8.0   -> -1    # correct
#
# The rule that produces the right answer is rpmvercmp's: when one version runs out
# of segments where the other has an ALPHABETIC segment, the alphabetic one is
# older. So the prerelease label must be glued to the release with no separator and
# stripped of its own dots: 0.8.0-rc.1 -> 0.8.0rc1, 0.8.0-beta.1 -> 0.8.0beta1.
# Ordering among prereleases then falls out of the same comparison (beta1 < rc1
# alphabetically; rc1 < rc2 numerically).
#
# Copying the '~' from build-deb.sh would have looked right, passed review by
# analogy, and silently offered every rc to users as an upgrade over the stable
# release it precedes.
print_pkgver() {
  local v="${1#v}"
  local base="${v%%-*}"
  local pre=""
  if [[ "$v" == *-* ]]; then
    # tr, not ${//}: the label may carry both dots and further dashes
    # (0.9.0-rc.1-hotfix), and pkgver forbids '-' outright - it is pacman's
    # pkgver/pkgrel separator, so a stray one would silently split the version.
    pre="$(printf '%s' "${v#*-}" | tr -d '.-')"
  fi
  printf '%s%s\n' "$base" "$pre"
}

# pkgver's legal alphabet per PKGBUILD(5): alphanumerics plus '.', '_', '+', and it
# must not contain '-' or ':'. Asserted rather than trusted - an out-of-alphabet
# pkgver makes makepkg fail deep inside .SRCINFO generation with a message that
# names neither this script nor the tag it came from.
assert_pkgver() {
  local pkgver="$1"
  [[ "$pkgver" =~ ^[0-9][A-Za-z0-9._+]*$ ]] \
    || { echo "error: '$pkgver' is not a legal pacman pkgver (alphanumerics, '.', '_', '+'; must start with a digit)" >&2; exit 1; }
  return 0
}

# --- runtime-loaded (dlopen'd) libraries, mapped to Arch packages -----------
# The Arch half of build-deb.sh's DLOPEN_LIBS table. Same soname keys, Arch package
# names in the value column, and THE TWO ARE GATED AGAINST EACH OTHER below
# (assert_dlopen_map_covers_deb): a soname added to build-deb.sh with no entry here
# fails this script rather than quietly shipping an AUR package that installs
# cleanly and then cannot open a window.
#
# That gate is the entire reason this table is a table and not a hand-written
# depends= line in a PKGBUILD. namcap cannot help here: it derives dependencies from
# ELF links, and EIGHT of these eleven are invisible to `ldd` because Avalonia
# dlopen's them at runtime. A PKGBUILD whose depends= was namcap-derived would be
# missing all eight - the same defect the Debian lane's two-mechanism design exists
# to prevent, reintroduced on a different distro.
ARCH_DLOPEN_MAP=(
  "libfontconfig.so.1|fontconfig"
  "libX11.so.6|libx11"
  "libXrandr.so.2|libxrandr"
  "libXi.so.6|libxi"
  "libXcursor.so.1|libxcursor"
  "libXext.so.6|libxext"
  "libICE.so.6|libice"
  "libSM.so.6|libsm"
  "libGL.so.1|libglvnd"          # Arch routes libGL.so.1 through libglvnd, not mesa
  "libsecret-1.so.0|libsecret"
  "libglib-2.0.so.0|glib2"
)

# Runtime dependencies that are NOT in build-deb.sh's dlopen table and so cannot be
# derived from it. Each is here for a stated reason; this list is short on purpose.
#
#   glibc, gcc-libs - the only real DT_NEEDED of the whole bundle. Measured on the
#       published v0.8.0 linux-x64 tarball: the AOT binary needs libm/libc/ld only,
#       librusty_pty.so and librusty_ssh.so add libgcc_s.so.1 (gcc-libs), and
#       libSkiaSharp.so adds libfontconfig.so.1 (already above). Everything else the
#       process maps is reached through one of those.
#   icu - dlopen'd by .NET's globalization stack, so `ldd` and namcap are both blind
#       to it, exactly like the eleven above. It is NOT in build-deb.sh's table
#       because the Debian side has to express it as a version-pinned alternatives
#       group (libicu74 | libicu72 | ...) in its depends= line instead. Arch ships
#       one unversioned `icu`, so it is a plain dependency here - but it still needs
#       to be SOMEWHERE this script can see, or it is the one runtime dependency
#       nothing on this lane checks.
#
# Deliberately NOT listed, and this is where the Arch depends= diverges from the
# Debian Depends: most visibly: brotli, freetype2, libpng, util-linux-libs. Those
# appear in the .deb because build-deb.sh derives via `ldd`, which walks the full
# TRANSITIVE closure and so surfaces fontconfig's and glib2's own dependencies as if
# they were ours. On Arch they arrive through fontconfig and glib2; listing them
# again is redundant and namcap flags it. Verified against the running process on
# Arch (2026-09-14): every one of them was mapped, none of them by us.
#   hicolor-icon-theme - owns /usr/share/icons/hicolor and ships the pacman hook
#       that rebuilds the icon cache. The PKGBUILD justifies having no .install
#       scriptlet by pointing at that hook; without this dependency that argument is
#       circular, because on a minimal system nothing guarantees the hook is there
#       and the installed icons never reach the app menu. namcap says so at ERROR
#       level ("Dependency hicolor-icon-theme detected and not included"), which is
#       how this was caught - the first smoke run passed every hand-written
#       assertion and still shipped it.
ARCH_EXTRA_DEPENDS=(
  glibc
  # gcc-libs, not the narrower `libgcc` that actually owns libgcc_s.so.1: Arch split
  # libgcc/libstdc++ out of gcc-libs recently, and gcc-libs pulls them either way,
  # so this installs on both sides of that split. namcap notes the indirection as a
  # warning ("implicitly satisfied"); taking the warning is the cheaper trade than
  # a hard dependency on a package name that predates neither.
  gcc-libs
  icu
  hicolor-icon-theme
)

print_arch_map() {
  printf '%s\n' "${ARCH_DLOPEN_MAP[@]}"
}

print_depends() {
  local e
  for e in "${ARCH_DLOPEN_MAP[@]}"; do printf '%s\n' "${e#*|}"; done
  printf '%s\n' "${ARCH_EXTRA_DEPENDS[@]}"
}

# The gate. Reads build-deb.sh's table rather than keeping a second copy of the
# soname list, for the reason smoke-test.sh was changed to do the same: two
# hand-maintained lists means a library missing from both is invisible to both.
# Bidirectional on purpose - an unmapped soname ships a broken package, and a stale
# mapping means someone removed a dependency on the Debian side and left this one
# claiming a dependency the app no longer has.
assert_dlopen_map_covers_deb() {
  local deb_sonames mapped_sonames
  [[ -x "$deb_script" ]] \
    || { echo "error: cannot read the dlopen table: $deb_script is missing or not executable" >&2; exit 1; }
  deb_sonames="$("$deb_script" --print-dlopen-sonames)"

  # Anti-vacuity, both sides. An empty table on either side would make this gate
  # pass having compared nothing - the precise failure shape this whole mechanism
  # exists to close, so it is checked rather than assumed.
  local deb_count map_count
  deb_count="$(grep -c . <<<"$deb_sonames" || true)"
  map_count="${#ARCH_DLOPEN_MAP[@]}"
  (( deb_count > 0 )) \
    || { echo "error: $deb_script --print-dlopen-sonames produced nothing; the Arch dependency gate would be vacuous" >&2; exit 1; }
  (( map_count > 0 )) \
    || { echo "error: ARCH_DLOPEN_MAP is empty; the Arch dependency gate would be vacuous" >&2; exit 1; }

  mapped_sonames="$(for e in "${ARCH_DLOPEN_MAP[@]}"; do printf '%s\n' "${e%%|*}"; done)"

  local missing stale
  missing="$(comm -23 <(sort <<<"$deb_sonames") <(sort <<<"$mapped_sonames"))"
  stale="$(comm -13 <(sort <<<"$deb_sonames") <(sort <<<"$mapped_sonames"))"

  if [[ -n "$missing" ]]; then
    echo "error: build-deb.sh declares dlopen'd libraries with no Arch package mapping in ARCH_DLOPEN_MAP:" >&2
    sed 's/^/  - /' <<<"$missing" >&2
    echo "Add each to ARCH_DLOPEN_MAP with the Arch package that owns it (pacman -Qqo /usr/lib/<soname>)." >&2
    exit 1
  fi
  if [[ -n "$stale" ]]; then
    echo "error: ARCH_DLOPEN_MAP maps sonames build-deb.sh no longer declares:" >&2
    sed 's/^/  - /' <<<"$stale" >&2
    echo "Remove them here, or restore them in build-deb.sh's DLOPEN_LIBS if the removal was a mistake." >&2
    exit 1
  fi
  echo "dlopen gate: $map_count soname(s) mapped to Arch packages, matching build-deb.sh's table exactly"
}

# --- argument-only modes ---------------------------------------------------
case "${1:-}" in
  --print-pkgver)
    [[ $# -eq 2 ]] || { echo "usage: $0 --print-pkgver <version>" >&2; exit 2; }
    pkgver="$(print_pkgver "$2")"; assert_pkgver "$pkgver"; printf '%s\n' "$pkgver"; exit 0 ;;
  --print-arch-map)
    print_arch_map; exit 0 ;;
  --print-depends)
    print_depends; exit 0 ;;
  *)
    # Falling through to normal generation is intentional - anything that is not one
    # of the --print-* modes is a `<version> <out-dir>` invocation. An unknown OPTION
    # is not that, though, and without this branch a typo like `--print-depend` would
    # be taken as a version string and fail much later complaining about a tag that
    # does not look like a tag.
    #
    # `if`, not `[[ ... ]] && { ... }`: a false `&&` test leaves status 1 as the last
    # command of the case body, which under `set -e` kills the script instead of
    # falling through - the opposite of what this branch is for.
    if [[ "${1:-}" == --* ]]; then
      echo "error: unknown option: $1" >&2
      echo "usage: $0 <version> <out-dir> [--tarball <path>] [--sha256 <sum>] [--source-ref <git-ref>]" >&2
      echo "       $0 --print-pkgver <version> | --print-arch-map | --print-depends" >&2
      exit 2
    fi
    ;;
esac

# --- arguments -------------------------------------------------------------
version="${1:-}"
out_dir="${2:-}"
[[ -n "$version" && -n "$out_dir" ]] || {
  echo "usage: $0 <version> <out-dir> [--tarball <path>] [--sha256 <sum>]" >&2; exit 2; }
shift 2

tarball_path=""
tarball_sha=""
source_ref=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --tarball) tarball_path="${2:?--tarball needs a path}"; shift 2 ;;
    --sha256)  tarball_sha="${2:?--sha256 needs a sum}";    shift 2 ;;
    --source-ref) source_ref="${2:?--source-ref needs a git ref}"; shift 2 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

tag="$version"
[[ "$tag" == v* ]] || tag="v$tag"
pkgver="$(print_pkgver "$version")"
assert_pkgver "$pkgver"

# The tag is emitted into the PKGBUILD verbatim rather than reconstructed from
# pkgver, because for a prerelease the mapping is LOSSY AND ONE-WAY: v0.8.0-rc.1
# becomes 0.8.0rc1, and nothing can turn 0.8.0rc1 back into the tag it came from.
# A PKGBUILD that built its download URLs out of "v$pkgver" would 404 on every
# prerelease, which is exactly when nobody is watching.
[[ "$tag" =~ ^v[0-9][A-Za-z0-9.+-]*$ ]] \
  || { echo "error: '$tag' does not look like a release tag" >&2; exit 1; }

# A ref that does not resolve must fail here, naming itself, rather than four times
# over inside sha_of with a message about a missing file.
if [[ -n "$source_ref" ]]; then
  git -C "$repo_root" rev-parse --verify --quiet "${source_ref}^{object}" >/dev/null \
    || { echo "error: --source-ref '$source_ref' does not resolve in $repo_root (a shallow clone may not have fetched it)" >&2; exit 1; }
  echo "reading ntilde.desktop, ntilde.1, the icon and LICENSE from git ref $source_ref"
fi

assert_dlopen_map_covers_deb

# --- checksums -------------------------------------------------------------
# Exactly one of --tarball / --sha256. A tarball is hashed here; a bare sum is
# taken on trust from the caller (the release workflow, which has just uploaded
# the asset it is quoting).
if [[ -n "$tarball_path" && -n "$tarball_sha" ]]; then
  echo "error: pass --tarball or --sha256, not both" >&2; exit 2
fi
if [[ -n "$tarball_path" ]]; then
  [[ -f "$tarball_path" ]] || { echo "error: no such tarball: $tarball_path" >&2; exit 1; }
  tarball_sha="$(sha256sum "$tarball_path" | awk '{print $1}')"
fi
[[ -n "$tarball_sha" ]] || { echo "error: need --tarball <path> or --sha256 <sum>" >&2; exit 2; }
[[ "$tarball_sha" =~ ^[0-9a-f]{64}$ ]] \
  || { echo "error: --sha256 is not a SHA-256 hex digest: '${tarball_sha:0:80}'" >&2; exit 1; }

# WHY NO LOCAL SOURCE FILES: the desktop entry, man page, icon and licence are
# fetched from the tag over HTTPS instead of being copied into the AUR repo beside
# the PKGBUILD. Two reasons, one of them measured:
#   * packaging/linux/ntilde.desktop and ntilde.1 stay the single source of truth for
#     both Linux packages. A copy in the AUR repo is a copy that drifts.
#   * src/Ntilde.App/Assets/ntilde_icon.png is 662 KB (and is, despite the
#     extension, a JPEG - ImageMagick sniffs content, which is why build-deb.sh
#     never noticed). Committing that into an AUR repo re-uploads it on every
#     release bump for no benefit.
# The sums below are computed from the LOCAL checkout, so this script must run at
# the tag it is generating for - the release workflow checks out
# release_metadata.checkout_ref before calling it. If the two ever disagree,
# makepkg's own integrity check is what catches it, loudly, on the next build;
# smoke-test.sh runs a real makepkg against the real URLs for that reason.
# --source-ref closes the gap the paragraph above describes rather than documenting
# it. With a ref, the four sums are read from that ref's BLOBS via `git show`, so the
# PKGBUILD's sha256sums always describe the same bytes its raw.githubusercontent URLs
# serve - regardless of what the working tree happens to hold.
#
# That is not a convenience. Without it, CI on a branch that edits ntilde.desktop (or
# the man page, icon, or LICENSE) generates a PKGBUILD pinning the BRANCH's sum
# against the TAG's URL, and the smoke test fails an integrity check that has nothing
# to do with the change under review - a false red on the one lane meant to catch
# real ones. With no --source-ref the working tree is used, which is right for a
# release built at its own tag and for local experimentation.
sha_of() {
  local rel="$1" out
  if [[ -n "$source_ref" ]]; then
    # `git show` writes nothing and fails when the path is absent at that ref, which
    # under pipefail takes the whole substitution down - caught here so the message
    # names the file and the ref instead of surfacing as an empty digest.
    out="$(git -C "$repo_root" show "${source_ref}:${rel}" | sha256sum | awk '{print $1}')" \
      || { echo "error: cannot read $rel at git ref $source_ref" >&2; exit 1; }
  else
    [[ -f "$repo_root/$rel" ]] || { echo "error: missing source file: $rel" >&2; exit 1; }
    out="$(sha256sum "$repo_root/$rel" | awk '{print $1}')"
  fi
  # A digest assertion, not decoration: every failure mode above produces an empty or
  # partial value, and an unchecked one would sail into the PKGBUILD as a sha256sums
  # entry that can never match.
  [[ "$out" =~ ^[0-9a-f]{64}$ ]] \
    || { echo "error: computed no usable SHA-256 for $rel (got '${out:0:80}')" >&2; exit 1; }
  printf '%s\n' "$out"
}
desktop_sha="$(sha_of packaging/linux/ntilde.desktop)"
man_sha="$(sha_of packaging/linux/ntilde.1)"
icon_sha="$(sha_of src/Ntilde.App/Assets/ntilde_icon.png)"
license_sha="$(sha_of LICENSE)"

depends_block="$(print_depends | sed "s/^/         '/; s/\$/'/" | sed '1s/^ *//')"

# --- emit PKGBUILD ---------------------------------------------------------
mkdir -p "$out_dir"
out_dir="$(cd "$out_dir" && pwd)"

# Quoted heredoc: every $pkgdir/$srcdir/$pkgver below belongs to makepkg, not to
# this shell. The handful of values this script supplies are @TOKEN@ placeholders
# substituted immediately after, so there is no escaping to get subtly wrong.
cat > "$out_dir/PKGBUILD" <<'PKGBUILD_EOF'
# Maintainer: benyblack <noreply@github.com>
#
# GENERATED by packaging/arch/build-arch.sh in the Ntilde repository.
# Do not edit this file in the AUR repo - edit the generator and regenerate, or the
# next release overwrites your change. The generator is what gates depends= against
# the runtime-dlopen'd library table the Debian package shares.

pkgname=ntilde-bin
pkgver=@PKGVER@
pkgrel=1
pkgdesc='Modern terminal emulator with GPU-accelerated rendering, native SSH support and shell integration'
# x86_64 only, deliberately. The release publishes a linux-arm64 tarball, but no
# arm64 build of this project has been verified end to end (packaging/linux/README.md
# says so in as many words), and an AUR package that fails on a declared
# architecture is worse than one that declares less. Add aarch64 here, with a
# source_aarch64/sha256sums_aarch64 pair, once something has actually launched on it.
arch=('x86_64')
url='https://github.com/benyblack/ntilde'
license=('MIT')
depends=(@DEPENDS@)
optdepends=('xorg-xwayland: run under a Wayland compositor (the app uses Avalonia'"'"'s X11 backend)'
            'gnome-keyring: store SSH passwords via the Secret Service'
            'kwallet: store SSH passwords via the Secret Service')
makedepends=('imagemagick')
provides=('ntilde')
conflicts=('ntilde')
# !strip: the payload is a NativeAOT binary. build-deb.sh strips the bundled native
# libraries but pointedly not the AOT binary itself; makepkg's default strip pass
# makes no such distinction, so it is disabled here and package() does the same
# selective strip by hand. (The AOT binary in the published tarball arrives stripped
# already; the four bundled .so files do not.)
# !debug: there is no source to build a debug package from.
options=('!strip' '!debug')

_tag=@TAG@
_rid=linux-x64
_raw="https://raw.githubusercontent.com/benyblack/ntilde/${_tag}"

source=("ntilde.desktop::${_raw}/packaging/linux/ntilde.desktop"
        "ntilde.1::${_raw}/packaging/linux/ntilde.1"
        "ntilde_icon.png::${_raw}/src/Ntilde.App/Assets/ntilde_icon.png"
        "LICENSE-${pkgver}::${_raw}/LICENSE")
source_x86_64=("ntilde-${_rid}-${_tag}.tar.gz::${url}/releases/download/${_tag}/ntilde-${_rid}-${_tag}.tar.gz")

sha256sums=('@DESKTOP_SHA@'
            '@MAN_SHA@'
            '@ICON_SHA@'
            '@LICENSE_SHA@')
sha256sums_x86_64=('@TARBALL_SHA@')

# The release tarball is FLAT - `tar -czf ... -C artifacts/publish/$RID .` in
# release.yml, so it has no top-level directory and unpacking it in $srcdir would
# strew 25 files over the other sources. Unpack it into a subdirectory instead.
noextract=("ntilde-${_rid}-${_tag}.tar.gz")

prepare() {
  rm -rf "${srcdir}/bundle"
  mkdir -p "${srcdir}/bundle"
  bsdtar -xf "${srcdir}/ntilde-${_rid}-${_tag}.tar.gz" -C "${srcdir}/bundle"
}

package() {
  install -dm755 "${pkgdir}/usr/lib/ntilde"
  cp -a "${srcdir}/bundle/." "${pkgdir}/usr/lib/ntilde/"

  # Debug symbols never ship: StripSymbols=true emits a ~79 MB Ntilde.dbg
  # beside the AOT binary, and release.yml deletes it before packaging. Belt and
  # braces, the same way build-deb.sh keeps its own removal.
  find "${pkgdir}/usr/lib/ntilde" \( -name '*.pdb' -o -name '*.dbg' \) -delete

  # Mode normalisation, then re-assert the one file that must stay executable.
  # ORDER MATTERS: reversing these would have the blanket pass clobber the binary's
  # exec bit straight back off. cp -a preserved the publish tree's modes verbatim,
  # including the 0744 SkiaSharp and HarfBuzzSharp ship with - a shared library
  # needs read, never execute.
  find "${pkgdir}/usr/lib/ntilde" -type f -exec chmod 0644 {} +
  find "${pkgdir}/usr/lib/ntilde" -type d -exec chmod 0755 {} +
  chmod 0755 "${pkgdir}/usr/lib/ntilde/Ntilde"

  # Selective strip, standing in for the makepkg pass disabled by options=(!strip).
  # Every *.so* under the bundle, versioned sonames included - not four hardcoded
  # names, so a native library added later is stripped too without editing this.
  # The AOT binary is deliberately left alone.
  while IFS= read -r -d '' _so; do
    strip --strip-unneeded "${_so}"
  done < <(find "${pkgdir}/usr/lib/ntilde" -name '*.so*' -print0)

  install -dm755 "${pkgdir}/usr/bin"
  # /usr/bin/ntilde collides with python-ntildeclient (OpenStack), which ships its own
  # /usr/bin/ntilde. pacman refuses to install over it, so a user with that package
  # gets a clear file-conflict error rather than a silently shadowed command.
  ln -s /usr/lib/ntilde/Ntilde "${pkgdir}/usr/bin/ntilde"

  install -Dm644 "${srcdir}/ntilde.desktop" "${pkgdir}/usr/share/applications/ntilde.desktop"
  # Uncompressed: makepkg's zipman option gzips man pages itself. Pre-gzipping the
  # way build-deb.sh has to would leave a ntilde.1.gz.gz on a stock makepkg.conf.
  install -Dm644 "${srcdir}/ntilde.1" "${pkgdir}/usr/share/man/man1/ntilde.1"
  install -Dm644 "${srcdir}/LICENSE-${pkgver}" "${pkgdir}/usr/share/licenses/${pkgname}/LICENSE"

  # Icons derived at package time from the one committed source image, which stays
  # the single cross-platform source of truth - same principle as build-deb.sh and
  # packaging/macos/make-icns.sh. A failure to scale is fatal here rather than
  # warn-and-continue: `magick` is a declared makedepends, so a failure means
  # something is actually wrong, not that the environment is thin.
  local _size
  for _size in 16 32 48 64 128 256; do
    install -dm755 "${pkgdir}/usr/share/icons/hicolor/${_size}x${_size}/apps"
    magick "${srcdir}/ntilde_icon.png" -resize "${_size}x${_size}" \
      "${pkgdir}/usr/share/icons/hicolor/${_size}x${_size}/apps/ntilde.png"
    chmod 0644 "${pkgdir}/usr/share/icons/hicolor/${_size}x${_size}/apps/ntilde.png"
  done
}

# No .install scriptlet. desktop-file-utils and hicolor-icon-theme ship pacman hooks
# that refresh the desktop and icon caches, exactly as their dpkg triggers do on the
# Debian side, so there is nothing for a post_install to do. That reasoning only
# holds because hicolor-icon-theme is in depends= above - it owns the hicolor
# hierarchy these icons install into. Dropping it would not merely lose a cache
# refresh, it would make this comment false.
PKGBUILD_EOF

sed -i \
  -e "s|@PKGVER@|$pkgver|g" \
  -e "s|@TAG@|$tag|g" \
  -e "s|@TARBALL_SHA@|$tarball_sha|g" \
  -e "s|@DESKTOP_SHA@|$desktop_sha|g" \
  -e "s|@MAN_SHA@|$man_sha|g" \
  -e "s|@ICON_SHA@|$icon_sha|g" \
  -e "s|@LICENSE_SHA@|$license_sha|g" \
  "$out_dir/PKGBUILD"

# The depends array is multi-line, so it goes in with awk rather than sed -
# a sed replacement carrying newlines is a portability trap not worth taking.
awk -v block="$depends_block" '{ sub(/@DEPENDS@/, block); print }' \
  "$out_dir/PKGBUILD" > "$out_dir/PKGBUILD.tmp" && mv "$out_dir/PKGBUILD.tmp" "$out_dir/PKGBUILD"

# Nothing may reach the AUR with a placeholder left in it: an unsubstituted @TOKEN@
# is a silently broken PKGBUILD (makepkg would treat it as a literal), and this
# script's whole job is to produce a file a human never reads before it is pushed.
if grep -n '@[A-Z_]*@' "$out_dir/PKGBUILD"; then
  echo "error: unsubstituted placeholder(s) left in the generated PKGBUILD (listed above)" >&2
  exit 1
fi

echo "wrote $out_dir/PKGBUILD (pkgver=$pkgver, tag=$tag)"

# --- .SRCINFO --------------------------------------------------------------
# The AUR requires it and rejects a push without it. makepkg is the only thing that
# can generate it, so this half is skipped off-Arch with a loud note rather than
# faked - a hand-written .SRCINFO that disagrees with the PKGBUILD is worse than
# an absent one, which at least fails at push time instead of installing something
# other than what the PKGBUILD says.
#
# A failure to produce it is NOT fatal to this script: the PKGBUILD is already
# written and correct, and killing the run here would report "generation failed"
# for a file that generated fine. CI learned this the hard way - the generator
# tests run in a container as root, makepkg REFUSES to run as root, and both
# full-generation tests failed on a step that had already done its job.
srcinfo_note() {
  # DELETE FIRST, always. Every path into this function means "there is no .SRCINFO
  # for the PKGBUILD just written", and that must be true on disk, not merely in the
  # message. Two ways it would otherwise be false:
  #   * a failed makepkg leaves a ZERO-BYTE .SRCINFO, because `> .SRCINFO` creates
  #     the file before makepkg ever runs;
  #   * regenerating into a REUSED output directory leaves the PREVIOUS version's
  #     .SRCINFO in place beside a freshly overwritten PKGBUILD.
  # The second is the nastier one: the file is valid, non-empty, and describes the
  # wrong version, so it passes `test -s` and would be pushed to the AUR as metadata
  # for a package it does not match.
  rm -f "$out_dir/.SRCINFO"
  echo "note: $1" >&2
  echo "      .SRCINFO was not generated. Run 'makepkg --printsrcinfo > .SRCINFO' in" >&2
  echo "      $out_dir on an Arch host, as an unprivileged user, before pushing to the AUR." >&2
}
if ! command -v makepkg >/dev/null 2>&1; then
  srcinfo_note "makepkg not found"
elif [[ "$(id -u)" -eq 0 ]]; then
  # Checked explicitly rather than left to makepkg's own refusal, so the reason is
  # one line instead of a wall of makepkg boilerplate about catastrophic damage.
  srcinfo_note "running as root, and makepkg refuses to run as root"
elif ( cd "$out_dir" && makepkg --printsrcinfo > .SRCINFO ); then
  echo "wrote $out_dir/.SRCINFO"
else
  srcinfo_note "makepkg --printsrcinfo failed"
fi
