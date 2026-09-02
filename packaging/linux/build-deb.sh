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
  local elf path resolved pkg

  while IFS= read -r elf; do
    # ldd's second field (soname) is unused here - only the resolved path matters.
    while read -r _ path; do
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
