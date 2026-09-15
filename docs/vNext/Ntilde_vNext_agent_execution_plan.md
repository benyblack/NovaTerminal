
# NovaTerminal — AI Agent Execution Plan

This document describes how multiple AI coding agents can implement
NovaTerminal roadmap features in parallel.

---

# Agent Roles

Agent A — Core / Replay
Agent B — Renderer / Performance
Agent C — UI / UX
Agent D — Tooling / CI

---

# Phase 1 — Observability (Weeks 1–6)

Agent B
Implement Render HUD overlay

Steps
1. Add overlay render pass
2. Bind metrics from RenderPerfMetrics
3. Create toggle command

Agent A
Implement snapshot export

Steps
1. Serialize terminal grid
2. Implement ANSI export
3. PNG export via renderer

Agent D
Add snapshot test harness

---

# Phase 2 — Replay Intelligence (Weeks 7–12)

Agent A
ReplayIndex implementation

Steps
1. Scan recording file
2. Build timestamp → offset map
3. Cache index

Agent C
Timeline UI

Features
- scrub bar
- markers
- bookmarks

Agent D
Replay seek regression tests

---

# Phase 3 — Structured Terminal (Weeks 13–18)

Agent A
Command boundary engine

Steps
1. Parse OSC shell markers
2. Emit command events
3. Record events in replay

Agent C
Command folding UI

Features
- collapse output
- show failed commands

Agent D
Command detection test suite

---

# Phase 4 — Engineering Credibility (Weeks 19–24)

Agent A
VT torture suite

Tests
- scroll storms
- unicode sequences
- resize storms

Agent C
Unicode diagnostic overlay

Agent D
Golden baseline test runner

---

# Phase 5 — Performance Guard (Weeks 25–32)

Agent B
Performance baseline recorder

Agent A
Perf comparison engine

Agent D
CI integration

---

# Phase 6 — Remote Platform (Weeks 33–52)

Agent A
Session event streaming

Agent B
Stream serialization

Agent C
Web viewer UI

Agent D
Relay server

---

# Parallelization Rules

1. Core and renderer changes must not introduce nondeterminism
2. All replay changes require regression tests
3. Rendering code must avoid per-frame allocations

---

# Quality Gates

Before merging:

- replay determinism test
- performance regression test
- VT torture test

