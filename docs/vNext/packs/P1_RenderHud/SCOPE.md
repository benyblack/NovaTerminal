# Pack P1 — Render Performance HUD

## Objective
Implement a **toggleable Render Performance HUD** overlay that displays live renderer metrics without perturbing the render loop.

## Deliverables
- HUD overlay rendered in an **overlay pass**
- Toggle via command palette / shortcut
- Metrics sampling at fixed cadence (default: 100ms)
- Copy metrics as JSON to clipboard (optional but recommended)

## In Scope
- HUD overlay UI (minimal, readable)
- Wiring to existing renderer metrics (`RenderPerfMetrics` / equivalent)
- Minimal command binding (toggle)
- Tests for toggle + sampling

## Out of Scope
- Any changes to recording format (`.novarec`)
- Replay timeline UI
- Command boundary detection
- Remote relay / plugin SDK
- Any major refactor of renderer architecture

## Hard Constraints
- **No per-frame allocations** in the render loop when HUD is enabled
- HUD must not contaminate main render-pass timing (overlay pass only)
- Must work in:
  - live sessions
  - replay playback (read-only; HUD shows metrics from replay rendering)

## Acceptance Criteria (Definition of Done)
- HUD can be enabled/disabled at runtime (no restart)
- Overhead when enabled: **< 3%** avg frame time on a representative workload
- Update cadence stable (±1 tick) at 100ms
- No new allocations on steady-state frames (validated with profiler or allocation counters)
- Tests pass in CI and locally on at least one OS

## Notes
If you discover that metrics are not currently accessible at the overlay layer, add a **narrow adapter interface** rather than plumbing metrics through unrelated layers.
