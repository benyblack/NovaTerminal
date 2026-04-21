# Execution Pack – Ghostty Gap Closure

Each PR is independent and can be executed sequentially.

---

## PR1 — Parser Hardening

### Goal
Ensure parser robustness under malformed and uncommon sequences.

### Tasks
- Implement C1 7-bit translation layer
- Define ST termination handling:
  - ESC \\
  - BEL
- Define unknown sequence handling policy
- Add malformed sequence recovery logic

### Tests
- Fuzz corpus (invalid/truncated sequences)
- Replay tests with broken input streams

### Done Criteria
- No crashes on malformed input
- Parser behavior deterministic and documented

---

## PR2 — Cursor Positioning Completion

### Goal
Fix all cursor positioning inconsistencies.

### Tasks
- Audit CUP/HVP default parameter behavior
- Implement HPA/VPA/HPR/VPR correctly
- Handle origin mode interactions

### Tests
- Unit tests for:
  - missing params
  - zero params
  - out-of-range values

### Done Criteria
- Cursor positioning consistent across all cases

---

## PR3 — Scroll & Wrap Correctness

### Goal
Make full-screen TUIs stable.

### Tasks
- Implement DECSTBM fully
- Fix IND / RI behavior
- Fix wraparound (DECAWM)
  - wide glyph at edge
  - combining marks at boundary

### Tests
- Replay tests:
  - vim-like redraw
  - scrolling regions
  - reverse index cases

### Done Criteria
- No visual corruption in TUIs

---

## PR4 — Alternate Screen

### Goal
Make alt-screen transitions correct and deterministic.

### Tasks
- Implement ?47 / ?1047 / ?1049
- Save/restore cursor and attributes
- Define scrollback behavior

### Tests
- Replay:
  - shell → app → shell
  - nested alt-screen transitions

### Done Criteria
- Stable alt-screen switching

---

## PR5 — Input Modes

### Goal
Fix interaction inconsistencies.

### Tasks
- Implement bracketed paste
- Implement mouse protocols:
  - 1000 / 1002 / 1003 / 1006
- Implement application cursor keys
- Implement focus reporting

### Tests
- Integration tests where possible
- App-path verification (vim/tmux)

### Done Criteria
- No broken input behavior

---

## PR6 — Tab System

### Goal
Complete tab handling.

### Tasks
- Implement HT / CHT / CBT
- Tab stop storage
- ESC H (set tab stop)
- CSI g (clear tab stops)

### Tests
- Replay:
  - aligned text output
  - tab-heavy CLI output

### Done Criteria
- Tabs behave like xterm

---

## PR7 — Unicode Width Model v2

### Goal
Improve grapheme correctness.

### Tasks
- Separate:
  - storage unit
  - render cluster
  - cursor unit
- Implement grapheme-aware cursor movement
- Improve width calculation

### Tests
- Emoji sequences
- ZWJ families
- Combining marks
- Regional indicators

### Done Criteria
- No cursor drift
- Correct rendering of complex text

---

## PR8 — Conformance Tooling

### Goal
Expose correctness as a product feature.

### Tasks
- Generate VT support report from matrix
- Validate:
  - every ✅ has evidence
- Add CI enforcement

### Optional
- CLI:
  novaterminal --vt-report

### Done Criteria
- Machine-readable compatibility report
- CI enforcement active

---

## Execution Order

PR1 → PR3 → PR4 → PR5 → PR2 → PR6 → PR7 → PR8

---

## Notes

- Do NOT mix rendering optimization with correctness fixes
- Every change must add or update:
  - matrix row
  - test evidence
- Replay tests are the primary correctness signal