# NovaTerminal – Internal Engineering Guide

This document defines **engineering intent**, **non-negotiable rules**, and
**how work is evaluated** in NovaTerminal.

It is authoritative for contributors and automated agents.

---

## Core Principle

> Terminal correctness is enforced by automated tests, not discipline.

Any change that weakens determinism, parity, or replayability is rejected.

---

## Non-Negotiable Rules

1. **Terminal Core is OS-agnostic**
   - No platform conditionals
   - No UI or rendering logic
   - No PTY knowledge

2. **Renderer is semantics-free**
   - Renderer may not “fix” buffer mistakes
   - All drawing is derived from buffer snapshots

3. **Tests have veto power**
   - Replay tests block merges
   - Parity tests block merges
   - Renderer metric regressions block merges

4. **If it cannot be replayed, it cannot be safely fixed**

---

## Architecture Boundaries

Terminal Core → Renderer → UI
↑
Replay


- VT parsing and buffer state live **only** in the core
- Rendering is incremental and cell-based
- UI orchestrates; it does not interpret terminal semantics

---

## Automated Test Gates

### Phase −1 (Blocking)
- Deterministic replay harness
- Cross-platform parity checks
- Renderer metrics (full redraw, dirty cells, frame time)

### Phase 0 (Correctness)
- VT completeness
- Alternate screen correctness
- Resize & reflow stability
- Zero flicker under stress

Feature work is blocked until these gates pass.

---

## What We Do NOT Optimize For

- Fast feature shipping at the expense of correctness
- Pixel-perfect UI tests over buffer-state tests
- Platform-specific hacks in core logic
- “Looks fine on my machine” fixes

---

## How Changes Are Evaluated

A change is acceptable only if:
- buffer invariants remain intact
- replay tests cover new behavior
- cross-platform parity is preserved
- renderer metrics do not regress

---

## Useful Docs

- `ROADMAP.md` – test-gated product roadmap
- `MODULE_OWNERSHIP.md` – invariant ownership
- `IMPLEMENTATION_WORK_PLAN.md` – correctness-first execution plan

---

## Final Reminder

> UI attracts users.  
> Correctness keeps them.
