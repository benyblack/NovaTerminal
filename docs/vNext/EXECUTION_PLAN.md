# NovaTerminal vNext Execution Plan

This document instructs AI coding agents how to start implementing the vNext architecture.

Agents must follow these rules:

1. Work feature-by-feature.
2. Each feature must be implemented as a vertical slice.
3. Each slice must include tests.
4. Determinism must never be broken.
5. Renderer hot paths must not allocate per frame.

Reference documents:

- architecture-vNext.md
- contracts-vNext.md
- test-pipeline-vNext.md

---

# Phase 1 — Observability Foundations

Implement the following features in order:

1. Render Performance HUD
2. Terminal Snapshot Export
3. ReplayIndex + Seek API

Each feature must be implemented in a separate PR.

---

# Feature 1 — Render Performance HUD

Goal:
Display renderer metrics in real time.

Metrics:

- frame time
- dirty rows
- dirty cells
- draw calls
- glyph cache hit rate
- texture uploads

Requirements:

- HUD toggle via command palette
- update cadence 100ms
- overhead <3%

Architecture:

Renderer
↓
RenderPerfMetrics
↓
HUD overlay render pass

Tests:

- HUD toggle test
- metrics update test
- replay compatibility test

---

# Feature 2 — Snapshot Export

CLI:

novaterm snapshot export

Outputs:

- snapshot.ansi
- snapshot.json
- snapshot.png

Tests:

- snapshot equality
- JSON schema validation
