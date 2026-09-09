#!/usr/bin/env bash
# One-time bootstrap for the Homebrew tap lane. See packaging/homebrew/README.md.
#
# Creates the tap repo (default benyblack/homebrew-tap) and pushes the cask for one
# stable release tag. After this has run once, release.yml's "Update Homebrew tap"
# step keeps the cask current on every stable tag, given HOMEBREW_TAP_TOKEN.
#
# Prerequisites: gh authenticated with a token that can create repos under the
# target owner, and either sha256sum (Git Bash/Linux) or shasum (macOS) on PATH.
#
# usage: bootstrap-tap.sh <tag> [tap-repo]
#   <tag>       stable release tag to package, e.g. v0.7.0 (prerelease tags are refused)
#   [tap-repo]  GitHub repo to create; default benyblack/homebrew-tap

set -euo pipefail

if [[ $# -lt 1 || $# -gt 2 || "$1" == "-h" || "$1" == "--help" ]]; then
  echo "usage: $0 <tag> [tap-repo]   e.g. $0 v0.7.0 benyblack/homebrew-tap" >&2
  exit 2
fi

tag="$1"
tap="${2:-benyblack/homebrew-tap}"
version="${tag#v}"

if [[ "$version" == *-* ]]; then
  echo "refusing $tag: homebrew-cask stable versions must not be prereleases" >&2
  exit 1
fi

here="$(cd "$(dirname "$0")" && pwd)"
template="$here/Casks/novaterminal.rb"

asset="NovaTerminal-osx-arm64-$tag.zip"
url="https://github.com/benyblack/NovaTerminal/releases/download/$tag/$asset"

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

echo "Downloading $url"
gh release download "$tag" --repo benyblack/NovaTerminal --pattern "$asset" --dir "$tmp" --clobber
if [[ ! -f "$tmp/$asset" ]]; then
  echo "release $tag carries no $asset - is this a tag with published macOS assets?" >&2
  exit 1
fi

if command -v sha256sum >/dev/null 2>&1; then
  sha="$(sha256sum "$tmp/$asset" | cut -d' ' -f1)"
else
  sha="$(shasum -a 256 "$tmp/$asset" | cut -d' ' -f1)"
fi
echo "sha256($asset) = $sha"

echo "Creating $tap (continues if it already exists)"
gh repo create "$tap" --public --description "Homebrew tap for NovaTerminal" || true

git clone "https://github.com/$tap.git" "$tmp/tap"
mkdir -p "$tmp/tap/Casks"
sed -e "s/__VERSION__/$version/" -e "s/__SHA256__/$sha/" "$template" > "$tmp/tap/Casks/novaterminal.rb"
# Same guard as the release lane: a broken template must fail here, not at brew install.
if command -v ruby >/dev/null 2>&1; then
  ruby -c "$tmp/tap/Casks/novaterminal.rb"
fi

cd "$tmp/tap"
if git diff --quiet; then
  echo "Cask in $tap already points at $version; nothing to push."
  exit 0
fi
git config user.name "$(gh api user --jq .login)"
git config user.email "$(gh api user --jq .id)+$(gh api user --jq .login)@users.noreply.github.com"
git add Casks/novaterminal.rb
git commit -m "novaterminal $version"
git push origin HEAD

# brew strips the homebrew- prefix from the repo name to form the tap identifier:
# benyblack/homebrew-tap -> benyblack/tap. A repo without the prefix keeps its name
# as-is (brew only auto-resolves homebrew-* repos, so the case keeps this honest).
repo_part="${tap#*/}"
case "$repo_part" in homebrew-*) repo_part="${repo_part#homebrew-}" ;; esac
tap_id="${tap%%/*}/$repo_part"
echo
echo "Done. Install with:"
echo "  brew install --cask $tap_id/novaterminal"
