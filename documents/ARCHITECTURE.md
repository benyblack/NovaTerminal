# NovaTerminal – Architecture & Design Rationale

This document describes the **internal architecture**, **invariants**, and **design trade-offs**
of NovaTerminal.

It is authoritative for contributors and automated agents.

---

## 1. Architectural Goals

NovaTerminal is designed to satisfy four non-negotiable goals:

1. **Deterministic terminal semantics**
2. **Cross-platform behavioral parity**
3. **Incremental, flicker-free rendering**
4. **Test-enforced correctness**

All architectural decisions follow from these goals.

---

## 2. High-Level Architecture

NovaTerminal is structured as a **strictly layered system**:

┌──────────────────────────────┐
│ UI Shell (Avalonia) │ OS-adaptive
├──────────────────────────────┤
│ Renderer (Skia) │ GPU-accelerated
│ - Cell-grid based │
│ - Incremental redraw │
├──────────────────────────────┤
│ Terminal Core (Cross-Platform)│
│ - VT / ANSI parser │
│ - Screen buffers │
│ - Scrollback │
│ - Reflow & wrapping │
│ - Search │
├──────────────────────────────┤
│ PTY Backend (Rust) │ OS-specific
└──────────────────────────────┘


### Hard Rule
> **No OS-specific logic is allowed in the Terminal Core.**

---

## 3. Terminal Core

The Terminal Core is the **single source of truth** for terminal semantics.

### 3.1 Responsibilities
- Parse VT / ANSI escape sequences
- Maintain deterministic screen state
- Handle alternate screen transitions
- Manage scrollback
- Perform lossless reflow on resize
- Provide read-only snapshots for rendering

### 3.2 Components

#### `AnsiParser`
- Implements a state machine for:
  - ESC
  - CSI
  - OSC
  - DEC private modes
- Emits **semantic operations**, not rendering actions

**Key invariant**
Same byte stream → same sequence of semantic operations


---

#### `TerminalBuffer`
- Applies semantic operations to terminal state
- Maintains:
  - cursor position
  - modes
  - tab stops
  - margins
- Owns:
  - main screen buffer
  - alternate screen buffer
  - scrollback

**Key invariants**
- Alternate screen is fully isolated
- Scrollback is immutable
- Buffer state is renderer-agnostic

---

#### `TerminalRow`
- Stores a row of cells
- Tracks:
  - hard line breaks
  - soft wraps (continuations)

Reflow relies entirely on row metadata — **not text measurement**.

---

#### `TerminalCell`
- Represents a single terminal cell
- Contains:
  - codepoint(s) or grapheme cluster
  - foreground/background color
  - style flags

**Important**
- Equality is defined only on render-affecting state
- No renderer-specific data allowed

---

### 3.3 Determinism

The Terminal Core must be **purely deterministic**:

- No timers
- No threading assumptions
- No OS calls
- No UI interactions

This allows:
- deterministic replay
- cross-platform parity testing
- safe refactoring

---

## 4. Renderer

The renderer is responsible for **turning a buffer snapshot into pixels**.

It is intentionally **semantics-free**.

### 4.1 Rendering Model

- Rendering is cell-grid based
- Text layout is driven by:
  - fixed cell width
  - fixed cell height
  - baseline offset
- Glyph measurement occurs once per font/size/DPI change

---

### 4.2 Incremental Rendering (Cell-Diff)

NovaTerminal uses **incremental rendering** to avoid flicker:

1. Renderer receives a **read-only snapshot**
2. Snapshot is diffed against previous frame
3. Dirty cell ranges are identified
4. Only dirty ranges are redrawn
5. Result is composited via a backing surface

**Invariant**
Renderer output is a pure function of (snapshot, metrics, theme)

---

### 4.3 What the Renderer Must NOT Do

- Fix buffer mistakes
- Guess wrapping
- Reinterpret VT semantics
- Perform layout heuristics

If rendering “looks wrong”, the bug is in the **Terminal Core**, not the renderer.

---

## 5. UI Shell (Avalonia)

The UI shell orchestrates **presentation and interaction**, not semantics.

### Responsibilities
- Window and pane management
- Input routing
- Selection handling
- Settings UI
- Command palette
- Pane resizing (pixel → row/col calculation)

### Non-Responsibilities
- VT parsing
- Buffer mutation (except via explicit APIs)
- Rendering logic

---

## 6. PTY Backend

NovaTerminal uses a **Rust-based PTY backend** to unify platform differences.

### Responsibilities
- Process creation
- PTY allocation
- Read/write loops
- Resize propagation

### Invariants
- PTY layer never interprets VT
- Bytes are delivered verbatim to the core
- IO is bounded and non-blocking

---

## 7. Deterministic Replay

Replay is a **core architectural feature**, not a debug tool.

### 7.1 Purpose
- Reproduce bugs exactly
- Enforce cross-platform parity
- Enable safe refactoring

### 7.2 Replay Pipeline

PTY Byte Stream
↓
[Recorder]
↓
Replay File
↓
AnsiParser
↓
TerminalBuffer
↓
Buffer Snapshot


### 7.3 Snapshot Semantics
Snapshots capture:
- visible screen content
- attributes
- cursor state
- alt/main screen flag
- minimal scrollback

Snapshots are **compared in CI**.

---

## 8. Cross-Platform Parity

NovaTerminal enforces **behavioral parity**:

| Aspect | Must Match Across OSes |
|------|------------------------|
| VT parsing | Yes |
| Buffer state | Yes |
| Wrapping & reflow | Yes |
| Search semantics | Yes |
| Rendering semantics | Yes |

Allowed differences:
- window chrome
- hotkeys
- blur/transparency
- credential storage backend

---

## 9. Automated Testing as Architecture

Automated testing is a **first-class architectural component**.

### Test Layers

1. **Unit tests**
   - Buffer and parser invariants
2. **Replay tests**
   - Real-world byte streams
3. **Parity tests**
   - Cross-platform equivalence
4. **Renderer metrics**
   - Full redraw count
   - Dirty cell count
   - Frame timing

### Rule
> Tests have veto power over all code changes.

---

## 10. Failure Modes We Explicitly Design Against

- “Looks fine on my machine”
- Resize-induced corruption
- Alternate screen leakage
- Renderer-driven semantic drift
- Platform-specific behavior divergence

---

## 11. Why This Architecture

This architecture is intentionally **boring**.

That is a feature.

- Ghostty and WezTerm succeed because they are predictable
- Users forgive missing features
- Users do not forgive broken terminals

NovaTerminal optimizes for **trust first**, features second.

---

## 12. Summary

NovaTerminal is:
- deterministic by design
- cross-platform by construction
- test-gated by policy
- incremental by default

> UI attracts users.  
> Correctness keeps them.

This document should be read before making architectural changes.
