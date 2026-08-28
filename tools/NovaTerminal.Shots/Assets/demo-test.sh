#!/usr/bin/env bash
# Per-line pacing is opt-in via NOVA_SHOTS_PACE, not always-on. A still capture only ever
# needs the finished transcript, and ShotContext.WaitForQuiet's 600ms settle window has to
# see genuinely quiet output between chunks - real per-line sleeps just add a way for a
# loaded machine to blow that budget for no benefit. Only ClipAgentScenario, which wants a
# real shell progressing on camera rather than one instant burst, sets this before its pane
# opens (env vars only take effect for a shell spawned after they are set).
#
# Timings/separators use SGR 2 (faint), not SGR 90 (bright black). Bright black is a fixed
# palette entry - TerminalDrawOperation.ResolveCellForeground looks it up per theme - and
# Solarized Dark's happens to sit at ~#073642 against its own ~#002B36 background, close
# enough to be functionally invisible in that theme's themes-grid tile. Faint instead blends
# whatever the cell's actual foreground is 50% toward its actual background
# (TerminalDrawOperation.cs's BlendTowards call gated on IsFaint), so it is dimmed relative
# to that theme's own contrast rather than pinned to one palette's specific dark grey - it
# reads as "quieter than the checkmarks" in every theme instead of disappearing in one.
printf '\033[2mRunning 6 test suites…\033[0m\n\n'
for suite in "vt::parser" "vt::reflow" "render::glyph_cache" "pty::session" "replay::roundtrip" "agent::journal"; do
  printf '  \033[32m✓\033[0m %-24s \033[2m%s\033[0m\n' "$suite" "$(( (RANDOM % 40) + 4 ))ms"
  if [ -n "${NOVA_SHOTS_PACE:-}" ]; then
    # Relies on an external `sleep` resolving on PATH in the demo shell - not vendored,
    # not verified at capture time. Deferred: flagged for the final review, not fixed here.
    sleep 0.18
  fi
done
printf '\n\033[32m  6 passed\033[0m \033[2m·\033[0m 0 failed \033[2m·\033[0m 0 skipped\n\n'
