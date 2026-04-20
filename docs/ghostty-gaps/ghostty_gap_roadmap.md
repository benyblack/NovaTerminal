# NovaTerminal vs Ghostty – Gap Roadmap

This document defines the prioritized execution plan to close critical VT gaps between NovaTerminal and Ghostty, based on the VT Conformance Matrix.

---

## 🎯 Strategic Goal

Move NovaTerminal from:
- Partial VT correctness + strong architecture

To:
- High VT correctness + provable determinism (differentiator)

---

## 🔴 Phase 1 — Core Correctness (P0)

### Objectives
- Make TUIs (vim, less, htop, tmux) behave correctly
- Eliminate cursor, scroll, and parser inconsistencies

### Scope

#### 1. Parser Hardening
- C1 controls (7-bit + optional 8-bit handling)
- ST termination (ESC \ vs BEL)
- Unknown sequence policy (ignore/print/recover)
- Malformed sequence recovery

PR1 status notes:
- 7-bit C1 handling is partial: CSI/OSC/DCS/APC and IND/NEL/RI recover correctly, unsupported `ESC @.._` controls are ignored.
- BEL termination is treated as permissive recovery for DCS/APC, not strict spec compliance.
- Unknown `ESC @.._` handling is ignore-with-recovery to keep parser state deterministic on broken streams.

#### 2. Cursor & Positioning
- CUP/HVP default parameter correctness
- HPA/VPA/HPR/VPR correctness
- Origin mode interactions

#### 3. Scrolling & Margins
- DECSTBM correctness
- IND / RI behavior
- Wraparound (DECAWM), including wide glyph edges

#### 4. Alternate Screen
- ?47 / ?1047 / ?1049 correctness
- Cursor + attribute save/restore
- Scrollback policy

### Exit Criteria
- No major rendering corruption in TUIs
- Replay tests stable for full-screen apps
- Matrix rows upgraded toward ✅

---

## 🟡 Phase 2 — Input & Interaction (P1)

### Objectives
- Make terminal interaction predictable and correct

### Scope

- Bracketed paste (?2004)
- Mouse protocols (1000/1002/1003/1006)
- Application cursor keys (?1)
- Focus reporting (?1004)
- Tab system (HT, CHT, CBT, tab stops)

### Exit Criteria
- vim/tmux/mc input behavior stable
- No broken paste or mouse interactions
- Tab system fully functional

---

## 🟢 Phase 3 — Unicode Excellence (P2)

### Objectives
- Achieve top-tier Unicode correctness

### Scope

- Grapheme clustering model
- Width correctness (emoji, CJK, ZWJ)
- Combining marks
- Cursor movement over graphemes

### Exit Criteria
- No cursor drift on complex text
- Correct rendering of emoji sequences
- Replay tests for Unicode scenarios stable

---

## 🔵 Phase 4 — Differentiation (P3)

### Objectives
- Turn engineering rigor into product advantage

### Scope

- VT conformance report generator
- Replay-based validation tooling
- CI enforcement of matrix rules
- Public compatibility reporting

### Exit Criteria
- Machine-readable VT support report
- CI fails on unsupported “✅” rows
- Public-facing compatibility claims

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
