# NovaTerminal – Technical Gap Checklist → Code Mapping

This document maps **production-grade terminal requirements** to **concrete code areas**
in the current NovaTerminal codebase.

Purpose:
- Guide automated agents (Antigravity / Codex)
- Make correctness work explicit and scoped
- Prevent platform-specific logic leakage into core code

---

## A. Terminal Core Correctness (Non-Negotiable)

### A1. VT / ANSI State Machine Completeness

**Responsibilities**
- Parse escape sequences
- Emit semantic actions (cursor move, style change, screen ops)
- Never depend on OS or PTY details

**Primary Code**
- `NovaTerminal/Core/AnsiParser.cs`
  - VT state machine (ESC / CSI / OSC / DEC modes)
- `NovaTerminal/Core/TerminalBuffer.cs`
  - Applies parsed actions to buffer state
- `NovaTerminal/Core/TerminalCell.cs`
  - Cell attributes (fg/bg, bold, italic, etc.)
- `NovaTerminal/Core/TerminalRow.cs`
  - Row structure, wrapping metadata

**Relevant Tests**
- `NovaTerminal.Tests/PowerShellBehaviorTests.cs`
- `NovaTerminal.Tests/PowerShellCursorPositionTests.cs`
- `NovaTerminal.Tests/PowerShellWrappingTests.cs`
- `NovaTerminal.Tests/CMDDuplicationTests.cs`
- `NovaTerminal.Tests/OhMyPoshTests.cs`

---

### A2. Alternate Screen Correctness

**Responsibilities**
- Maintain main and alternate buffers independently
- Restore main buffer byte-for-byte on exit
- Preserve cursor and attribute state

**Primary Code**
- `NovaTerminal/Core/TerminalBuffer.cs`
  - `_mainScreen`
  - `_altScreen`
  - `_isAltScreen`

**Trigger Source**
- `NovaTerminal/Core/AnsiParser.cs`
  - DEC private mode handling

**Relevant Tests**
- `NovaTerminal.Tests/AlternateScreenTests.cs`

---

### A3. Line Wrapping & Reflow

**Responsibilities**
- Distinguish soft wraps vs hard line breaks
- Reflow text correctly on resize
- Preserve attributes and glyph width

**Primary Code**
- `NovaTerminal/Core/TerminalBuffer.cs`
  - `Resize(...)`
  - `Reflow(...)`
- `NovaTerminal/Core/TerminalRow.cs`
  - Wrap flags and continuation metadata

**Relevant Tests**
- `NovaTerminal.Tests/ReflowScenariosTests.cs`
- `NovaTerminal.Tests/ReflowRegressionTests.cs`
- `NovaTerminal.Tests/PlainPowerShellResizeTests.cs`
- `NovaTerminal.Tests/PowerShellWrappingTests.cs`

---

## B. Resize & Stress Stability

### B1. Resize Under Load

**Responsibilities**
- Resize must not trigger full redraw
- Must not corrupt buffer state
- Must remain flicker-free

**Resize Pipeline**
- `NovaTerminal/Controls/TerminalPane.axaml.cs`
  - Pixel → row/column calculation
- `NovaTerminal/Core/TerminalView.cs`
  - Resize invalidation / render scheduling
- `NovaTerminal/Core/RustPtySession.cs`
  - PTY resize propagation
- `NovaTerminal/Core/ConPtyNative.cs`
  - Windows-specific resize calls

**Rendering**
- `NovaTerminal/Core/TerminalDrawOperation.cs`
  - Drawing logic (cell grid → Skia)

**Relevant Tests**
- `NovaTerminal.Tests/PlainPowerShellResizeTests.cs`
- `NovaTerminal.Tests/ReflowRegressionTests.cs`
- `NovaTerminal.Tests/PowerShellDiagnosticTests.cs`

---

### B2. Scrollback Integrity

**Responsibilities**
- Scrollback must be immutable
- Alternate screen must not pollute scrollback
- Search must not mutate buffer

**Primary Code**
- `NovaTerminal/Core/TerminalBuffer.cs`
  - Scrollback storage and indexing
- `NovaTerminal/Core/SearchMatch.cs`
  - Search result representation

**Relevant Tests**
- `NovaTerminal.Tests/TerminalBufferTests.cs`

---

## C. Rendering Guarantees (Cross-Platform Determinism)

### C1. Deterministic Cell Model

**Responsibilities**
- Rendering driven by cell grid
- No layout logic in renderer
- Same buffer → same output on all OSes

**Primary Code**
- `NovaTerminal/Core/TerminalCell.cs`
- `NovaTerminal/Core/TerminalRow.cs`
- `NovaTerminal/Core/TerminalBuffer.cs`

---

### C2. Incremental Rendering Only (Cell Diff)

**Responsibilities**
- Track previous frame
- Render only changed cells
- Prevent flicker under resize and output

**Primary Code**
- `NovaTerminal/Core/TerminalDrawOperation.cs`
- `NovaTerminal/Core/TerminalView.cs`

**Status**
- Cell-diff rendering not fully implemented yet
- This is a required future enhancement

---

### C3. Font & Glyph Consistency

**Responsibilities**
- Ligatures optional and predictable
- Emoji width correctness
- Font fallback without grid drift

**Primary Code**
- `NovaTerminal/Core/TerminalView.cs`
  - Font selection and metrics
- `NovaTerminal/Core/TerminalDrawOperation.cs`
  - Skia text drawing

---

## D. PTY & IO Layer Correctness

### D1. PTY Abstraction Discipline

**Rules**
- PTY does NOT interpret VT
- VT core does NOT know OS details
- No platform branching in parser or buffer

**Primary Code**
- `NovaTerminal/Core/ITerminalSession.cs`
- `NovaTerminal/Core/RustPtySession.cs`
- `NovaTerminal/Core/ConPtyNative.cs`

---

### D2. Backpressure & Flow Control

**Responsibilities**
- Prevent UI thread starvation
- Handle large outputs smoothly
- Maintain low input latency

**Primary Code**
- `NovaTerminal/Core/RustPtySession.cs`
  - Read loop, buffering, async dispatch
- `NovaTerminal/Core/TerminalView.cs`
  - Invalidation throttling / render coalescing

---

## E. Cross-Platform Parity Rules

### E1. Behavioral Parity (Required)

**Must be identical across OSes**
- VT interpretation
- Reflow behavior
- Cursor semantics

**Primary Code**
- `NovaTerminal/Core/AnsiParser.cs`
- `NovaTerminal/Core/TerminalBuffer.cs`
- `NovaTerminal/Core/TerminalRow.cs`
- `NovaTerminal/Core/TerminalCell.cs`

---

### E2. UI Parity (Allowed to Differ)

**May vary per OS**
- Window chrome
- Transparency / blur
- Global hotkeys

**Primary Code**
- `NovaTerminal/MainWindow.axaml(.cs)`
- `NovaTerminal/Controls/TerminalPane.axaml(.cs)`
- `NovaTerminal/SettingsWindow.axaml(.cs)`

---

## F. SSH & Remote Safety

### F1. Credential Handling

**Responsibilities**
- No plaintext storage
- Explicit user consent
- OS-specific secure storage

**Primary Code**
- `NovaTerminal/Core/TerminalProfile.cs`
- `NovaTerminal/Core/VaultService.cs`
- `NovaTerminal/Core/RustPtySession.cs` ⚠️
  - Contains password prompt injection logic
  - Must be replaced with secure flow

---

### F2. Remote Session Robustness

**Responsibilities**
- Network jitter must not corrupt buffer
- Reconnect must not desync terminal state

**Primary Code**
- `NovaTerminal/Core/RustPtySession.cs`
- `NovaTerminal/Core/ShellHelper.cs`

---

## G. Observability & Debuggability

### G1. Deterministic Replay (Missing Feature)

**Purpose**
- Reproduce bugs deterministically
- Enable golden-state testing

**Recommended Location**
- New module: `NovaTerminal/Core/Replay/`
- Feed recorded byte streams into:
  - `AnsiParser` → `TerminalBuffer`

**Tests**
- Extend `NovaTerminal.Tests` with replay-based assertions

---

## H. Production-Grade Exit Criteria

NovaTerminal is production-grade when:

- 24h stress test passes:
  - vim
  - htop
  - tmux
  - ssh
- Zero flicker
- Zero buffer corruption
- Identical behavior across Windows, Linux, macOS

---

## Quick Debugging Cheat Sheet

- VT parsing bugs → `Core/AnsiParser.cs`
- Wrapping / reflow bugs → `TerminalBuffer.cs`, `TerminalRow.cs`
- Alternate screen issues → `TerminalBuffer.cs`
- Flicker → `TerminalView.cs`, `TerminalDrawOperation.cs`
- UI lag under load → `RustPtySession.cs`, `TerminalView.cs`
- SSH security → `RustPtySession.cs`, `VaultService.cs`

---

## Final Note

> UI features attract users.  
> Terminal correctness keeps them.

This mapping should be treated as **authoritative** when prioritizing work.
