# NovaTerminal – Product & Engineering Roadmap (Cross-Platform, Test-Gated)

This roadmap is authoritative for humans and automated agents (Antigravity / Codex).

Core principle:
> **Terminal correctness is enforced by automated tests, not discipline.**

---

## Phase −1 — Automated Correctness Infrastructure (MANDATORY)

> This phase MUST be completed before any work in Phase 0 or later.
> It is a hard gate.

### −1.1 Deterministic Replay Harness (G1)

**Deliverables**
- Raw PTY byte-stream recording
- Core-only replay runner (`AnsiParser → TerminalBuffer`)
- Deterministic buffer snapshot serializer
- Replay-based unit tests

**Acceptance Criteria**
- Any terminal bug can be recorded once and replayed in CI
- Replay produces identical buffer state across OSes
- Replay tests run in <5 seconds (fast feedback)

**Gate**
- ❌ No Terminal Core refactor allowed without replay coverage
- ❌ No rendering refactor allowed until replay exists

---

### −1.2 Cross-Platform Parity Tests

**Deliverables**
- Same replay fixtures run on Windows, Linux, macOS
- Buffer snapshots must match exactly

**Gate**
- ❌ Platform-specific behavior differences are rejected unless explicitly documented

---

### −1.3 Renderer Metrics & Guardrails

**Deliverables**
- Instrumentation for:
  - full redraw count
  - dirty cell count
  - frame time
- Threshold-based assertions in tests

**Gate**
- ❌ Rendering changes without metrics tests are rejected

---

## Phase 0 — Production-Grade Terminal Correctness

> UI and features are irrelevant until this phase is complete.

### 0.1 VT / ANSI Core Correctness

**Deliverables**
- Complete VT/ANSI state machine
- DEC private modes correctness
- OSC handling (titles + OSC 8 hyperlinks)
- Proper reset semantics

**Acceptance Criteria**
- `vim`, `htop`, `less`, `mc`, `tmux` behave correctly
- No cursor or attribute desync

---

### 0.2 Alternate Screen & Scrollback Integrity

**Deliverables**
- Strict main/alt buffer separation
- Scrollback isolation
- Cursor restoration correctness

**Acceptance Criteria**
- Enter/exit full-screen apps leaves main buffer unchanged

---

### 0.3 Wrapping & Reflow Correctness

**Deliverables**
- Lossless reflow on resize
- Correct handling of wide glyphs
- Stable soft vs hard wrap semantics

**Acceptance Criteria**
- Resize wide → narrow → wide yields identical content

---

### 0.4 Rendering Stability (Zero Flicker Rule)

**Deliverables**
- Incremental (cell-diff) rendering
- No full redraw per frame
- Stable resize under load

**Acceptance Criteria**
- Zero flicker in `vim`, `htop`, fast scroll output

---

## Phase 1 — Switching Friction Killers

### 1.1 Session Restore & Workspaces
### 1.2 Import Compatibility
### 1.3 Transient / Toggleable Terminal Window (Cross-Platform Capability)

*(unchanged in scope; gated by Phases −1 and 0)*

---

## Phase 2 — Remote-First Differentiation
*(unchanged, but test-gated)*

---

## Phase 3 — Modern Terminal Capabilities
*(unchanged, but test-gated)*

---

## Phase 4 — Optional AI (Post v1)
*(unchanged)*

---

## Hard Rule for Agents

> If a change cannot be covered by automated tests, it must not be merged.

