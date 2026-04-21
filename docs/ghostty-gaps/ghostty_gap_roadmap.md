# NovaTerminal vs Ghostty – Gap Roadmap

_Last reviewed: 2026-04-21._

This document defines the prioritized execution plan to close critical VT gaps between NovaTerminal and Ghostty, based on the VT Conformance Matrix.

---

## 🎯 Strategic Goal

Move NovaTerminal from:
- Partial VT correctness + strong architecture

To:
- High VT correctness + provable determinism (differentiator)

---

## 🟢 Phase 1 — Core Correctness (P0) — **Shipped**

### Objectives
- Make TUIs (vim, less, htop, tmux) behave correctly
- Eliminate cursor, scroll, and parser inconsistencies

### Scope

#### 1. Parser Hardening — shipped via PR1/PR3
- C1 controls (7-bit + optional 8-bit handling)
- ST termination (ESC \ vs BEL)
- Unknown sequence policy (ignore/print/recover)
- Malformed sequence recovery

PR1 status notes:
- 7-bit C1 handling is partial: CSI/OSC/DCS/APC and IND/NEL/RI recover correctly, unsupported `ESC @.._` controls are ignored.
- BEL termination is treated as permissive recovery for DCS/APC, not strict spec compliance.
- Unknown `ESC @.._` handling is ignore-with-recovery to keep parser state deterministic on broken streams.

#### 2. Cursor & Positioning — shipped via PR2
- CUP/HVP default parameter correctness
- HPA/VPA/HPR/VPR correctness
- Origin mode interactions

#### 3. Scrolling & Margins — shipped via PR3
- DECSTBM correctness
- IND / RI behavior
- Wraparound (DECAWM), including wide glyph edges

#### 4. Alternate Screen — shipped via PR4
- ?47 / ?1047 / ?1049 correctness
- Cursor + attribute save/restore
- Scrollback policy

### Exit Criteria — met
- No major rendering corruption in TUIs
- Replay tests stable for full-screen apps
- Matrix rows upgraded toward ✅

---

## 🟢 Phase 2 — Input & Interaction (P1) — **Shipped**

### Objectives
- Make terminal interaction predictable and correct

### Scope — shipped via PR5 (input modes) and PR6 (tab system)

- Bracketed paste (?2004)
- Mouse protocols (1000/1002/1003/1006)
- Application cursor keys (?1)
- Focus reporting (?1004)
- Tab system (HT, CHT, CBT, tab stops)

### Exit Criteria — met
- vim/tmux/mc input behavior stable
- No broken paste or mouse interactions
- Tab system fully functional

---

## 🟢 Phase 3 — Unicode Excellence (P2) — **Shipped**

### Objectives
- Achieve top-tier Unicode correctness

### Scope — shipped via PR7 (Unicode width model v2)

- Grapheme clustering model
- Width correctness (emoji, CJK, ZWJ)
- Combining marks
- Cursor movement over graphemes

### Exit Criteria — met
- No cursor drift on complex text
- Correct rendering of emoji sequences
- Replay tests for Unicode scenarios stable

---

## 🟡 Phase 4 — Differentiation (P3) — **In progress**

### Objectives
- Turn engineering rigor into product advantage

### Scope

- [x] VT conformance report generator (`src/NovaTerminal.Conformance`, shipped with VT report CLI)
- [x] CI enforcement of matrix rules (`.github/workflows/vt-conformance.yml`)
- [x] Machine-readable VT support report (`--vt-report --json`)
- [ ] Replay-based validation tooling extensions beyond current coverage
- [ ] Public compatibility reporting (external-facing page)

### Exit Criteria
- Machine-readable VT support report ✅
- CI fails on unsupported “✅” rows ✅
- Public-facing compatibility claims — pending

---

## ⚖️ Strategic Decisions

### SIXEL
Choose one:
- ❌ De-prioritize (align with modern protocols)
- ✅ Invest (differentiate for legacy/scientific use)

---

## 🏁 Final Outcome

NovaTerminal becomes:

- Not just “a terminal”
- But:
  - A **deterministic VT engine**
  - With **provable correctness**
  - And **auditable compatibility**
