# NovaTerminal vNext — 12-Month Feature Roadmap (Draft)

> Theme: **Deterministic + Observable + Structured** terminal workflows (GPU-first).

This roadmap is organized into **4 phases (Q1–Q4)** with concrete deliverables, acceptance criteria, and engineering notes.  
Assumes the current NovaTerminal pillars: **GPU rendering**, **deterministic VT replay**, **JSONL performance metrics**, and **multi-pane UI**.

---

## Scope Summary

### Pillar A — Terminal Observability
- Render Performance HUD
- Snapshot Export (ANSI/JSON/PNG/replay snippet)
- Performance Regression Guard (record/compare budgets)

### Pillar B — Structured Terminal Intelligence
- Command Boundary Detection Engine (shell integrations)
- Command Folding UI
- Semantic Error Detection + (optional) AI actions

### Pillar C — Engineering Credibility
- VT Torture Test Mode (compat suite + score)
- Unicode / Font Diagnostic Mode

### Pillar D — Remote & Platform Foundations
- Session Relay (live share)
- Remote Replay Viewer (upload/playback)
- Plugin SDK (experimental)

---

# Q1 (Months 1–3): Observability Foundations

## 1) Render Performance HUD (P0)
**Goal:** make GPU rendering and perf instrumentation visible and debuggable in-product.

### Deliverables
- HUD overlay (toggle)
- Live counters (frame time, dirty rows/cells, draw calls, glyph cache hit, texture uploads, memory)
- “Copy metrics” action (copies JSON to clipboard)
- Optional “record HUD to JSONL for 10s”

### Acceptance Criteria
- HUD toggles instantly (no app restart)
- Overhead when enabled: **< 3%** average frame-time impact on a representative workload
- Metrics update at a fixed cadence (e.g., 10 Hz) without stalling render loop
- Captured HUD sample is deterministic over replay for same recording (within tolerances)

### Engineering Notes
- Reuse existing `RenderPerfMetrics` / `RenderPerfWriter` paths if available.
- Render HUD in a separate overlay pass to avoid contaminating main draw timing.

---

## 2) Deterministic Replay Inspector v2 (P0)
**Goal:** transform replay from “playback” to “time navigation”.

### Deliverables
- Timeline scrubber with markers:
  - Resize
  - Alternate screen enter/exit
  - Detected command boundaries (stub now; full in Q2)
  - Error events (stderr bursts/exit code)
- Bookmarks (named + timestamp)
- “Jump to next/prev marker”
- Replay diff view (minimal): compare two timestamps (cell grid diff heatmap)

### Acceptance Criteria
- Seeking to any timestamp is deterministic and stable (no transient rendering artifacts)
- “Resize marker seek” reproduces the same layout
- Diff view highlights changed cells without false positives for same timestamp seek

### Engineering Notes
- Add a `ReplayIndex` sidecar (built on load, cached) with:
  - time -> file offset
  - marker metadata
- Keep index format forward-compatible.

---

## 3) Terminal Snapshot Export (P1)
**Goal:** easy bug reports + sharing + CI artifacts.

### Deliverables
- Export current screen:
  - `snapshot.ansi`
  - `snapshot.json` (cell grid + metadata)
  - `snapshot.png`
- Export replay slice (e.g., last 30 seconds) to `.novarec` subset
- “Copy bug report bundle” -> creates a zip

### Acceptance Criteria
- Exports complete in **< 500ms** for typical screen sizes (80x24 to 200x60)
- JSON format is documented and versioned (`schemaVersion`)
- PNG matches the on-screen render within tolerance

---

# Q2 (Months 4–6): Structured Terminal Intelligence

## 4) Command Boundary Detection Engine (P0)
**Goal:** detect PROMPT/COMMAND/OUTPUT/EXIT boundaries reliably.

### Deliverables
- Shell integrations:
  - bash
  - zsh
  - fish (optional if time)
  - PowerShell (optional if time)
- Boundary events emitted into:
  - live session event stream
  - replay recording (so replay inspector can show markers)
- Minimal UI: command list view (recent commands)

### Acceptance Criteria
- 95%+ correct boundary detection across common prompts
- No boundary “leakage” across panes/sessions
- Works with SSH sessions (as long as the remote shell integration is installed)

### Engineering Notes
- Prefer OSC marker approach where possible; fallback to prompt heuristics only as last resort.
- Provide a “shell setup snippet” users can paste into config.

---

## 5) Command Folding UI (P1)
**Goal:** collapse noisy output, navigate by commands.

### Deliverables
- Fold/Unfold per command block
- “Collapse all successful commands”
- “Show only failed commands”
- Export single command block to Markdown

### Acceptance Criteria
- Folding does not lose scrollback data
- Search includes folded content (with hit markers)
- Fold state persists across app restart for current session (optional)

---

## 6) Semantic Error Detection + Actions (P1)
**Goal:** make failures actionable without being a “chat terminal”.

### Deliverables
- Error triggers:
  - exit code ≠ 0
  - stderr burst heuristic
  - known pattern match (e.g., “command not found”)
- Actions:
  - “Explain error” (local template)
  - “Search docs” (opens browser with query)
  - Optional: “Ask AI to explain” (gated / opt-in)

### Acceptance Criteria
- No spam: max one prompt per command unless user expands
- Always shows exact command + exit code context
- AI (if enabled) is never invoked silently

---

# Q3 (Months 7–9): Engineering Credibility Mode

## 7) VT Torture Test Mode (P0)
**Goal:** make correctness measurable and regressions preventable.

### Deliverables
- Built-in suite runner:
  - VTTEST-style sequences
  - resize stress
  - scroll stress
  - alt screen transitions
- Outputs:
  - compatibility score
  - artifact bundle (snapshots + diffs + replay)
- CLI entrypoint: `novaterm vttest run`

### Acceptance Criteria
- Suite is deterministic on a given OS+GPU driver combo
- Golden baselines can be per-OS and per-font-profile (avoid CI breakage)
- Clear reporting: “what failed and why” with minimal reproduction

---

## 8) Unicode / Font Diagnostic Mode (P1)
**Goal:** debug powerline, combining marks, emoji width, fallback issues.

### Deliverables
- Toggle overlay that shows:
  - grapheme cluster boundaries
  - ambiguous width characters
  - fallback font used per glyph
  - cell width/height rounding diagnostics
- “Font lab” page:
  - test strings
  - screenshots
  - copy/paste

### Acceptance Criteria
- Overlay does not alter layout (pure diagnostics)
- Helps diagnose known issues (powerline/nerd fonts) with clear indicators

---

## 9) Performance Regression Guard (P1)
**Goal:** formalize performance budgets using replay + metrics.

### Deliverables
- `novaterm perf record <workload>` -> stores baseline JSONL + metadata
- `novaterm perf compare` -> pass/fail against thresholds
- CI-friendly output (JUnit/JSON summary)

### Acceptance Criteria
- Stable results within tolerance on same machine (define: ±5% default)
- Allows multiple baselines: per OS / GPU / font profile

---

# Q4 (Months 10–12): Remote & Platform Foundations

## 10) Session Relay (Live Share) (P1)
**Goal:** share a live session without building full SaaS immediately.

### Deliverables
- Local streaming agent (client) that emits a compact event stream
- Relay server (minimal) + web viewer (read-only)
- Security:
  - end-to-end encryption (or TLS + shared secret minimum viable)
  - expiring share links

### Acceptance Criteria
- Viewer latency < 300ms on good network
- No keystrokes are accepted remotely in v1 (read-only)
- Users can revoke sessions instantly

---

## 11) Remote Replay Viewer (P2)
**Goal:** upload `.novarec` and view in browser.

### Deliverables
- Upload endpoint + storage (local filesystem or S3-compatible)
- Browser playback:
  - timeline
  - markers
  - speed control
- Share link

### Acceptance Criteria
- Deterministic playback matching desktop replay
- Handles large recordings (streamed loading)

---

## 12) Plugin SDK (Experimental) (P2)
**Goal:** ecosystem seed without locking core architecture.

### Deliverables
- Minimal stable interfaces:
  - cell grid access (read-only)
  - replay event tap
  - command boundaries
  - metrics stream
- Plugin loader (signed/whitelisted optional in v1)
- Example plugins:
  - build monitor
  - git status badge
  - log highlight rules

### Acceptance Criteria
- Plugins cannot crash the host (isolation: process boundary or strict sandbox)
- Versioned API with compatibility checks
- Clear security story (disabled by default)

---

# Cross-Cutting Non-Functional Requirements

## Determinism
- Any feature that touches replay must not introduce nondeterministic branches.
- Document and gate non-deterministic inputs (time, randomness, GPU timing).

## Performance
- Avoid per-frame allocations in render loop.
- Provide perf baselines for HUD off/on, folding on/off, overlays.

## Test Strategy (Mandatory)
- Unit: parser/indexer, boundary engine, export formats
- Integration: replay seek + marker accuracy
- Golden: per-OS + per-font-profile baselines (avoid CI font install breakage)
- Stress: resize + large scrollback + unicode suite

---

# Open-Core Gating Suggestions (Optional)
- Free: replay playback, basic snapshot export, basic command list
- Pro: time-travel diff, perf regression guard, live share, plugin SDK
- Enterprise: policy controls, signed plugins, audit bundles

---

# Appendix: Suggested CLI Surface
- `novaterm replay inspect <file>`
- `novaterm snapshot export`
- `novaterm perf record|compare`
- `novaterm vttest run`
- `novaterm share start|stop` (Q4)
