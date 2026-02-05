# NovaTerminal – Module Ownership & Invariant Map (Test-Enforced)

Each module owns specific invariants.
Automated tests are the enforcement mechanism.

Breaking an invariant is a bug, even if the UI appears correct.

---

## Terminal Core (Strictly Cross-Platform)

### Core/AnsiParser.cs — VT / ANSI Parsing
**Owns**
- VT state machine and parsing
**Invariants**
- Deterministic parsing
- No OS, PTY, or rendering logic

**Test Authority**
- Replay tests MUST validate parser output
- Parser changes without replay coverage are rejected

---

### Core/TerminalBuffer.cs — Screen & Scrollback State
**Owns**
- Canonical terminal state
- Main/alt buffers
- Scrollback and reflow

**Invariants**
- Source of truth
- Lossless reflow
- Alt screen isolation

**Test Authority**
- Buffer snapshot tests are authoritative
- Renderer may not “fix” buffer mistakes

---

### Core/TerminalRow.cs — Row Semantics
### Core/TerminalCell.cs — Cell Semantics
*(ownership unchanged, but equality and wrap rules are test-gated)*

---

## Renderer (Semantics-Free)

### Core/TerminalView.cs — Viewport & Invalidation
### Core/TerminalDrawOperation.cs — Drawing

**Invariants**
- Renderer is a pure function of buffer snapshot
- No semantic decisions
- Incremental rendering only

**Test Authority**
- Renderer metrics tests (full redraw count, dirty cells)
- Flicker regressions block merges

---

## PTY / Process Bridge

### Core/RustPtySession.cs
**Invariants**
- No VT parsing
- Bounded IO
- Deterministic byte delivery

**Test Authority**
- Replay capture tests validate byte integrity

---

## Tests (First-Class Owners)

### NovaTerminal.Tests/*
**Owns**
- Regression protection
- Parity enforcement
- Invariant validation

**Authority**
- Tests may block any change
- “Looks fine” is not a valid argument

---

## Guiding Rule

> If tests disagree with code, tests are correct.
