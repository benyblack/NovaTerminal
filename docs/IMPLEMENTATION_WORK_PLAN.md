# NovaTerminal – Implementation Work Plan (Test-Gated)

This plan enforces correctness-first development.
Progression is blocked until gates are satisfied.

---

## Phase −1 — Automated Correctness Infrastructure (BLOCKING)

### Gate −1A: Deterministic Replay (G1)

**Steps**
1. Add raw byte recorder to RustPtySession
2. Implement core-only replay runner
3. Add buffer snapshot serializer
4. Add replay-based tests

**STOP**
- ❌ Do not refactor Terminal Core before this gate passes

---

### Gate −1B: Cross-Platform Parity

**Steps**
- Run same replay fixtures on all OSes
- Assert identical buffer snapshots

**STOP**
- ❌ Platform divergence blocks progress

---

### Gate −1C: Renderer Metrics

**Steps**
- Add redraw counters and frame metrics
- Add assertions to tests

**STOP**
- ❌ Rendering refactors without metrics are rejected

---

## Phase 0 — Correctness Hardening

### Gate 0A: VT + Alt Screen
### Gate 0B: Reflow
### Gate 0C: Zero Flicker Rendering

**Rule**
- Each gate requires replay tests covering new behavior

---

## Phase 1+ — Feature Work

**Rule**
- No feature work may weaken:
  - replay determinism
  - buffer invariants
  - renderer metrics thresholds

---

## Execution Order (Strict)

1. Phase −1 (all gates)
2. Phase 0
3. Phase 1+
4. CI expansion and UI automation (later)

---

## Non-Negotiable Rule

> If it is not testable, it is not shippable.
