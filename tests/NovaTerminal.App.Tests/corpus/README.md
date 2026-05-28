# ANSI Corpus

This folder contains static replay assets used by parser regression tests.
Current `.rec` files are **synthetic guard traces** (handcrafted minimal sequences)
designed to catch parser regressions in CSI leader/intermediate handling.

## Capture Guidance

Use `PtyRecorder` to capture raw PTY traffic from real sessions. Recommended capture settings:

- Window size: `120x40`
- `TERM=xterm-256color`
- UTF-8 locale
- Record raw PTY output (do not sanitize escapes)

When generating real captures, record at least:

- `opencode` startup
- `btop` navigation
- `yazi` navigation
- `ranger` navigation

## Notes

- Corpus files are committed static test assets.
- CI must never spawn external terminal applications to generate them.

## Replacing Synthetic Traces With Real Captures

Replace synthetic traces when you need broader real-world coverage:

1. Capture offline with `PtyRecorder` using the settings above.
2. Save files with clear names (for example `real_opencode_startup.rec`).
3. Keep existing synthetic files as fast guard rails, or update tests explicitly.
4. Commit both capture files and any snapshot/test updates in one change.
