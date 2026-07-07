# ADR: NovaTerminal as an Agent Host

_Status: **Accepted** (2026-07-07) · Date: 2026-07-07 · Supersedes the strategic framing of
`docs/ghostty-gaps/ghostty_gap_roadmap.md`; amends Phases 4–5 of `docs/ROADMAP.md`._

---

## Context

### What changed in the industry

Since this project started, the terminal market has bifurcated:

1. **Pure emulators** competing on speed and correctness. Ghostty won this
   race: fastest rendering, strong correctness, a full-time famous maintainer,
   and default-recommendation status in every 2026 comparison.
2. **Agent-aware terminals** built to host CLI coding agents (Claude Code,
   Codex CLI, Gemini CLI). Warp rebranded itself an "Agentic Development
   Environment"; tools like cmux and Agent Deck exist purely to orchestrate
   agent sessions. CLI agents are now a mainstream developer workflow, and the
   terminal a developer chooses materially affects their agent workflow.

What agent-aware terminals compete on today: session observability (agents
reading screen state), session status detection (running / waiting-for-input /
idle), completion & stall notifications, programmatic session spawning, and
parallel-session orchestration. Most implementations are tmux-layer inference
or proprietary. None offer deterministic replay.

### Where NovaTerminal stands

- Chasing Ghostty on its own axis (speed + correctness as the headline) is
  not winnable for a nights-and-weekends solo project. The gap-closure
  program produced real quality, but as a *strategy* it is a treadmill.
- NovaTerminal's unusual investments map directly onto what agents need:
  - **Cell-based buffer + VT correctness** → structured, trustworthy screen
    reads ("the agent sees exactly what a human sees").
  - **Thread-safe PTY ownership** → real process/session status, not
    heuristics layered on tmux.
  - **Deterministic replay** → record and replay agent sessions with
    byte-for-byte buffer parity. No other terminal can do this.
  - **Existing MCP server** (`src/NovaTerminal.McpServer`) → the transport
    and packaging already exist; today it is repo-facing (docs, VT
    explanations, profile validation), not session-facing.
  - **Native SSH profiles** → agents can be given correct remote sessions,
    which no agent-aware terminal offers today.

## Decision

**NovaTerminal positions itself as the correct, deterministic terminal that is
the best host for AI agents — "enable, don't embed."**

Concretely:

1. Extend the MCP surface from *repo-facing* to *session-facing*: agents can
   observe, query status of, and (with permission) act inside live terminal
   sessions.
2. Do **not** build embedded AI (chat sidebars, model integrations, vendor
   bets). Command Assist remains the only user-facing AI-adjacent surface and
   keeps its existing opt-in constraints.
3. Demote Ghostty gap closure from an active program to a regression gate:
   the VT conformance CI lane stays blocking; new matrix cells are closed
   only when a real TUI application or agent workflow hits them.
4. Treat distribution (brew / winget / Flatpak) as a parallel first-class
   workstream: adoption goals fail on install friction before they fail on
   features.

## Consequences

- VT correctness stops being a me-too feature and becomes the trust
  foundation of the agent story.
- Replay gains a second audience: debugging *agent* behavior, not just
  terminal bugs.
- The McpServer grows a live control path and therefore a security model
  (below). It is no longer "read-only, stdio-only" once Milestone A3 ships;
  README and docs must be updated at that point.
- Roadmap Phases 4–5 (`docs/ROADMAP.md`) are effectively reframed by the
  milestones below; 5.2 Automation Hooks is absorbed into A2.

---

## Architecture

### Control channel

The MCP server is a standalone stdio process; live sessions exist in the
running App. The design is a **local IPC control endpoint hosted by the App,
proxied by the MCP server**:

```
agent (Claude Code / Codex / …)
   │  stdio MCP
   ▼
NovaTerminal.McpServer  ── local IPC (named pipe / unix socket) ──▶  NovaTerminal.App
                                                                    (session registry,
                                                                     buffer snapshots,
                                                                     PTY status, input)
```

- The stdio MCP server remains the single public surface; the IPC contract is
  internal and versioned.
- The App endpoint is loopback-only, per-user, and off by default
  (`Settings → Agent Access`).
- Buffer reads go through the existing snapshot boundary — the same
  deterministic serializer the replay/parity tests use. No new read path.

### Module placement (respects enforced invariants)

- Session-control contracts (DTOs, IPC protocol) live in a new leaf library
  (working name `NovaTerminal.AgentHost.Contracts`) referenced by both App
  and McpServer. `VT` stays a leaf; `Pty` still never sees VT types — status
  events surface via the existing App orchestration layer.
- No production assembly references test libraries; architecture tests gain
  assertions for the new edges before the first merge.

### Permission model

| Capability | Default | Gate |
|---|---|---|
| list sessions, read screen/scrollback, session status | off | user enables Agent Access (observe) |
| notifications (completion / stall) | off | same toggle |
| send input, spawn/close sessions & panes | off | separate explicit opt-in, per-profile allowlist for SSH |
| replay export | off | observe permission + explicit export action |

Every acting tool call is journaled to a visible activity log in the UI.
Nothing is silent — same principle as the credential-consent rules.

---

## Roadmap

Each milestone is independently shippable and announceable. Estimates assume
~5–15 hrs/week.

### A1 — Observe (first public demo)

**Deliverables**
- [x] App-side IPC endpoint + session registry exposure (PRs #183, #184)
- [x] MCP tools: `novaterminal.list_sessions`, `novaterminal.read_screen`
      (visible grid + cursor), `novaterminal.read_scrollback` (ranged)
- [x] `Agent Access (observe)` settings toggle, off by default (PR #184)
- [ ] Docs + demo recording: Claude Code reading a live `htop`/`vim` session

**Acceptance criteria**
- Screen reads are snapshots from the deterministic serializer; a replay of
  the same byte stream yields an identical `read_screen` result (parity test)
- Zero behavior change when the toggle is off (architecture + integration test)

### A2 — Status & notifications

**Deliverables**
- [x] Session status model: `running` / `awaitingInput` / `idle` / `exited`
      with precise/heuristic confidence tiers (PTY-derived, not scrape-based;
      exit status included; foreground-process reporting deferred) (PR #186)
- [x] MCP tools: `novaterminal.get_session_status`,
      `novaterminal.wait_for_events` (cursor long-poll over a bounded event
      ring) for completion/stall events (PRs #187, #188)
- [x] In-app notification for long-running command completion, default-off
      toggle (absorbs ROADMAP 5.2); OS-native notification backends remain a
      follow-up

**Acceptance criteria**
- Status transitions covered by replay-driven tests (recorded fixtures per
  state)
- Stall detection has an explicit, documented definition (no output for N s
  while `running`) — no heuristics that can't be tested

### A3 — Act (permissioned)

**Deliverables**
- [ ] MCP tools: `novaterminal.send_input`, `novaterminal.spawn_session`
      (local profile or SSH profile by name), `novaterminal.close_session`
- [ ] Separate opt-in + per-profile SSH allowlist + UI activity journal
- [ ] Threat-model doc for the acting surface

**Acceptance criteria**
- Acting tools hard-fail when only observe permission is granted (tested)
- Input injection is byte-faithful and replay-recorded like any PTY input

### A4 — Replay for agents (the moat)

**Deliverables**
- [ ] `novaterminal.export_replay` for a session/time range
- [ ] CLI: `nova replay <file>` headless render-to-text/PNG for CI and
      agent-run postmortems
- [ ] Docs: "Debug what your agent did, frame by frame"

**Acceptance criteria**
- Exported replays round-trip through the existing replay runner with
  byte-identical buffer snapshots on all three OSes

### Parallel track — Distribution

- [ ] Homebrew cask/formula (macOS, Linux)
- [ ] winget manifest (Windows)
- [ ] Flatpak (Linux)
- [ ] Each A-milestone announcement links a one-command install

---

## Non-goals

- Embedded chat UI, model API keys, or any model-vendor dependency
- Agent orchestration UI (fleets, worktree-per-task) — v1 hosts sessions;
  orchestrators like cmux can build *on* the MCP surface
- Remote (network-exposed) control endpoint
- Weakening any existing invariant: determinism, parity, replayability, and
  the zero-flicker rule gate this work exactly as they gate everything else

## Final rule (unchanged)

> **If it is not testable, it is not shippable.**

Agent-facing reads and events are only trustworthy because they are built on
the deterministic core — that is the product.
