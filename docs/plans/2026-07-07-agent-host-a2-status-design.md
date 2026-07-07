# Agent Host A2 (Status & Notifications) Design

Milestone **A2** of `docs/agent-host/DIRECTION.md`: agents can ask what a
session is *doing* — running, waiting for input, idle, exited — and wait for
completion/stall events instead of polling screens. Also absorbs ROADMAP §5.2
(automation hooks / OS notifications). Builds directly on the A1 surface
(#183, #184, #185).

## Goal

Expose per-session status and an event channel:

- `novaterminal.get_session_status` — current status with confidence tier
- `novaterminal.wait_for_events` — long-poll for status changes, command
  completion, bells, and stalls
- status summary included in `novaterminal.list_sessions`
- optional user-facing notification when a long-running command finishes

The change must:

- derive status from **owned signals** (PTY process state, shell-integration
  events, alt-screen state) — never from screen scraping
- be explicit about precision: a `confidence` field distinguishes
  shell-integration-backed status from PTY-only heuristics
- keep every stall/idle definition explicit and testable (fixed thresholds,
  injectable clock) — no untestable heuristics
- stay observe-only and behind the existing `Agent Access (observe)` toggle;
  protocol changes are strictly additive (version stays 1)

## Current State

Signals that already exist, per pane:

- **PTY truth (always available)** — `ITerminalLifecycle`
  (`src/NovaTerminal.Pty/ITerminalSession.cs`): `IsProcessRunning`,
  `HasActiveChildProcesses`, `ExitCode`, `OnExit`; `ITerminalIO.OnOutputReceived`.
- **Shell integration (precise, opt-in)** — `ShellLifecycleTracker`
  (`src/NovaTerminal.App/CommandAssist/ShellIntegration/Runtime/`):
  `PromptReady`, `CommandAccepted`, `CommandStarted`,
  `CommandFinished(exitCode, duration)`, `WorkingDirectoryChanged`, already
  surfaced as `TerminalPane` events (`CommandStarted`, `CommandFinished`,
  `ProcessExited`, `OutputReceived`, `BellReceived`).
- **VT state** — `TerminalBuffer.IsAltScreenActive` (a TUI owns the screen).
- The agent-host registry (`AgentSessionRegistration`) already holds a
  lock-protected metadata snapshot pushed from the UI thread; status extends
  the same pattern.

Nothing about "status" crosses the IPC boundary today.

## Status Model

One enum, two confidence tiers.

| Status | Precise (shell integration active) | Heuristic (PTY-only) |
|---|---|---|
| `running` | between `CommandStarted` and `CommandFinished`, or alt-screen active | `HasActiveChildProcesses`, or alt-screen active |
| `awaitingInput` | `PromptReady` seen since last command | process alive, no active children, not alt-screen |
| `idle` | `awaitingInput` with no output/input for `IdleThresholdSeconds` (default 60) | same, from heuristic `awaitingInput` |
| `exited` | `OnExit` fired / `!IsProcessRunning`; carries `ExitCode` | same (PTY truth in both tiers) |

Stall is an **event**, not a status: `running` with no output for
`StallThresholdSeconds` (default 30) emits `stalled`; output resumption emits
`statusChanged(running)`. Definitions live in contracts constants; both
thresholds are server-side and reported in the status DTO so clients never
hard-code them.

`confidence` is `precise` when the pane's shell integration is active,
`heuristic` otherwise. Agents (and Warp-style comparisons) get honesty for
free: NovaTerminal reports *how* it knows, which tmux-layer scrapers cannot.

## Architecture

### App side

- `AgentHost/AgentSessionStatus.cs` — immutable status snapshot:
  `{ Status, Confidence, ExitCode?, CurrentCommand?, StatusSinceMs,
  LastOutputAtMs, StallThresholdSeconds, IdleThresholdSeconds }`.
- `AgentSessionRegistration` gains a lock-protected `Status` slot plus an
  `ApplySignal(...)` reducer — a pure state machine with an injectable clock,
  mirroring `ShellLifecycleTracker`'s testability pattern.
- `TerminalPane` wiring (all UI-thread, same touch points as the A1 snapshot
  pushes): `OutputReceived`, `CommandStarted`, `CommandFinished`,
  `ProcessExited`, `BellReceived`, shell-integration prompt events, and
  `Buffer.OnScreenSwitched` for alt-screen transitions.
- **Timers:** one shared 1 s tick in `AgentHostService` (running only while
  the endpoint is up) sweeps registrations for idle/stall transitions. No
  per-session timers; cost is O(open panes) per second, only when the user
  opted in.
- **Event ring:** a bounded per-service ring buffer (256 entries, monotonically
  increasing `seq`, oldest evicted) of
  `{ seq, timestampMs, paneId, type, exitCode?, durationMs? }` where `type` ∈
  `statusChanged | commandFinished | bell | stalled | sessionOpened |
  sessionClosed`. Long-pollers read from a cursor; eviction is reported via
  the response's `oldestSeq` so a lapsed client knows it missed events.

### Protocol (additive; version stays 1)

- `getSessionStatus { paneId }` → status DTO
- `waitForEvents { sinceSeq, timeoutMs }` → `{ events[], nextSeq, oldestSeq }`
  — returns immediately if events exist past the cursor, otherwise parks up to
  `timeoutMs` (server-capped at 25 s, under the client's 30 s guard) and
  returns empty on timeout. One long-poll per connection at a time.
- `SessionInfo` gains optional `status` + `confidence` fields (omitted when
  unknown, same nullable-field pattern as `tabId`).

### MCP tools

- `novaterminal.get_session_status` — one session's status, human-readable
- `novaterminal.wait_for_events` — long-poll wrapper; the tool description
  teaches the cursor protocol ("pass nextSeq from the previous call")
- `novaterminal.list_sessions` — status column added

### Notifications (ROADMAP §5.2 absorbed)

App-side and independent of agents: when `CommandFinished` arrives with
`duration >= NotifyAfterSeconds` (default 30) for an unfocused pane, raise the
existing in-app toast; OS-native notifications (Windows toast / macOS
UNUserNotification / libnotify) ride the same event behind
`LongCommandNotificationsEnabled` (default off, settings toggle). This ships
last and is UI-only — no protocol involvement.

## Alternatives Considered

**Push events over a persistent stream instead of long-poll** — a second frame
type and connection lifecycle complexity for no A2 benefit; MCP tools are
request/response anyway. The ring + cursor gives at-least-once delivery with
explicit loss reporting, which is enough for "tell me when it finishes".

**Foreground-process inspection for better heuristics** (read the PTY's
foreground process name à la tmux) — platform-specific, racy, and the payoff
is small next to shell integration, which we already ship. Deferred; the
`confidence` field leaves room for it.

**Status computed on demand instead of event-driven** — polling
`HasActiveChildProcesses` per request would miss transitions between polls
(no events possible) and does process-tree walks on the request path.
Event-driven with a 1 s sweep is deterministic and testable.

## Testing

- **State machine (pure):** signal sequences × both confidence tiers →
  expected status transitions, with a fake clock driving idle/stall
  thresholds. Exhaustive table tests, no UI.
- **Replay-driven:** the shell-integration events originate in the parser, so
  recorded fixtures (existing Command Assist lanes) drive
  `running → awaitingInput` transitions deterministically — per the
  DIRECTION acceptance criterion.
- **Event ring:** cursor semantics, eviction reporting (`oldestSeq`),
  long-poll wake-on-event and timeout-returns-empty, one-poller-per-connection.
- **IPC/tools:** `wait_for_events` round trip over the real transport;
  status field appears in `list_sessions`; unknown pane → `sessionNotFound`.
- **Off-is-off:** no timer runs and no ring exists while Agent Access is
  disabled (the sweep lives inside `AgentHostService`'s lifecycle).

## Out of Scope

- Acting on sessions (A3), replay export (A4)
- Foreground-process name reporting (future heuristic upgrade)
- Notification actions/deep-links ("click to focus pane") — UI follow-up

## Suggested PR Slicing

1. Status model + reducer + pane wiring + state-machine tests (no protocol
   change; registry-internal)
2. Protocol additions: `getSessionStatus`, `waitForEvents`, event ring,
   `SessionInfo.status` + endpoint tests
3. MCP tools + client long-poll support + tests
4. Long-command notifications (toast + settings toggle) + docs

Each step keeps CI green and is independently shippable.
