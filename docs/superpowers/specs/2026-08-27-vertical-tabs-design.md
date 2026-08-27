# Vertical Tab Sidebar — Design

**Date:** 2026-08-27
**Status:** Approved (brainstorming complete)

## Problem

Users who run several AI agent CLIs (Claude Code, Codex, aider, …) in parallel keep one
NovaTerminal tab per agent. The horizontal tab strip fails them three ways:

1. With many tabs the strip overflows and titles truncate, so tabs stop being identifiable.
2. There is no way to tell at a glance which agent is still working, which finished, and
   which is waiting for input.
3. There is no overview surface — nothing that feels like a monitor over all sessions.

## Solution overview

An optional **vertical tab sidebar**: a toggleable tab-strip orientation. In vertical mode
the tab headers move out of the title bar into a resizable left sidebar where each row
shows the session title, a heuristic status indicator, and a one-line preview of the most
recent output. Horizontal mode (today's UI) remains the default and the two modes are
mutually exclusive — one `TabControl`, one source of truth.

Status is inferred from terminal output heuristics (no cooperation required from the
program in the tab), building on the pane events MainWindow already consumes
(`OutputReceived`, `BellReceived`, `ProcessExited`) and the existing per-tab state
(`HasActivity`, `HasBell`) with its batched visual-refresh queue.

## Settings

- `TerminalSettings.TabStripOrientation` — enum `Horizontal` (default) | `Vertical`.
- `TerminalSettings.VerticalTabStripWidth` — persisted sidebar width in px (default ~220,
  clamped to a sane min/max).

Toggle surfaces:

- Settings window (Appearance/Window section).
- Command palette entry ("Toggle vertical tabs").
- Keyboard shortcut registered in `ShortcutCatalog`.

Known integration gotchas, handled explicitly:

- Register the new fields in McpServer `SettingsTools`, or the two GATING settings
  drift-guard tests fail.
- Verify whether the `TerminalPane.ApplySettings` effectiveSettings whitelist applies.
  Expected: it does not (these are window-level, not pane-level, settings), but the plan
  must confirm rather than assume.
- `MainWindow.SetupCommandPalette()` runs lazily (palette open / settings save), not at
  startup — any initial-window UI for the new mode must be initialized at ctor end.

## Layout & rendering

Single `TabControl` ("Tabs" in `MainWindow.axaml`), two presentations selected by the
orientation setting:

- **Horizontal (unchanged):** headers in the 36px title-bar strip, horizontal
  `StackPanel`, hidden-tab overflow counting, current styles.
- **Vertical:** the control theme swaps to dock the header `ItemsPresenter` in a left
  sidebar — vertical `StackPanel`, vertical `ScrollViewer`, user-resizable via a drag
  grip (width persisted). Terminal content fills the remaining space.

Title bar in vertical mode: keeps the drag region, window controls, and title-bar icons;
the tab region is simply empty. `TitleBarLayoutResolver` gets a "no tabs" mode so the
reserved tab area collapses.

Because the same `TabItem` instances are used in both modes, existing behavior carries
over untouched: MRU cycling, broadcast tabs, middle-click close, right-click context menu,
session restore, pane zoom. Horizontal-only logic (hidden-tab counting,
`MinimumTabHeaderRightReserve`) is skipped in vertical mode; the sidebar scrolls instead.

Switching orientation at runtime re-templates the `TabControl` and rebuilds header hosts;
it must not dispose or recreate panes/sessions.

## Row content (vertical mode only)

Each sidebar row renders:

1. **Status indicator** — see status model below.
2. **Title** — the same header text as today, with far more room before ellipsis.
3. **Preview line** — the most recent non-empty output line of the tab's active pane,
   secondary-text styling, single line, ellipsized. Updated on `OutputReceived` but
   throttled through the existing `QueueTabVisualRefresh` batching so bursty agent output
   does not cause per-chunk UI work.

Horizontal mode rows are unchanged.

## Status model

A small pure class `TabStatusTracker` (one per tab, injectable clock, no Avalonia
dependencies) consumes the already-wired pane events and produces one of three states:

- **Working** — output received within the last ~2 seconds (timer-decayed, coalesced).
  Rendered as a pulse/spinner-style indicator.
- **Attention** — a bell was received, **or** the tab transitioned Working → quiet while
  unselected (interpretation: the agent streamed output and stopped — finished or waiting
  for input). Rendered as an accent dot. Sticky until the tab is selected.
- **Idle** — everything else. Selecting a tab clears Attention/activity, matching the
  existing `HasActivity`/`HasBell` clearing behavior.

The heuristic is deliberately approximate; it works with any agent CLI with zero setup.
If exact status is ever wanted, an explicit signal source (OSC sequences /
shell-integration marks) can feed the same tracker — out of scope for this feature.

## Error handling & performance

- All tracker input arrives via events MainWindow already subscribes to; no new
  subscriptions to session internals.
- Preview-line extraction reads the terminal buffer's last non-empty row on the UI thread
  inside the batched refresh, never per output chunk.
- Working→Idle decay uses a single coalesced timer pass (piggybacking on the existing
  refresh batching), not one timer per tab.
- A disposed/closed tab unhooks its tracker with the existing pane-unhook path.

## Testing

- **Unit:** `TabStatusTracker` state transitions with an injected clock (working decay,
  bell → attention, quiet-while-unselected → attention, select clears).
- **Headless window tests** (TestMainWindowFactory + `Show()` + `RunJobs()` pattern):
  orientation toggle re-templates without disposing sessions; vertical rows show
  title/status/preview; width persistence round-trips; title-bar tab region collapses.
- **Drift guards:** new settings registered in McpServer `SettingsTools`; settings JSON
  round-trip.
- Run targeted test projects, not whole-solution (`scripts/build.ps1`).

## Out of scope (YAGNI)

- Live terminal thumbnails per row.
- Tab grouping, pinning, or per-agent icons.
- Explicit OSC/shell-integration status protocol (tracker is the future plug-in point).
- Right-side placement (left only for v1).
