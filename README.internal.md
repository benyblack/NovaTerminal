# Ntilde – Internal Engineering Guide

This document defines **engineering intent**, **non-negotiable rules**, and
**how work is evaluated** in Ntilde.

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

The solution keeps terminal semantics isolated behind small project boundaries so the VT core remains pure and reusable:

- **Ntilde.App** owns Avalonia UI, interaction, and app composition.
- **Ntilde.Platform** owns platform-integration utilities (input routing, path mapping, process abstraction, SSH) and the credential vault.
- **Ntilde.VT** owns terminal state, parsing, buffer semantics, and reflow.
- **Ntilde.Rendering** owns Skia-based drawing from immutable buffer snapshots.
- **Ntilde.Pty** owns OS/process and stream integration.
- **Ntilde.Replay** owns recording and replay infrastructure.
- **Ntilde.CommandAssist** owns the Command Assist domain, ranking, storage, and shell integration.
- **Ntilde.Backup** owns `.ntildebackup` export/import and automatic snapshots.
- **Ntilde.VtContract** owns the machine-readable VT capability catalogue.
- **Ntilde.AgentHost.Contracts** owns the app-to-agent wire protocol.
- **Ntilde.McpServer** owns the opt-in MCP surface agents connect to.
- **Ntilde.Cli** and **Ntilde.Conformance** own the headless entry point and the conformance tool.

Key constraints:

- **Ntilde.VT** contains **no** Avalonia or SkiaSharp references.
- **Ntilde.Rendering** contains **no** Avalonia references and does not fix semantic bugs.
- **Ntilde.Pty** is strictly for stream/process management and binary interop.
- **CommandAssist, Backup, VtContract and AgentHost.Contracts have zero project
  references** and must keep it that way. That empty list is what lets the MCP
  server share code with the app without acquiring a path into App, VT, Pty or
  Rendering. The architecture tests assert it per assembly.
- UI concerns stay out of the terminal core logic.

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

- `docs/ROADMAP.md` – test-gated product roadmap
- `docs/MODULE_OWNERSHIP.md` – invariant ownership
- `docs/archive/IMPLEMENTATION_WORK_PLAN.md` – the original correctness-first execution plan (historical)
- `CONTRIBUTING.md` – build/test commands, CI lanes, test categories
- `CLAUDE.md` – why builds must go through `scripts/build.{ps1,sh}`

---

## Final Reminder

> UI attracts users.  
> Correctness keeps them.
