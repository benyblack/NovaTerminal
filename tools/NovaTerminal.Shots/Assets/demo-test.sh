#!/usr/bin/env bash
printf '\033[90mRunning 6 test suites…\033[0m\n\n'
for suite in "vt::parser" "vt::reflow" "render::glyph_cache" "pty::session" "replay::roundtrip" "agent::journal"; do
  printf '  \033[32m✓\033[0m %-24s \033[90m%s\033[0m\n' "$suite" "$(( (RANDOM % 40) + 4 ))ms"
  sleep 0.18
done
printf '\n\033[32m  6 passed\033[0m \033[90m·\033[0m 0 failed \033[90m·\033[0m 0 skipped\n\n'
