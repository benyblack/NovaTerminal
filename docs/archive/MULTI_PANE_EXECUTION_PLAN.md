# NovaTerminal - Multi-Pane Execution Plan

Date: 2026-02-14  
Status: Executed (M1-M3 complete)

## Execution Summary

- M1 branch: `feature/m1-multipane-foundation` -> merged via `7d52a69`
- M2 branch: `feature/m2-multipane-ux-polish` -> merged via `0c9f60a`
- M3 branch: `feature/m3-multipane-advanced-workflows` -> merged via `20e45fa`

## Objective

Deliver best-in-class multi-pane UX and reliability (WezTerm/Ghostty-level polish) without destabilizing VT correctness or renderer performance.

## Scope Decisions (Locked)

1. Correctness and performance regressions are release blockers.
2. Minimal-intrusion refactors first, structural refactor only where required for M3 features.
3. Open Core packaging:
   - Free: robust baseline multi-pane and keyboard navigation.
   - Pro: advanced pane workflows.
   - Team/Enterprise: replay/audit/collaboration hooks.

## Baseline Architecture (Current)

- Layout tree and splitters: `NovaTerminal/MainWindow.axaml.cs`
- Session persistence: `NovaTerminal/Core/SessionManager.cs`
- Per-pane container/session: `NovaTerminal/Controls/TerminalPane.axaml.cs`
- Input/render/resize path: `NovaTerminal/Core/TerminalView.cs`
- Canonical screen state: `NovaTerminal/Core/TerminalBuffer*.cs`
- PTY bridge: `NovaTerminal/Core/RustPtySession.cs`
- Native resize path: `NovaTerminal/native/src/lib.rs`

## Release Gates (Global)

1. All existing tests pass.
2. Multi-pane state machine tests pass (split/close/focus/restore).
3. No output loss under flood test.
4. Resize latency and frame-time budgets pass.
5. Cross-platform replay parity remains green.

## Performance Budgets

- Resize interaction under TUI load:
  - P95 resize-to-render < 40ms
  - No visual white-pane/flicker events
- 4-pane log flood:
  - No dropped PTY chunks
  - Active pane input latency P95 < 30ms
- Idle with 4 panes:
  - Low steady CPU, no runaway timers or invalidation loops

## Milestone Plan

## M1 - Correctness and Safety (P0)

### Goal

Eliminate pane lifecycle, focus-routing, and throughput correctness risks.

### Tasks

- Fix directional pane navigation to skip `GridSplitter` nodes and guarantee focus lands on a `TerminalPane`.
- Align split semantics with UX spec and enforce minimum pane constraints (20 cols, 5 rows effective minimum).
- Add close-running-process policy (`confirm`, `graceful`, `force`) and surface exit status/restart affordance.
- Remove silent output loss path by instrumenting and correcting PTY output backpressure behavior.
- Add pane-level telemetry for frame time, resize latency, queue depth, and dropped chunks.

### Modules / Files

- `NovaTerminal/MainWindow.axaml.cs`
- `NovaTerminal/MainWindow.axaml`
- `NovaTerminal/Controls/TerminalPane.axaml.cs`
- `NovaTerminal/Core/ITerminalSession.cs`
- `NovaTerminal/Core/RustPtySession.cs`
- `NovaTerminal/Core/TerminalView.cs`
- `NovaTerminal/Core/RendererStatistics.cs`

### Acceptance Criteria

- Alt+arrow navigation always lands on a terminal pane.
- Min pane size prevents unusable panes during splitter drag.
- Closing active pane with running process follows configured policy.
- Exit code is visible for exited panes and restart action is available.
- Flood test confirms zero dropped chunks at target throughput.
- New perf counters are emitted and asserted in tests.

### Risk

High (PTY lifecycle + throughput changes)

### Depends On

None

## M2 - UX Polish and Discoverability (P1)

### Goal

Raise interaction quality and discoverability to best-in-class baseline.

### Tasks

- Add pane equalize command and divider double-click equalization.
- Implement adaptive resize throttle tuned for interactive TUIs.
- Expand pane command surface in command palette and pane context menu.
- Harden focus visuals with theme-aware contrast thresholds.
- Add accessibility metadata for pane labels and focus transitions.

### Modules / Files

- `NovaTerminal/MainWindow.axaml.cs`
- `NovaTerminal/MainWindow.axaml`
- `NovaTerminal/Controls/TerminalPane.axaml`
- `NovaTerminal/Controls/TerminalPane.axaml.cs`
- `NovaTerminal/Core/TerminalView.cs`

### Acceptance Criteria

- Equalize works for current split subtree and preserves layout invariants.
- Drag-resize is visually smooth and final geometry is exact.
- All pane actions are keyboard-accessible and discoverable.
- Focus contrast passes defined threshold in all built-in themes.
- Automation labels exist for panes and active-pane transitions are observable.

### Risk

Medium

### Depends On

M1 instrumentation and routing fixes

## M3 - Advanced Workflows and Paid-Tier Foundation (P1/P2)

### Goal

Introduce model-driven pane operations that enable premium features safely.

### Tasks

- Introduce explicit pane layout model decoupled from direct visual-tree mutation.
- Implement zoom toggle with exact geometry restore.
- Implement broadcast input with clear armed-state indicator and scoped targets.
- Persist active pane and advanced layout metadata per tab/session.
- Prepare hooks for cross-pane replay timeline and audit event stream.

### Modules / Files

- `NovaTerminal/MainWindow.axaml.cs`
- `NovaTerminal/Core/SessionManager.cs`
- `NovaTerminal/Core/SessionModels.cs`
- `NovaTerminal/Core/TerminalView.cs`
- New layout model files under `NovaTerminal/Core/` (to be introduced)

### Acceptance Criteria

- Swap/rotate/zoom/broadcast operate through model transforms, not ad-hoc UI mutations.
- Zoom mode round-trips without geometry drift.
- Broadcast mode is safe, explicit, and test-covered.
- Session restore includes active pane identity and advanced layout metadata.

### Risk

High (architectural change)

### Depends On

M1 and M2 stability gates

## Test Strategy and Gates

## State Machine Tests

- Add unit tests for split, close, equalize, focus traversal, and restore transitions.
- Validate stale pane mappings are cleaned and active pane is always resolvable.

## Stress and Performance Tests

- Add resize storm tests with pane count variants (2, 4, 6 panes).
- Add multi-pane flood tests with concurrent input in active pane.
- Assert queue depth and dropped-chunk counters.

## VT Correctness Guard

- Keep replay and reflow suites as hard gate.
- Any pane feature change must run replay and regression categories.

## Torture Scenarios (Required)

1. Rapid splitter drag with `vim` in 2 panes for 30s.
2. 4 panes tailing high-rate logs while typing in one pane.
3. Repeated alt-screen app open/exit combined with tab switching.
4. Close pane with running process and verify policy behavior.
5. Restore nested 6-pane layout with saved ratios and active pane.
6. Concurrent search and selection across panes under live output.

## CI Execution

- PR gate:
  - unit + integration + replay + targeted performance checks
- Nightly gate:
  - stress suite and torture scenarios with artifacted perf reports
- Failure policy:
  - perf-budget failures and replay parity failures block release

## Ownership Map

- UI shell and pane routing:
  - `MainWindow` and pane composition owners
- Renderer and invalidation:
  - `TerminalView` and draw operation owners
- Buffer correctness:
  - `TerminalBuffer` owners
- PTY bridge:
  - `RustPtySession` + native bridge owners
- Test authority:
  - `NovaTerminal.Tests` owners

## Open Core Feature Packaging Plan

## Free

- Split/close/focus navigation
- Reliable resize and focus handling
- Per-pane scrollback/search/selection isolation

## Pro

- Layout presets and advanced persistence
- Broadcast mode
- Pane groups and advanced pane search workflows

## Team/Enterprise

- Multi-pane synchronized session replay
- Collaboration/event hooks
- Audit logging for pane lifecycle and command actions

## Intentional Deferrals

1. Drag-to-reorder panes (defer until model-driven layout is stable).
2. Cosmetic pane chrome experiments (defer until resize/focus correctness is locked).
3. Live collaborative editing features (defer until replay/audit primitives are production-grade).

## Execution Sequence

1. Implement M1 on feature branches with focused PRs.
2. Run full test and perf gates after each PR.
3. Merge to `main` only when gates pass.
4. Execute M2 after M1 reliability is established.
5. Start M3 only after M2 UX and performance gates are consistently green.
