# NovaTerminal vNext — Target Architecture (12-Month Plan)

> Goal: evolve NovaTerminal into a **deterministic, observable, structured terminal platform**  
> (GPU-first renderer + replay correctness + command awareness + shareability).

This document is meant to be the **single reference** for coding agents and human reviewers.

---

## 0. Architectural Principles (Non‑Negotiables)

### Determinism-first ✅
- Any feature that affects replay must be **replayable** and **indexable**.
- “Non-deterministic inputs” (time, random, GPU timing, host environment) must be:
  - captured into the recording, or
  - explicitly excluded from deterministic paths.

### Render loop purity ✅
- **No per-frame allocations** in hot paths.
- “Observability features” (HUD/overlays) must not pollute render timings.

### Event-sourced terminal model ✅
- The primary durable artifact is the **event stream** (recording).
- Derived views (timeline, command list, folds) are **indexes** over events.

### UI is a projection ✅
- UI state is a projection of model state + indexes.
- “Fancy UI” must not corrupt the core terminal state model.

---

## 1. High-Level System Diagram

```
+--------------------+        +-----------------------+
|      UI Shell      |        |        CLI Tool       |
|  Avalonia (App)    |        |   novaterm (cli)      |
+---------+----------+        +-----------+-----------+
          |                               |
          | ViewModels / Commands         | Record/Compare/Run suites
          v                               v
+--------------------+        +-----------------------+
|   Session Manager  |<------>|   Workload Runner     |
|  panes/tabs/layout |        | (vttest/perf scripts) |
+---------+----------+        +-----------+-----------+
          |
          | (Terminal Session API)
          v
+--------------------+     events     +--------------------+
|  Terminal Core     |--------------->| Recording Writer   |
|  (State + VT)      |                | (.novarec + idx)   |
+---------+----------+                +---------+----------+
          |                                        |
          | render model                            | read + seek
          v                                        v
+--------------------+                +--------------------+
|  Renderer (GPU)    |<---------------| Recording Reader   |
| (layout + glyphs)  |                | + Replay Engine    |
+---------+----------+                +---------+----------+
          |                                        |
          | metrics                                 | indexes
          v                                        v
+--------------------+                +--------------------+
| Perf Metrics Bus   |--------------->| Index Builders     |
| (JSONL + HUD feed) |                | (timeline/commands)|
+--------------------+                +--------------------+
```

---

## 2. Core Subsystems

### 2.1 Terminal Core (VT Engine + State)
**Responsibilities**
- Parse VT/ANSI sequences
- Maintain the canonical terminal grid state
- Manage scrollback, alternate screen, cursor state
- Emit **semantic events** (screen updates, mode changes)

**Key interfaces**
- `ITerminalSession`
  - `WriteInput(ReadOnlySpan<byte> input)`
  - `OnPtyOutput(ReadOnlySpan<byte> bytes)`
  - `Snapshot(): TerminalSnapshot` (for export + diff)
- `ITerminalEventSink`
  - `Emit(TerminalEvent e)`

**Data model**
- `TerminalGrid` (cells + metadata)
- `TerminalCell` (packed where possible) + side tables:
  - grapheme clusters
  - style attributes
  - font fallback metadata (diagnostics)

**Determinism notes**
- Normalize ambiguous-width/unicode width behavior via:
  - explicit width table version
  - captured font profile ID (for diagnostics + CI)

---

### 2.2 Renderer (GPU-first)
**Responsibilities**
- Convert terminal grid into draw commands
- Manage glyph atlas and caching
- Handle scaling, snapping, DPI, and subpixel issues
- Provide screenshot render (PNG export) and HUD overlay pass

**Render passes**
1. Background pass
2. Cell text pass
3. Cursor/selection pass
4. Overlay pass (HUD/diagnostics)

**Key constraints**
- No allocations per frame
- Metrics collection must be “tap-only”:
  - counters updated in-place
  - sampled at fixed cadence

---

### 2.3 Perf Metrics Bus
**Purpose**
- Bridge between renderer/core metrics and:
  - JSONL writer (perf regression tooling)
  - HUD overlay (real-time observability)

**API sketch**
- `IRenderMetricsSource -> RenderPerfMetrics`
- `IMetricsSampler` (cadence, smoothing)
- `IMetricsWriter` (JSONL)

**Artifacts**
- `perf.jsonl` per workload
- `perf-summary.json` for CI

---

## 3. Recording & Replay Architecture

### 3.1 Recording Format vNext (.novarec)
**Principle:** record events, not pixels.

**Core event types**
- `PtyOutputChunk { t, bytes }`
- `Resize { t, cols, rows }`
- `ModeChange { t, flags }` (alt screen, mouse mode, etc.)
- `CommandBoundary { t, kind, data }` (Q2+)
- `Marker { t, kind, payload }` (errors, bookmarks)
- `Metadata { schemaVersion, os, appVersion, fontProfileId, dpiProfileId }`

**Writer placement**
- At session boundary: `TerminalCore -> RecordingWriter`
- Optionally include “derived” events (command boundaries) if detected

---

### 3.2 Replay Engine
**Responsibilities**
- Provide deterministic playback with:
  - seek to timestamp
  - step-by-event
  - speed controls
- Drive the same `TerminalCore` state transitions as live session

**Interfaces**
- `IReplayPlayer`
  - `Play() / Pause()`
  - `Seek(TimeSpan t)`
  - `Step(int events)`
  - `GetMarkers()` (from index)

---

### 3.3 Replay Index (Sidecar)
**Goal:** O(1) seek, O(log n) marker queries.

**Index artifacts**
- `.novarec.idx` (binary)
- Optional `.novarec.meta.json` for quick display

**Index tables**
- `TimeToOffset[]`
- `MarkerTable[]` (kind -> sorted timestamps)
- `CommandTable[]` (command start/end offsets, exit codes)

**Build strategy**
- Built on write (streaming) OR built on read (scan once + cache)
- Versioned, forward-compatible

---

### 3.4 Replay Projections (UI features)
Projections derived from replay + index:
- Timeline markers
- Command list
- Fold regions
- Error regions
- Diff comparisons (two snapshots)

---

## 4. Structured Terminal Intelligence

### 4.1 Command Boundary Engine
**Goal:** detect the command lifecycle robustly.

**Preferred mechanism**
- OSC markers (e.g., 133 series) emitted by shell integration

**Integration approach**
- Provide a user-installable snippet:
  - bash/zsh: PS1 + PROMPT_COMMAND hooks
  - optional PowerShell profile additions
- Engine parses these sequences and emits `CommandBoundary` events:
  - `PromptStart`
  - `CommandStart`
  - `CommandEnd`
  - `ExitCode`

**Fallback mechanism**
- Heuristics (only if markers absent):
  - prompt regex
  - timing windows
  - line discipline assumptions
> Keep fallback clearly labeled “best-effort” and disable by default if it causes noise.

---

### 4.2 Command Folding Model
**Principle:** folding is a UI projection over the event stream.
- Do NOT delete scrollback.
- Maintain a “visible span map”:
  - command regions collapsed -> summarized view
- Search:
  - hits inside collapsed spans show “hit markers”
  - expand on demand

**Data structures**
- `FoldRegion { startEventOffset, endEventOffset, state }`
- `VisibleSpanIndex` (fast mapping for scroll)

---

### 4.3 Semantic Error Detection
**Trigger sources**
- Exit code != 0 (from boundary engine)
- stderr bursts (from output classification, best-effort)
- known patterns (configurable)

**Action policy**
- Never auto-call AI.
- Provide explicit actions:
  - copy context
  - search docs
  - open issue template bundle

---

## 5. Testing & Quality Architecture

### 5.1 Deterministic Golden Pipeline (per OS/font profile)
**Problem you already hit:** fonts + OS-specific baselines.

**Solution**
- Baselines are keyed by:
  - OS (win/mac/linux)
  - font profile (e.g., “NerdFontInstalled”, “DefaultFallback”)
  - DPI profile
- CI chooses an explicit profile.
- “No system font installation” as a dependency:
  - use bundled fonts in test assets where possible.

**Artifacts**
- `.novarec` recording
- `snapshot.json` (canonical state)
- `snapshot.png` (visual)
- `diff.png` (heatmap)

---

### 5.2 VT Torture Suite Runner
**Goal:** correctness credibility and regression gating.

**Runner**
- CLI: `novaterm vttest run`
- Emits:
  - pass/fail summary
  - compatibility score
  - artifact bundle

**Suite composition**
- VTTEST-derived sequences (where licensable)
- Resize storms (fast + randomized but seeded)
- Unicode stress (combining marks, ZWJ sequences)
- Alt screen transitions
- Full-screen TUI traces (htop, mc, vim, lazygit, etc.)

---

### 5.3 Performance Regression Guard
**CLI**
- `novaterm perf record <workload>`
- `novaterm perf compare <baseline> <candidate>`

**Comparison model**
- tolerant thresholds:
  - default ±5%
  - user-configurable per metric
- outputs:
  - JSON summary
  - JUnit for CI

---

## 6. Remote & Platform Architecture

### 6.1 Session Relay (Live Share v1)
**Scope v1**
- read-only viewer
- no remote input
- TLS + expiring token

**Data model**
- Stream the same event model used by replay:
  - “live .novarec stream” (append-only)
- Viewer can:
  - follow live tail
  - pause and scrub recent buffer

**Components**
- Local client:
  - emits events over WebSocket
  - signs requests with token
- Relay server:
  - validates token
  - fans out stream to viewers
- Web viewer:
  - renders events using xterm.js or cell-stream projection

---

### 6.2 Remote Replay Viewer (Upload)
**Model**
- Upload `.novarec` + `.idx`
- Browser playback with markers

**Key requirement**
- Streamed loading; avoid loading full file into memory.

---

### 6.3 Plugin SDK (Experimental)
**Principle:** plugins must not crash the host.

**Minimum safe surface (read-only first)**
- Cell grid snapshot access
- Replay event tap
- Metrics stream
- Command boundary events

**Isolation options**
- Out-of-proc plugins (preferred)
- In-proc with strict contract + fail-fast unloading (risky)

**Versioning**
- Semantic versioned API package
- Compatibility checks on load

---

## 7. Suggested Repo Layout (Optional)

```
src/
  NovaTerminal.Core/           (Terminal core, events, replay contracts)
  NovaTerminal.Rendering/      (GPU renderer, glyph cache, overlays)
  NovaTerminal.Replay/         (reader/player/index)
  NovaTerminal.Shell/          (shell integration snippets + helpers)
  NovaTerminal.Cli/            (vttest/perf/snapshot/share commands)
  NovaTerminal.App/            (Avalonia UI shell)
  NovaTerminal.Remote/         (relay client/server protocols) [Q4]
tests/
  Core.Tests/
  Rendering.Tests/
  Replay.Tests/
  VTTests/                     (torture suite + fixtures)
  Perf.Tests/                  (perf record/compare harness)
assets/
  fonts/
  recordings/
  baselines/
```

---

## 8. Roadmap-to-Architecture Traceability

| Roadmap Feature | Subsystems Touched | Key Artifacts |
|---|---|---|
| Render HUD | Rendering + Metrics | HUD overlay pass, JSON metrics |
| Replay timeline | Replay + Index + UI | `.idx`, markers, seek API |
| Snapshot export | Core + Rendering | ANSI/JSON/PNG + zip bundle |
| Command boundaries | Shell + Core + Replay | Command events in recording |
| Folding | UI projection + indexes | Fold regions, visible span map |
| VT torture | CLI + Replay + Baselines | suite runner + artifacts |
| Perf guard | CLI + Metrics | perf.jsonl + compare |
| Session relay | Remote + Replay contracts | live event stream |
| Plugin SDK | Core APIs + isolation | versioned SDK |

---

## 9. “First 30 Days” Execution (Recommended)

1. Implement **Render HUD** end-to-end behind a feature flag.
2. Implement **Snapshot export** (JSON + PNG) with schema versioning.
3. Add **ReplayIndex** scanning and a minimal `Seek(t)` API.
4. Add 3 deterministic replay regression tests:
   - seek to resize
   - seek across alt-screen
   - seek across heavy scroll

If those 4 land cleanly, the rest of the roadmap becomes much easier.

---

## Appendix A — Event Contract (Skeleton)

```
TerminalEvent
  - type
  - t (monotonic timestamp)
  - payload (typed)
```

Recommended:
- store time in microseconds since session start
- store monotonic clock source in metadata

---

## Appendix B — Profiles (Font/DPI/OS)
Create explicit profiles:
- `FontProfileId`: e.g., `bundled-jetbrainsmono`, `system-nerdfont`
- `DpiProfileId`: e.g., `100%`, `150%`
- baseline keys = `os + fontProfileId + dpiProfileId`

This directly addresses CI brittleness from font installs.

