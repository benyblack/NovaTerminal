# Contributing to NovaTerminal

Thank you for your interest in contributing to **NovaTerminal**.

NovaTerminal is a terminal emulator.  
That means **correctness, determinism, and stability** matter more than feature velocity.

This document explains how to contribute **successfully**.

---

## Core Philosophy

> **Terminal correctness is enforced by automated tests, not discipline.**

If a change cannot be tested, it cannot be merged.

---

## Before You Start

Please read the following documents first:

- `README.md` – project overview
- `README.internal.md` – engineering rules and intent
- `docs/ARCHITECTURE.md` – architectural boundaries
- `docs/MODULE_OWNERSHIP.md` – invariant ownership
- `docs/ROADMAP.md` – test-gated roadmap

PRs that ignore these documents will be rejected.

---

## What We Value

We value contributions that:

- improve VT / ANSI correctness
- fix edge cases in resize, reflow, or alternate screen handling
- improve deterministic replay and test coverage
- reduce flicker or rendering instability
- improve cross-platform parity

We value **correctness over speed**, and **clarity over cleverness**.

---

## What We Do NOT Accept

We generally do not accept PRs that:

- add features without tests
- introduce OS-specific logic into the Terminal Core
- “fix” rendering by changing semantics
- bypass replay or parity tests
- rely on “works on my machine” reasoning
- optimize UI appearance at the expense of correctness

---

## Contribution Types

### 1. Bug Fixes (Highly Welcome)

Bug fixes should include:

- a minimal reproduction
- a replay fixture if applicable
- a test that fails before the fix and passes after

If you cannot reproduce the bug deterministically, explain why.

---

### 2. Correctness Improvements

Examples:
- VT edge cases
- DEC private mode handling
- cursor state bugs
- scrollback isolation issues

These almost always require:
- replay tests
- buffer snapshot assertions

---

### 3. Performance Improvements

Performance work must:
- preserve terminal semantics
- include renderer metrics tests
- demonstrate no regression in correctness

Performance PRs without metrics will be rejected.

---

### 4. Feature Work

Feature work is welcome **only if it does not violate roadmap gates**.

Before starting feature work:
- check `ROADMAP.md`
- ensure Phase −1 and Phase 0 gates are respected

Features that bypass correctness phases will be rejected.

---

## Architectural Rules (Non-Negotiable)

### Terminal Core
- Must remain OS-agnostic
- Must be deterministic
- Must not depend on UI or rendering

### Renderer
- Must not interpret VT semantics
- Must not “fix” buffer issues
- Must use incremental (cell-diff) rendering

### PTY Layer
- Must not parse VT
- Must deliver raw bytes
- Must be bounded and non-blocking

---

## Tests Are Mandatory

### Required Test Coverage

Depending on the change, you must add or update:

- unit tests
- replay tests (`Category=Replay`)
- cross-platform parity tests
- renderer metrics tests (`Category=RenderMetrics`)

### Hard Rule

> If a change is not covered by tests, it will not be merged.

---

## How to Submit a PR

1. Fork the repository
2. Create a focused branch
3. Make small, reviewable commits
4. Add or update tests
5. Run all tests locally
6. Open a PR with a clear description

---

## PR Checklist (Required)

Your PR description **must** answer:

- What invariant does this change affect?
- Which module owns that invariant?
- What tests cover the change?
- Does this affect cross-platform behavior?
- Does this change renderer metrics?

PRs missing this information will be blocked.

---

## CI & Review Process

All PRs are automatically checked by CI:

- unit tests
- replay tests
- parity checks
- renderer metric thresholds

Failing CI blocks merge.

Maintainers may request:
- additional replay fixtures
- stricter assertions
- reduced scope

---

## Coding Style

- Prefer clarity over cleverness
- Avoid premature optimization
- Keep methods small and explicit
- Comment *why*, not *what*

---

## Communication

If you are unsure:
- open an issue first
- describe the problem and approach
- ask before implementing large changes

We are happy to help — but we expect rigor.

---

## Final Reminder

NovaTerminal is a terminal emulator.

Users may forgive missing features.  
They will not forgive broken behavior.

> **Correctness first. Always.**

Thank you for helping make NovaTerminal better.
