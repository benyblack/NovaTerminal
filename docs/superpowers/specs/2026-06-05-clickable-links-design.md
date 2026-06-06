# Clickable Links — Design

**Date:** 2026-06-05
**Status:** Approved (brainstorming) — ready for implementation planning

## Goal

Make links in the terminal "just work and look clickable." Two parts:

1. **Auto-detect plain-text URLs** (e.g. `https://…` printed by `ls`, `git`, logs) and make
   them clickable. Today these have no OSC 8 wrapping and are invisible to the existing link
   logic.
2. **Polish the existing OSC 8 experience** — hover underline, pointer cursor, and consistent
   discoverability so the user doesn't have to *know* to hold a modifier.

Architecture must be **expandable**: adding new kinds of detected links later (bare domains,
file paths, ticket IDs, …) must not require reworking the system.

## Prior art (why this shape)

| Emulator | Auto-detects | Architecture | Open gesture |
|---|---|---|---|
| Windows Terminal | scheme URLs (experimental, opt-out-able); OSC 8 always on | single matcher | hover underline + Ctrl+Click |
| WezTerm | scheme URLs + email→mailto | **configurable regex-rule list** | Ctrl/Cmd+Click |
| kitty | scheme list (`http https file ftp ssh git mailto …`) | configurable prefix list | Ctrl+Shift+Click |
| iTerm2 | scheme URLs + file paths + smart selection | configurable rule sets | Cmd+Click |
| Alacritty | scheme URLs (`hints`) | configurable regex hints | modifier/keyboard |
| GNOME/VTE | scheme URLs + email + bare `www.` | built-in regex set | Ctrl+Click |

Patterns: default detection is **scheme-based** (+ email); bare-domain detection is the
minority due to false positives; the well-engineered emulators use a **configurable rule list**;
interaction is universally **hover-underline + modifier-click to open** (plain click stays as
selection). This design = the WezTerm model: rule-list architecture shipping with scheme+email
defaults.

## What already exists (reused, not rebuilt)

- **OSC 8 explicit links**: parsed in `AnsiParser.HandleOsc` (`src/NovaTerminal.VT/AnsiParser.cs`),
  stored per-row in a side table (`TerminalRow` / `TerminalPage` `SetHyperlink`/`GetHyperlink`,
  `SmallMap<string>`), preserved across reflow, looked up via
  `TerminalBuffer.GetHyperlinkAbsolute(col, absRow)`. Opened on Ctrl+Click in
  `TerminalView.OnPointerPressed` (`src/NovaTerminal.App/Shell/TerminalView.cs:1662`).
- **Rendering** flows through a custom `TerminalDrawOperation` (`TerminalView.Render`,
  ~line 1533) that already receives transient overlay state such as `_selection`. The hover
  underline rides the same channel — no buffer mutation.

## New components

### 1. `UrlDetector` — the rule-list engine (extensibility seam)

Pure, UI-free class in `NovaTerminal.VT`.

- Holds an ordered `IReadOnlyList<LinkRule>`, where `LinkRule = { string Name, Regex Pattern,
  Func<Match,string> Resolve }`. `Resolve` maps a match to a final URI (the email rule resolves
  to `mailto:<addr>`; the scheme rule returns the match verbatim).
- `IReadOnlyList<LinkSpan> Detect(string lineText)` returns `LinkSpan = { int StartChar,
  int EndChar, string Uri }`.
- **Ships with two rules:**
  - **scheme rule**: matches `\b\w+://\S+`, then trims trailing punctuation and unbalanced
    closing brackets (`. , ; : ! ? ) ] }`) so `(see https://x.com).` yields `https://x.com`.
  - **email rule**: matches `\b[\w.+-]+@[\w-]+(\.[\w-]+)+\b` → `mailto:<match>`.
- **Expanding = appending a rule.** Bare `www.`/domains, file paths, Jira tickets, etc. slot in
  with no changes elsewhere.

### 2. Per-row detection cache

- Extract a row's display text from its cells, honoring wide cells and extended (surrogate-pair)
  text, while building a **char-offset → column** map (needed because a detected span is in
  character offsets but hit-testing/underlining works in columns).
- Run `UrlDetector.Detect` over that text; cache resulting spans keyed by a row content-version
  (reuse the row's existing dirty/version signal; recompute on change).
- Detection runs **only when the mouse enters an uncached visible row** — never on the VT write
  path. Cannot regress write throughput.

### 3. Hover state + hit-testing

In `TerminalView.OnPointerMoved`, **non-mouse-reporting path only** (mouse-reporting apps keep
current behavior):

- Map cursor position → `(absRow, col)` via existing `ScreenToTerminal` + scroll offset.
- Look up **OSC 8 link first** (`GetHyperlinkAbsolute`), else a **detected span** for that cell.
- On a hit: set `_hoveredLink = { absRow, startCol, endCol, uri }`, set cursor to
  `StandardCursorType.Hand`, and `InvalidateVisual()` **only when the span changes**.
- On no hit / pointer leave: clear `_hoveredLink`, restore cursor, invalidate if it changed.
- Underline shows on plain hover (no modifier required to *see* the link).

### 4. Render overlay

- Pass `_hoveredLink` into `TerminalDrawOperation` (alongside `_selection`).
- The draw op underlines the hovered cells using `PixelGrid.YForUnderline`.
- Transient UI state only — cell `Underline` flags are never mutated.

### 5. Unified open path + safety

- Refactor the existing Ctrl+Click OSC 8 block into one `TryOpenLink(string uri)` used by both
  OSC 8 and detected links.
- **Modifier:** `Ctrl` on Windows/Linux, `Cmd` (Meta) on macOS.
- **Scheme allowlist before opening:** `http`, `https`, `mailto`, `file`. Anything else is not
  launched, so detected text cannot shell-execute arbitrary schemes via
  `Process.Start(UseShellExecute=true)`.

### 6. Settings toggle

- A setting to enable/disable **auto-detection** (default: on). OSC 8 links remain always-on.
  Mirrors Windows Terminal treating detection as opt-out-able; low cost, fits the existing
  config story.

## Scope / YAGNI (v1)

- **Single visual-row spans only.** A URL wrapped across two rows opens only the visible portion.
  Future extension: resolve spans over logical/reflowed lines.
- No keyboard-driven link navigation (Alacritty-style hints) in v1.
- No bare-domain / file-path rules in v1 — the architecture supports them; defaults stay tight to
  keep false positives near zero.

## Testing

- **`UrlDetector` unit tests (bulk):** scheme matches; email→mailto; trailing-punctuation and
  bracket trimming; no false positive on `file.name` / `a.b` / version strings; multiple links per
  line; char-offset → column mapping across wide chars and surrogate pairs.
- **Hit-test + open path:** via the existing headless `TerminalView` test harness — hover sets the
  span, click resolves the right URI, scheme allowlist rejects disallowed schemes, OSC 8 takes
  precedence over a detected span on the same cell.

## Files likely touched

- `src/NovaTerminal.VT/UrlDetector.cs` (new), `LinkRule` / `LinkSpan` types.
- `src/NovaTerminal.App/Shell/TerminalView.cs` — hover state, hit-testing, `TryOpenLink`,
  pass-through to draw op.
- `TerminalDrawOperation` (in `TerminalView.cs` / rendering) — draw hover underline.
- Settings surface — auto-detection toggle.
- Tests under the VT test project + App headless harness.
