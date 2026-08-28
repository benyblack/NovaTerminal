#!/usr/bin/env bash
# Generate a macOS .icns from the source PNG icon.
#
# Usage: make-icns.sh <source.png> <output.icns>
#
# Runs on macOS only (sips + iconutil ship with the OS). The PNG stays the single
# source of truth for the app icon on every platform; this script derives the
# Apple-specific format at packaging time instead of committing a binary to the
# repo. nova_icon.png is 1024x1024, so every iconset size is a high-quality
# downscale.
set -euo pipefail

if [[ "$(uname)" != "Darwin" ]]; then
    echo "make-icns.sh must run on macOS (needs sips and iconutil)." >&2
    exit 1
fi

if [[ $# -ne 2 ]]; then
    echo "Usage: $0 <source.png> <output.icns>" >&2
    exit 1
fi

source_png=$1
output_icns=$2

if [[ ! -f "$source_png" ]]; then
    echo "Source icon not found: $source_png" >&2
    exit 1
fi

workdir=$(mktemp -d)
trap 'rm -rf "$workdir"' EXIT
iconset="$workdir/icon.iconset"
mkdir -p "$iconset"

# Apple's required iconset layout: each size plus its @2x retina variant. sips -z
# takes <height> <width>; both are square here.
#
# -s format png is load-bearing: the source asset is JPEG data despite its .png
# extension (JFIF magic bytes), and without an explicit format sips sniffs the
# input, warns "Output file suffix should be jpg", writes JPEG bytes into the
# .png-named iconset files, and iconutil then fails with "Failed to generate
# ICNS". Forcing the output format makes the script indifferent to what the
# source really is.
resize() {
    local px=$1 name=$2
    sips -s format png -z "$px" "$px" "$source_png" --out "$iconset/$name.png" >/dev/null
}

resize 16   icon_16x16
resize 32   icon_16x16@2x
resize 32   icon_32x32
resize 64   icon_32x32@2x
resize 128  icon_128x128
resize 256  icon_128x128@2x
resize 256  icon_256x256
resize 512  icon_256x256@2x
resize 512  icon_512x512
resize 1024 icon_512x512@2x

mkdir -p "$(dirname "$output_icns")"
iconutil -c icns "$iconset" -o "$output_icns"

echo "Wrote $output_icns"
