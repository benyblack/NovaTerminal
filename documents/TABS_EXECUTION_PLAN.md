# NovaTerminal Tabs Execution Plan

Date: 2026-02-14  
Status: In Execution (M1/M2 complete; M3 foundation implemented; nightly stability gate pending)

User manual: `documents/TABS_USER_MANUAL.md`

## Execution Status Snapshot (2026-02-14)

Completed in code + tests:
- M1 core behavior implemented: stable `TabId`, close policy flow, `Ctrl+W`/`Ctrl+Shift+W`, MRU switching, overflow tab list, selected-tab auto-scroll.
- M2 UX behavior implemented: title precedence/truncation uniqueness hints, activity/bell/exit indicators, context menu actions, pin/protect, automation labels.
- M3 foundation implemented:
  - tamper-evident workspace bundle export/import (SHA-256 hash verified)
  - bundle open/apply flow (without forced persistence)
  - current-session bundle export
  - shareable workspace templates (save/list/apply)
  - per-profile tab template rules with auto-apply
  - managed policy hooks (import/export allow flags, max tabs, SSO placeholder gates)
  - audit trail for workspace/bundle/template operations
- Instrumentation implemented:
  - `MainWindow` tab switch/visual/automation timings
  - `TerminalView` active timer tracking and hidden invalidation pressure
  - `SessionManager` save/restore timing and payload bytes
- CI gating implemented:
  - PR: tab perf smoke lane
  - Nightly: stress/perf/latency/render-metrics lane
  - metrics artifact emission for perf lanes
- New acceptance tests added:
  - core tab behavior tests (MRU, overflow math, truncation/suffix behavior)
  - close policy matrix tests
  - tab performance budget smoke tests
  - title resolution persistence tests
  - 5,000-iteration lifecycle stress loop test
  - workspace bundle integrity/policy tests
  - workspace policy manager tests
  - tab template rule tests

Remaining before declaring Free tab work complete:
- Run and pass 7 consecutive nightly stress runs.
- Confirm no open P0/P1 tab correctness bugs after nightly window.
- Keep replay/VT parity green while nightly stress runs are accumulated.

## Objective

Ship a **flawless Free tab experience under stress** before any paid tab features.

Primary principle:
- correctness and performance are release blockers
- feature count is secondary

## Scope

In scope:
- tab create/close/switch correctness
- MRU switching and overflow handling
- tab identity/title/activity model
- safe close behavior for running processes
- session restore reliability for tabs

Out of scope until Free gates pass:
- tab detach to new window
- premium workspace/rules features
- enterprise policy/SSO packaging

## Current Baseline (Code Areas)

- `NovaTerminal/MainWindow.axaml.cs`: tab lifecycle, switching, keybindings, tab visuals, restore wiring
- `NovaTerminal/MainWindow.axaml`: tab host UI/template
- `NovaTerminal/App.axaml`: global `TabItem` style/template
- `NovaTerminal/Core/SessionManager.cs`: save/restore tab+pane session state
- `NovaTerminal/Core/SessionModels.cs`: persisted tab/pane schema
- `NovaTerminal/Controls/TerminalPane.axaml.cs`: title sources (OSC/CWD/profile), PTY pane lifecycle
- `NovaTerminal/Core/TerminalView.cs`: per-pane rendering, timers, invalidation/resize path
- `NovaTerminal/Core/RustPtySession.cs`: PTY output/input lifecycle

## Release Gates (Global)

1. No tab correctness regressions (create/close/switch/restore).
2. No perf regressions under stress (10+ active tabs).
3. Replay/VT correctness suites stay green.
4. Memory and timer lifecycle must stay stable after close/reopen loops.

## Performance Budgets (Free)

- Tab switch latency: p95 < 35ms with 10 active streaming tabs.
- New tab create latency: p95 < 120ms (warm profile).
- 20-tab background stream: active tab input latency p95 < 30ms.
- No unbounded memory growth after 5,000 tab create/close cycles.
- No runaway background timer loops after tab close.

## Milestone Plan

## M1 - Free Blockers: Correctness + Stress Safety (P0)

### Goal

Guarantee deterministic tab behavior and stable performance under load.

### Tasks

- Introduce explicit tab state model in `MainWindow`:
  - stable `TabId` persisted in session
  - active/inactive/activity flags
- Implement first-class tab close flow:
  - `CloseTabAsync` with running-process confirmation parity
  - shortcut split: `Ctrl+W` close tab, `Ctrl+Shift+W` close pane
- Implement MRU switching:
  - `Ctrl+Tab` / `Ctrl+Shift+Tab` use MRU, not index cycle
- Add overflow strategy for 10+ tabs:
  - overflow menu + palette switch command
- Harden lifecycle/perf:
  - stop background render/cursor timers for detached/closed terminal views
  - reduce full-tab repaint passes on every selection event

### Files

- `NovaTerminal/MainWindow.axaml.cs`
- `NovaTerminal/MainWindow.axaml`
- `NovaTerminal/Core/SessionModels.cs`
- `NovaTerminal/Core/SessionManager.cs`
- `NovaTerminal/Core/TerminalView.cs`

### Acceptance Criteria

- MRU switching verified by tests and manual stress.
- Close-tab policy always honored for running processes.
- Overflow navigation works with 20 tabs.
- Timer and render loop cleanup validated in close/reopen stress test.
- Budgets above pass in CI stress lane.

### Risk

High

### Mitigations

- keep refactor minimal and local to tab path
- add instrumentation first, optimize second
- ship behind internal flag until stress lane is green

## M2 - Free UX Completion (P1)

### Goal

Deliver best-in-class usability without violating M1 perf budgets.

### Tasks

- Tab title and identity polish:
  - title precedence: user rename > OSC > CWD/profile fallback
  - deterministic truncation with uniqueness hint
- Activity indicators:
  - background output dot
  - bell/attention badge with debounce
  - exit status signal on finished tabs
- Discoverability + accessibility:
  - tab context menu (close, close others, rename, copy title)
  - command palette entries for all tab ops
  - automation labels for tab state (active/attention)
- Optional safety:
  - protected/pinned tabs to prevent accidental close

### Files

- `NovaTerminal/MainWindow.axaml.cs`
- `NovaTerminal/MainWindow.axaml`
- `NovaTerminal/App.axaml`
- `NovaTerminal/Controls/TerminalPane.axaml.cs`

### Acceptance Criteria

- Activity states visible and non-noisy.
- Title behavior stable across restart.
- Full keyboard and context-menu tab workflow is available.
- Accessibility labels present and testable.

### Risk

Medium

### Mitigations

- throttle attention updates
- enforce contrast thresholds in built-in themes

## M3 - Paid Foundation (Only After Free Is Flawless) (P1/P2)

### Goal

Add paid workflow leverage with zero regression to Free tab quality.

### Tasks

- Pro:
  - named workspace persistence (tabs + panes + cwd + profiles + metadata)
  - advanced searchable tab switcher
  - tab rules/templates
- Team:
  - shareable workspace templates
  - import/export session bundles
  - policy-controlled defaults
- Enterprise:
  - audit trail for workspace/session export/import
  - managed policy hooks
  - tamper-evident bundle primitives

### Files

- `NovaTerminal/Core/SessionModels.cs`
- `NovaTerminal/Core/SessionManager.cs`
- `NovaTerminal/MainWindow.axaml.cs`
- new workspace/policy services under `NovaTerminal/Core/`

### Acceptance Criteria

- Workspace round-trip deterministic and versioned.
- No regressions on Free tab stress budgets.
- Paid features behind explicit gating flags.

### Risk

Medium-High

### Mitigations

- schema versioning + migration tests
- separate paid feature toggles from Free core paths

## Test Strategy

## Unit / Integration

- tab identity persistence (`TabId`) and restore mapping
- MRU ordering correctness
- close behavior policy matrix (idle/running/confirm/graceful/force)
- title precedence and truncation behavior
- pinned/protected close guards

## Stress / Perf

- rapid tab switching under output flood (10+ tabs)
- 20 tabs background output, active typing in one tab
- tab create/close loop (5,000 iterations)
- restore large session (20 tabs, mixed pane trees)
- repeated app close/open restore cycles

## Torture Scenarios (Required)

1. 20 tabs tailing logs + continuous MRU switching for 5 minutes.  
Expected: no stutter spikes over budget.
2. Bell storms in background tabs while editing in active tab.  
Expected: no input lag, controlled attention indicators.
3. Close running tabs from all entry points (shortcut/menu/context).  
Expected: policy-consistent confirmation behavior.
4. Restore session with long/duplicate-like titles.  
Expected: stable identity, unambiguous display.
5. 5,000 create/close loops with memory sampling.  
Expected: stable memory envelope, no timer leaks.

## CI Gates

- PR gate:
  - tab unit/integration suite
  - baseline perf smoke (switch/create latency)
  - replay/VT correctness
- Nightly gate:
  - full tab stress suite with perf artifact reports
  - memory/leak lane

## Instrumentation Plan (Perf Blocker)

Add counters/timers:
- `MainWindow`:
  - tab switch duration
  - tab visual update duration
  - automation-label update duration
- `TerminalView`:
  - active timer count
  - invalidation frequency for hidden/offscreen tabs
- `SessionManager`:
  - save/restore duration and payload size

Use `RendererStatistics` style reporting and artifact logs for nightly comparison.

## Branching + PR Sequence

1. `feature/tabs-m1-correctness-stress`
2. `feature/tabs-m2-ux-completion`
3. `feature/tabs-m3-paid-foundation`

Rules:
- one milestone per feature branch
- run full gates before merge
- never merge if perf budget fails

## Success Criteria

Free is ready to ship when:
- all M1 and M2 acceptance criteria are green
- stress budgets pass for 7 consecutive nightly runs
- no open P0/P1 tab correctness bugs

Paid work can start only after Free freeze passes these gates.
