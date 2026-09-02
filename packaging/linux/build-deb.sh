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
#
# The '~' in the replacement MUST be escaped: bash subjects the replacement text of
# ${param/pattern/string} to tilde expansion, so an unescaped '~' becomes the home
# directory ("/root", "/c/Users/<user>", ...) instead of a literal tilde. Verified
# against bash 5.1 (ubuntu:22.04) and 5.2 (git-bash) - both expand it unescaped.
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

# Preflight the tools dependency derivation needs. Without this, a missing 'file' or
# 'dpkg-query' does not fail the build - find/grep just match nothing, and
# 2>/dev/null + `|| true` around dpkg-query swallow the rest - so the script exits 0
# having shipped a .deb with an incomplete Depends: (missing libstdc++6, libssl3,
# whatever libSkiaSharp.so really links), an install that succeeds and then fails at
# load time on a clean machine with nothing in the build log to explain why. That is
# exactly the failure mode the two-mechanism dependency design exists to prevent.
# strip is here for the same reason: without it, the strip call below would need
# its own silent-skip fallback, and a .deb built on a host missing binutils would
# ship unstripped .so files with nothing in the build log to explain why.
for _tool in dpkg-deb dpkg-query file ldd strip; do
  command -v "$_tool" >/dev/null 2>&1 || { echo "missing required tool: $_tool" >&2; exit 1; }
done

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
  local -a unresolved=()
  local elf soname path resolved pkg

  while IFS= read -r elf; do
    # ldd's second field ("=>") is a fixed separator, kept only so `path` lands in
    # $3 - it is never used itself. For a resolved line ("libc.so.6 => /path (0x...)")
    # $3 is the absolute path. For an UNRESOLVED line ("libfoo.so.1 => not found")
    # $3 is the literal word "not" - that is the signal distinguished below, not
    # some ambient "junk path" the old `-e "$path"` check was quietly absorbing.
    while read -r soname _ path; do
      [[ -n "$soname" ]] || continue
      if [[ "$path" == "not" || -z "$path" ]]; then
        # Previously silently dropped here (`[[ -n "$path" && -e "$path" ]] ||
        # continue` treated "not" exactly like a nonexistent path, discarding the
        # exact signal the two-mechanism dependency design exists to surface: an
        # unresolved link means Depends: is about to ship incomplete). Loud now.
        unresolved+=("$soname needed by $elf")
        continue
      fi
      [[ -e "$path" ]] || continue
      resolved="$(readlink -f "$path")"
      pkg="$(dpkg-query -S "$resolved" 2>/dev/null | head -1 | cut -d: -f1 || true)"
      # dpkg-query can answer with a comma-separated list; take the first name.
      pkg="${pkg%%,*}"
      [[ -n "$pkg" ]] && found+=("$pkg")
    done < <(ldd "$elf" 2>/dev/null | awk '/=>/ { print $1, $2, $3 }')
  done < <(find "$root" -type f -exec sh -c 'file -b "$1" | grep -q "^ELF"' _ {} \; -print)

  if ((${#unresolved[@]})); then
    printf 'error: unresolved shared library dependency: %s\n' "${unresolved[@]}" >&2
    return 1
  fi

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
# Command substitution, not `< <(...)`: a process substitution's exit status is
# invisible to the loop that reads it (bash does not propagate it, and `set -e`
# does not catch it either), so derive_linked_depends's new `return 1` on an
# unresolved soname would be silently swallowed by the very construct meant to
# consume its output. Capturing into a variable first makes `||` below real.
linked_pkgs="$(derive_linked_depends "$publish_dir")" \
  || { echo "build-deb.sh: dependency derivation failed (see unresolved sonames above)" >&2; exit 1; }
while IFS= read -r pkg; do
  [[ -z "$pkg" || "$pkg" == "libc6" ]] && continue
  depends+=", $pkg"
done <<< "$linked_pkgs"
for pkg in "${DLOPEN_DEPENDS[@]}"; do
  # -qE, not -q: the pattern uses extended-regex alternation `(^|, )...(,|$)`, which a
  # basic regex (grep -q's default) treats as literal parentheses/pipe and can never
  # match - every dlopen dependency would be appended unconditionally, duplicating any
  # package ldd already found (lintian flags a duplicated Depends relation).
  grep -qE "(^|, )$pkg(,|$)" <<<"$depends" || depends+=", $pkg"
done

# --- stage the tree --------------------------------------------------------
install -d "$stage/DEBIAN"
install -d "$stage/usr/lib/novaterminal"
install -d "$stage/usr/bin"
install -d "$stage/usr/share/applications"
install -d "$stage/usr/share/man/man1"
install -d "$stage/usr/share/doc/novaterminal"

cp -a "$publish_dir/." "$stage/usr/lib/novaterminal/"
# A .deb ships no build leftovers; the AOT publish can contain debug symbols.
find "$stage/usr/lib/novaterminal" -name '*.pdb' -delete
find "$stage/usr/lib/novaterminal" -name '*.dbg' -delete

# Strip debug symbols from the bundled native libraries (SkiaSharp, HarfBuzzSharp,
# the Rust natives) - standard Debian practice, and lintian's
# unstripped-binary-or-object flags every one of them as an ERROR otherwise.
# Deliberately every *.so* under the bundle (versioned sonames like libfoo.so.1
# included, not just the unversioned dev-symlink shape), not four hardcoded
# names, so a future native library added here is stripped too without editing
# this script again - '*.so' alone would silently miss a real .so.N and leave
# it to redden lintian on the next unrelated change.
# Deliberately NOT the NovaTerminal AOT binary itself: lintian does not flag it,
# and NativeAOT output is not something to strip-and-hope on.
while IFS= read -r -d '' so; do
  strip --strip-unneeded "$so"
done < <(find "$stage/usr/lib/novaterminal" -name '*.so*' -print0)

# cp -a inherits every mode bit from the publish directory verbatim, including
# the 0744 SkiaSharp/HarfBuzzSharp ship with - lintian correctly flags a shared
# library carrying an execute bit (shared-library-is-executable): the dynamic
# loader only needs read permission to mmap a .so, never execute. Normalise every
# regular file in the bundle to 0644 and every directory to 0755, THEN re-assert
# 0755 on the one file that must stay executable: the entry point binary. Order
# matters - reversing these two steps would have the blanket pass clobber the
# binary's own exec bit right back off.
find "$stage/usr/lib/novaterminal" -type f -exec chmod 0644 {} +
find "$stage/usr/lib/novaterminal" -type d -exec chmod 0755 {} +
chmod 0755 "$stage/usr/lib/novaterminal/NovaTerminal"

ln -s /usr/lib/novaterminal/NovaTerminal "$stage/usr/bin/nova"
install -m 0644 "$here/nova.desktop" "$stage/usr/share/applications/novaterminal.desktop"
gzip -9nc "$here/nova.1" > "$stage/usr/share/man/man1/nova.1.gz"
chmod 0644 "$stage/usr/share/man/man1/nova.1.gz"

# --- icons -----------------------------------------------------------------
# Derived at packaging time from the one committed PNG, which stays the single
# cross-platform source of truth (same principle as packaging/macos/make-icns.sh).
icon_src="$repo_root/src/NovaTerminal.App/Assets/nova_icon.png"

# A MISSING ICON SOURCE IS FATAL - it used to warn and continue, which meant this
# script could exit 0 having produced a package with no /usr/share/icons/hicolor/**
# at all. Nothing downstream catches that: desktop-file-validate does not check that
# `Icon=novaterminal` resolves to an installed file, and lintian's icon-size-mismatch
# is a warning, so `--fail-on error` passes it too. Spec acceptance criterion 5
# ("NovaTerminal appears in the app menu with its icon") would then have zero
# coverage while ci.yml's change-detection pattern watches nova_icon.png, implying
# coverage that did not exist. Same reasoning as the missing-scaler case below: an
# iconless package is not a degraded package, it is a broken one.
if [[ ! -f "$icon_src" ]]; then
  echo "error: icon source not found at $icon_src - cannot build a valid hicolor icon theme, and a package with no icon fails the app-menu acceptance criterion" >&2
  exit 1
fi

# A missing resize tool is fatal, not a warn-and-continue case like an individual
# failed resize below: with no tool at all, EVERY size would install the same
# unscaled source file unchanged into all six hicolor buckets - a broken icon theme
# (wrong sizes everywhere) shipped silently, which is exactly what the spec's
# app-menu-icon acceptance criterion exists to catch. A single size occasionally
# failing to scale is tolerable degradation; having no scaler at all is not.
if command -v magick >/dev/null 2>&1; then
  resize() { magick "$1" -resize "$2x$2" "$3"; }        # ImageMagick 7
elif command -v convert >/dev/null 2>&1; then
  resize() { convert "$1" -resize "$2x$2" "$3"; }       # ImageMagick 6 (ubuntu-22.04)
else
  echo "error: no image resize tool found (need ImageMagick 'magick' or 'convert') - cannot build a valid hicolor icon theme" >&2
  exit 1
fi

# The per-size warn-and-continue fallback below is DELIBERATE and stays: one bucket
# failing to scale should install the unscaled source rather than fail the build.
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

# --- lintian overrides -------------------------------------------------------
# SkiaSharp's native build statically links freetype/libjpeg/libpng, and
# NativeAOT statically links zlib into the published binary. Both are inherent
# to what a self-contained bundle IS - there is no "unbundle and Depend on the
# system library" option available here the way there would be for a normally
# source-built Debian package. Named tags and exact paths only, deliberately no
# wildcard override: a genuinely new embedded library pulled in by a future
# SkiaSharp/AOT bump must still trip this gate rather than being silently waved
# through by an override that was written to cover today's four findings.
#
# No brackets around the path: verified empirically against real lintian
# (2.114.0, ubuntu-22.04's apt version) - an override written as
# "embedded-library freetype [path]" (matching the printed E: line literally)
# comes back as `mismatched-override`, because lintian's own info field for
# this tag has no brackets. The printed message adds them for readability; the
# override text must match the tag's raw info field, not the rendered message.
install -d "$stage/usr/share/lintian/overrides"
cat > "$stage/usr/share/lintian/overrides/novaterminal" <<'EOF'
# SkiaSharp's prebuilt native binary statically links freetype. Vendored
# upstream by the SkiaSharp NuGet package - not something this script builds
# or can unbundle.
novaterminal: embedded-library freetype usr/lib/novaterminal/libSkiaSharp.so
# Same as above: SkiaSharp statically links libjpeg.
novaterminal: embedded-library libjpeg usr/lib/novaterminal/libSkiaSharp.so
# Same as above: SkiaSharp statically links libpng.
novaterminal: embedded-library libpng usr/lib/novaterminal/libSkiaSharp.so
# NativeAOT statically links zlib into the published binary. A self-contained
# AOT publish has no "link against the system libz at runtime" mode to fall
# back to - this is what --self-contained true -p:PublishAot=true produces.
novaterminal: embedded-library zlib usr/lib/novaterminal/NovaTerminal
EOF
chmod 0644 "$stage/usr/share/lintian/overrides/novaterminal"

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
