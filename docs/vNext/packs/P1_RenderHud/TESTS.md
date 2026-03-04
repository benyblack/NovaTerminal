# Pack P1 — Tests (Render HUD)

## Required Tests
1. **Toggle test**
   - HUD disabled by default (unless already enabled in app settings)
   - Toggle ON/OFF switches visibility state deterministically

2. **Sampling cadence test**
   - Given a fake/controlled metrics source, HUD refreshes ~every 100ms
   - Does not spam UI thread

3. **No-render-loop allocation (best-effort)**
   - If you have an existing perf harness, add an assertion that allocations/frame do not increase when HUD is ON.
   - If not feasible in CI, add a local-only test or diagnostic counter.

## Optional Tests
- Snapshot screenshot of HUD overlay in a deterministic replay (golden baseline) if infra exists.

## Test Placement
- Prefer `tests/NovaTerminal.Tests/Rendering/**` (or the closest existing suite)
- Use deterministic fakes and avoid timing flakes:
  - drive sampling via injectable clock or controlled scheduler if possible
