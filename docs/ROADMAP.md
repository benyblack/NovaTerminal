# NovaTerminal – Product & Engineering Roadmap

_Last reviewed: 2026-04-21._

This roadmap is **authoritative** for NovaTerminal.

It applies to:
- maintainers
- contributors
- automated agents (Antigravity / Codex)

Related execution plans:
- `docs/PRODUCTION_EXECUTION_PLAN.md`
- `docs/MULTI_PANE_EXECUTION_PLAN.md`
- `docs/TABS_EXECUTION_PLAN.md`

---

## Core Principle (Non-Negotiable)

> **Terminal correctness is enforced by automated tests, not discipline.**

No feature work may weaken:
- deterministic behavior
- cross-platform parity
- replayability
- rendering stability

If a change cannot be tested, it must not ship.

---

## Platform Policy

### Tier-1 Platforms
- Windows
- Linux
- macOS

### Behavioral Parity (Required)
Must behave identically across OSes:
- VT / ANSI interpretation
- buffer state
- wrapping & reflow
- alternate screen behavior
- search semantics

### UI Parity (Allowed to Differ)
- window chrome
- blur / transparency
- global hotkeys
- credential storage backend

---

## Architectural Constraints

- Terminal Core is **OS-agnostic**
- Renderer is **semantics-free**
- PTY layer never parses VT
- UI orchestrates, never interprets

These constraints are enforced by tests.

---

# Phase −1 — Automated Correctness Infrastructure (BLOCKING)

> This phase must be completed before **any** correctness or feature work.
> It is a hard gate.

---

## −1.1 Deterministic Replay Harness (G1)

### Deliverables
- [x] Raw PTY byte-stream recording
- [x] Core-only replay runner:
  - `AnsiParser → TerminalBuffer`
- [x] Deterministic buffer snapshot serializer
- [x] Replay-based automated tests

### Acceptance Criteria
- Any terminal bug can be recorded once and replayed in CI
- Replay produces identical buffer state on all OSes
- Replay tests are fast (<5 seconds total)

### Gate
- ❌ No Terminal Core refactors without replay coverage
- ❌ No rendering refactors before replay exists

---

## −1.2 Cross-Platform Parity Tests

### Deliverables
- [x] Same replay fixtures executed on:
  - Windows
  - Linux
  - macOS
- [x] Snapshot comparison across platforms

### Acceptance Criteria
- Buffer snapshots are byte-for-byte identical
- Any divergence must be explicitly documented

### Gate
- ❌ Platform-specific behavior without justification blocks merge

---

## −1.3 Renderer Metrics & Guardrails

### Deliverables
- [x] Renderer instrumentation:
  - full redraw count
  - dirty cell count
  - frame timing
- [x] Threshold-based automated assertions

### Acceptance Criteria
- No flicker regressions
- No steady-state full redraws

### Gate
- ❌ Rendering changes without metrics tests are rejected

---

# Phase 0 — Production-Grade Terminal Correctness

> UI features are irrelevant until this phase is complete.

---

## 0.1 VT / ANSI Core Completeness

### Deliverables
- [x] Complete VT / ANSI state machine (tracked in `docs/vt_coverage_matrix.md` with CI-validated report)
- [x] DEC private modes correctness
- [x] OSC handling:
  - window title
  - OSC 8 hyperlinks
- [x] Proper reset semantics (`ESC c` / RIS)

### Acceptance Criteria
- Correct behavior in:
  - `vim`
  - `htop`
  - `less`
  - `mc`
  - `tmux`
- No cursor desync
- No attribute leakage

---

## 0.2 Alternate Screen & Scrollback Integrity

### Deliverables
- [x] Strict main / alternate buffer separation
- [x] Scrollback isolation
- [x] Cursor and attribute restoration

### Acceptance Criteria
- Entering/exiting full-screen apps leaves main buffer unchanged
- No scrollback corruption

---

## 0.3 Wrapping & Reflow Correctness

### Deliverables
- [x] Correct soft vs hard wrap semantics
- [x] Lossless reflow on resize
- [x] Correct handling of wide glyphs (emoji, CJK — Unicode width model v2)

### Acceptance Criteria
- Resize wide → narrow → wide yields identical content
- No duplication, truncation, or jitter

---

## 0.4 Rendering Stability (Zero-Flicker Rule)

### Deliverables
- [x] Incremental (cell-diff) rendering
- [x] Backing surface cache
- [x] Stable resize under load (Thread-safe PTY backend)

### Acceptance Criteria
- No visible flicker in:
  - `vim` resize
  - `htop` resize
  - fast scrolling output

---

# Phase 1 — Switching Friction Killers

> Make adoption painless for normal users.

---

## 1.1 Session Restore & Workspaces

### Deliverables
- [x] Tab and pane restoration
- [ ] Named workspaces
- [x] Optional startup restore

---

## 1.2 Profile Import

### Deliverables
- [x] Import Windows Terminal profiles (Windows)
- [x] Import `~/.ssh/config`
- [x] Font and color scheme mapping where possible

---

## 1.3 Transient / Toggleable Terminal Window

### Deliverables
- [x] Toggle show/hide without destroying sessions (Quake Mode)
- [x] OS-adaptive behavior

---

## 1.4 Search Overlay

### Deliverables
- [x] Floating search bar (VS Code style)
- [x] Regex and case-sensitive support
- [x] Highlight matches in scrollback
- [x] Navigation (Next/Prev)

---

# Phase 2 — Remote-First Differentiation

> Win remote workflows without becoming a full multiplexer.

---

## 2.1 First-Class SSH UX

### Deliverables
- [x] SSH connection manager
- [x] Tags, labels, groups
- [x] Jump host support
- [x] Identity selection UI

### Native SSH backend (experimental, opt-in)

A native Rust SSH crate ships alongside the default OpenSSH backend, gated by
`TerminalSettings.ExperimentalNativeSshEnabled`. Status and scope are tracked
in `docs/SSH_ROADMAP.md` and `docs/native-ssh/Native_SSH_Test_Matrix.md`.

---

## 2.2 Credential Provider Abstraction

### Deliverables
- [x] Unified credential interface:
  - [x] Windows: DPAPI
  - [ ] macOS: Keychain
  - [ ] Linux: Secret Service
- [x] Explicit user consent

---

## 2.3 Port Forwarding UI

### Deliverables
- [x] Local / remote / dynamic forwarding
- [x] Persistent per profile
- [x] Visible status indicators

---

## 2.4 Lightweight SFTP Actions

### Deliverables
- [x] Upload/download actions
- [x] Command palette integration
- [x] No full file explorer

---

# Phase 3 — Modern Terminal Capabilities

> Optional but differentiating.

---

## 3.1 Inline Images

### Deliverables
- [x] iTerm2 image protocol
- [x] Optional Kitty graphics protocol (Query support + Action support)

---

## 3.2 SIXEL (Optional)

### Deliverables
- [x] SIXEL rendering
- No authoring tools

---

## 3.3 Font & Text Excellence - [STATUS: STABLE]

### Deliverables
- [x] Ligature support (Phase 2 complex shaping)
- [x] Fallback font chain (Deterministic HarfBuzz)
- [x] Emoji width correctness (Unicode 16.0)
- [x] DPI-safe metrics (Physics-Perfect sharpening)
- [x] GPU-Accelerated Glyph Cache (Phase 3 Performance)

---

# Phase 4 — Optional AI (Post-v1)

> Must be opt-in and privacy-respecting.

---

## 4.1 Structured Command Blocks

### Deliverables
- Command grouping
- Exit status and duration
- Copy/share per block

---

## 4.2 Optional Command Intelligence

### Deliverables
- Explain commands
- Rewrite commands
- Generate snippets

### Constraints
- Offline mode supported
- No silent data exfiltration

---

# Phase 5 — Ecosystem (Long-Term)

---

## 5.1 Plugin API (Read-Only First)

### Deliverables
- Hooks for command lifecycle
- No UI injection initially

---

## 5.2 Automation Hooks

### Deliverables
- Notifications for long-running commands
- Exit-status triggers
- OS notification integration

---

# Engineering Quality Gates (Always Enforced)

- Deterministic replay coverage
- Cross-platform parity
- Incremental rendering only
- No full redraw per frame
- Low input latency under load
- No OS logic in Terminal Core

---

# Explicit Non-Goals (v1)

- Full tmux clone
- Mandatory accounts
- Mandatory AI
- IDE features
- Embedded editors

---

## Final Rule

> **If it is not testable, it is not shippable.**

This roadmap exists to protect user trust.
