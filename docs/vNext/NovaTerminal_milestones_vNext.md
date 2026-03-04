
# NovaTerminal — GitHub Milestones Plan (12 Months)

This document defines **recommended GitHub milestones** for the NovaTerminal roadmap.  
Each milestone corresponds to a **major capability step** toward making NovaTerminal a
deterministic, observable, and developer‑grade terminal.

Milestones assume a **12‑month roadmap** with incremental releases.

---

# Milestone M1 — Observability Foundations
**Timeline:** Month 1–2

## Goals
Expose renderer performance and enable deterministic state export.

## Issues
- Render Performance HUD
- Terminal Snapshot Export
- Metrics instrumentation improvements
- HUD toggle command
- HUD performance tests

## Deliverables
- Toggleable GPU performance overlay
- Export terminal screen as:
  - ANSI
  - JSON cell grid
  - PNG snapshot
- Bug report bundle export

## Acceptance Criteria
- HUD overhead <3% frame time
- Snapshot export <500ms
- Metrics visible during replay

---

# Milestone M2 — Replay Intelligence
**Timeline:** Month 3–4

## Goals
Upgrade replay system from simple playback to **interactive timeline navigation**.

## Issues
- ReplayIndex implementation
- Replay timeline UI
- Replay markers
- Replay bookmarks
- Timestamp seek API

## Deliverables
- Timeline scrubber
- Fast seek support
- Replay markers:
  - resize
  - alt screen
  - stderr bursts
- Replay bookmarks

## Acceptance Criteria
- Seeking deterministic
- Index built automatically on load
- Works with large recordings

---

# Milestone M3 — Structured Terminal
**Timeline:** Month 5–6

## Goals
Introduce structured command awareness.

## Issues
- Command boundary detection engine
- OSC shell markers integration
- Command event recording in replay
- Command list UI
- Command folding

## Deliverables
Command structure detection:

PROMPT  
COMMAND  
STDOUT  
STDERR  
EXIT CODE  

UI features:

- command history list
- collapse command output
- show failed commands

## Acceptance Criteria
- >95% command detection accuracy
- Works with bash and zsh
- Works over SSH sessions

---

# Milestone M4 — Engineering Credibility
**Timeline:** Month 7–8

## Goals
Position NovaTerminal as a **correctness and debugging platform for terminal apps**.

## Issues
- VT torture test runner
- Unicode diagnostics overlay
- Font fallback inspector
- Resize stress testing

## Deliverables
Command:

novaterm vttest run

Outputs:

- compatibility score
- snapshot artifacts
- replay recordings

Unicode diagnostics overlay:

- grapheme clusters
- ambiguous width characters
- fallback fonts

## Acceptance Criteria
- deterministic results
- CI compatible
- per‑OS baseline support

---

# Milestone M5 — Performance Guard
**Timeline:** Month 9–10

## Goals
Enable performance regression detection using replay workloads.

## Issues
- Performance baseline recording
- Performance comparison CLI
- CI performance reporting
- Replay workload generator

## Deliverables

Commands:

novaterm perf record  
novaterm perf compare

Metrics tracked:

- frame time
- draw calls
- dirty rows
- glyph cache hit rate

## Acceptance Criteria
- stable results within tolerance
- CI integration
- JSON + JUnit output

---

# Milestone M6 — Remote Platform
**Timeline:** Month 11–12

## Goals
Introduce sharing and extensibility capabilities.

## Issues
- Session relay server
- Web terminal viewer
- Replay upload viewer
- Plugin SDK

## Deliverables

### Session Relay
Architecture:

Local terminal  
→ event stream  
→ relay server  
→ browser viewer

Viewer features:

- live playback
- resize support
- read‑only access

### Plugin SDK

APIs:

- cell grid access
- replay event stream
- command boundaries
- metrics

## Acceptance Criteria
- session latency <300ms
- share links expire
- plugins isolated from host

---

# Suggested Release Tags

| Version | Milestone |
|-------|-------|
| v0.9 | Observability Foundations |
| v1.0 | Replay Intelligence |
| v1.1 | Structured Terminal |
| v1.2 | Engineering Credibility |
| v1.3 | Performance Guard |
| v1.4 | Remote Platform |

---

# Long‑Term Vision

After these milestones NovaTerminal becomes known for:

- deterministic terminal replay
- structured command awareness
- GPU performance observability
- engineering‑grade VT correctness testing
- collaborative terminal sessions
