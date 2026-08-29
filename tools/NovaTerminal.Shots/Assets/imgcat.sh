#!/usr/bin/env bash
# Fabricated for this demo workspace, trimmed from iTerm2's own imgcat: emits one local file as
# an OSC 1337 File= inline image, so `bash scripts/imgcat.sh assets/nova-logo.png` reads the way
# a real user's command would. No machine-derived data - just the file's own bytes and name.
set -euo pipefail

if [ $# -ne 1 ]; then
  echo "usage: imgcat.sh <path>" >&2
  exit 1
fi

file="$1"
name_b64=$(basename -- "$file" | base64 | tr -d '\n')
size=$(wc -c < "$file")
data_b64=$(base64 -- "$file" | tr -d '\n')

printf '\033]1337;File=name=%s;size=%s;inline=1:%s\a\n' "$name_b64" "$size" "$data_b64"
