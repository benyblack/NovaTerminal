# Agent Host A3 (Act, Permissioned) Design

Milestone **A3** of `docs/agent-host/DIRECTION.md`: agents can — with explicit,
separate permission — type into, spawn, and close terminal sessions. Follows
A1 (observe), A2 (status), A4 (replay export); the protocol stays additive on
version 1.

## Goal

- **`novaterminal.send_input`** — inject input into a live session, byte-faithful
  and replay-recorded exactly like human keystrokes.
- **`novaterminal.spawn_session`** — open a new tab running a local profile or
  an *allowlisted* SSH profile, by name; returns the new paneId.
- **`novaterminal.close_session`** — close a live pane.
- **Permission model** (DIRECTION table: "off; separate explicit opt-in,
  per-profile allowlist for SSH"): a new default-off `AgentAccessActEnabled`
  settings toggle, plus a per-SSH-profile `AllowAgentAccess` flag (default
  off). Acting on an SSH session (input, spawn) requires both.
- **Activity journal**: every acting call — including denied ones — is recorded
  to a visible in-app journal. Nothing is silent.
- **Threat-model doc** for the acting surface.

## Current State (verified)

- The observe endpoint (`AgentHostService`) already gates everything on
  `AgentAccessObserveEnabled` and carries a second volatile gate
  (`ReplayExportEnabled`, A4) pushed by MainWindow on settings apply — the
  same pattern extends to the act gate.
- `AgentSessionRegistration` publishes the pane's `ITerminalSession` (volatile
  slot, A4). `ITerminalSession.SendInput(string)` is thread-safe (bounded
  input queue, dedicated writer thread) and already records input to any
  active manual recording (`_recorder?.RecordInput`) — the "replay-recorded
  like any PTY input" acceptance criterion falls out of reusing this path.
  The A4 flight ring records output only; agent input never lands in agent
  exports (privacy invariant upheld).
- Registrations carry `Title`/`ProfileName`/`Kind` ("local"/"ssh") but not the
  profile id; the allowlist check needs identity, so the pane will also push
  `ProfileId`.
- `MainWindow.AddTab(TerminalProfile?)` opens a tab from a local settings
  profile or a store-backed SSH profile (`_sshConnectionService`);
  `CloseActivePaneAsync()` implements the split-aware close path. Both are
  UI-thread-only, so acting requests need a UI executor bridge.
- `SshProfile` (Platform, JSON store with schema versioning) has no
  agent-related field; adding a bool defaulting to false needs no migration.

## Design

### Permission model

| Check | sendInput | spawnSession | closeSession |
|---|---|---|---|
| Observe endpoint running | ✓ (transport exists at all) | ✓ | ✓ |
| `AgentAccessActEnabled` (new, default off) | ✓ | ✓ | ✓ |
| SSH profile `AllowAgentAccess` (new, default off) | ✓ when the target pane is an SSH session | ✓ when the named profile is SSH | — |

- `AgentAccessActEnabled` is a settings-window sub-toggle under Agent access
  (sibling of the A4 replay-export toggle) with copy that states exactly what
  it allows. MainWindow pushes it into `AgentHostService.ActEnabled`
  (volatile) at both settings-apply sites.
- `AllowAgentAccess` on `SshProfile` (persisted, default false), surfaced as a
  checkbox in the connection profile editor. The endpoint checks it through an
  injectable probe (`Func<Guid, bool>` published by MainWindow, reading the
  profile store) so tests can stub it and the service never touches UI state.
- `closeSession` deliberately has no SSH-allowlist requirement: closing is
  destructive-but-bounded (ends a session the user can see disappearing, and
  is journaled); it cannot exfiltrate or execute anything remotely.

Denied calls return distinct stable error codes so agents can tell "ask the
user to opt in" from "pick a different profile": `actDisabled`,
`profileNotAllowed`, `profileNotFound`, `sessionNotRunning`, `actUnavailable`
(endpoint running but no UI executor — e.g. teardown race), `spawnFailed`.

### Protocol (additive, version stays 1)

- `sendInput { paneId, text }` → `{ bytesSent }`. `text` is a JSON string and
  may contain control characters (`\x03` = Ctrl-C, `\r` = Enter …); it is
  UTF-8 encoded and queued through the session's normal `SendInput` path —
  byte-faithful at the boundary we own (injecting invalid-UTF-8 byte
  sequences is out of scope; the session API is string-typed end to end).
  Capped at 32 KiB per call (`malformedRequest` beyond).
- `spawnSession { profile? }` → `{ paneId, tabId, profileName, kind }`.
  Missing/empty `profile` = the default local profile. Otherwise the name is
  resolved case-insensitively against local settings profiles first, then the
  SSH store (`profileNotFound` if neither; `profileNotAllowed` for an SSH
  profile without `AllowAgentAccess`).
- `closeSession { paneId }` → `{ closed: true }`.

### UI executor bridge (spawn/close)

`AgentHostService` never touches Avalonia. MainWindow publishes a volatile
executor object (`IAgentActionExecutor`: `Task<SpawnResult> SpawnAsync(...)`,
`Task<bool> ClosePaneAsync(Guid paneId)`) on startup and clears it on close;
its implementation marshals to the UI thread (`Dispatcher.UIThread.InvokeAsync`)
and reuses `AddTab` / the existing split-aware close path. Null executor →
`actUnavailable`. Agent-triggered closes bypass the close-confirmation dialog
(a modal question the agent cannot answer); the act opt-in plus the journal
entry is the consent surface — called out in the threat model.

`sendInput` needs no executor: it goes through the registration's published
session, which is thread-safe by contract.

### Activity journal

`AgentActivityJournal` (App/AgentHost): thread-safe bounded ring (last 200
entries) of `{ timestampUtc, method, paneId?, target, outcome }` where outcome
is `ok` or the error code — **denied attempts are journaled too**. The
endpoint appends on every acting request; an `EntryAdded` event feeds the UI.
PR3 adds the visible surface (menu-accessible journal window listing entries,
newest first, with a live indicator while entries arrive). The journal is
in-memory: it is a visibility surface, not an audit log — stated in the
threat model.

## Alternatives considered

- **Reusing the observe toggle for acting** — rejected outright by DIRECTION's
  permission table; observing must never quietly escalate.
- **Allowlist on local profiles too** — local shells are already fully
  reachable by any local process running as the user; the allowlist exists
  because SSH profiles reach *other* machines with the user's credentials.
  A local allowlist would be security theater; the single act toggle governs
  local sessions.
- **Blocking close on the confirm dialog** — turns every agent close into a
  25 s protocol timeout with a surprise modal; rejected in favor of
  bypass + journal.
- **Persisting the journal to disk** — an append-only file invites being
  treated as an audit log, which it is not (the agent's own MCP transcript is
  the audit trail); in-memory keeps the privacy footprint zero.

## Testing

- **Hard-fail acceptance (DIRECTION):** all three methods return `actDisabled`
  when only observe is enabled; sendInput/spawn return `profileNotAllowed` for
  non-allowlisted SSH targets; everything still works for local sessions with
  the act toggle on.
- **Byte-faithful acceptance:** handler test asserts the exact wire string
  (including `\x03` and `\r`) reaches `ITerminalSession.SendInput`;
  PtySmoke-level test proves an agent-injected command executes in a live
  shell and that a manual recording of that session contains the injected
  input event (replay-recorded like any PTY input).
- **Protocol:** unknown pane → `sessionNotFound`; exited session →
  `sessionNotRunning`; missing params → `malformedRequest`; oversized input →
  `malformedRequest`; no executor → `actUnavailable`; spawn resolves local
  before SSH on name collision.
- **Journal:** every call (allowed and denied) appends exactly one entry with
  the right outcome; ring is bounded.
- **Off-is-off:** with the act toggle off nothing about observe behavior
  changes (existing suites stay green).

## Out of scope

- Raw non-UTF-8 byte injection, per-key events, or paste-bracketing controls.
- Split-pane spawning (`spawnSession` opens tabs; panes/splits can follow).
- Persisted audit logging, per-agent identity, or rate limiting beyond the
  input size cap (the endpoint is per-user local; see threat model).
- Any network-exposed control surface (unchanged non-goal).

## Suggested PR slicing

1. **Act gate + sendInput:** contracts (method, error codes, DTOs), settings
   toggle + `ActEnabled` plumbing, `SshProfile.AllowAgentAccess` + allowlist
   probe, registration `ProfileId` + `TrySendInput`, journal core (no UI),
   `sendInput` handler, MCP `novaterminal.send_input`, acceptance + protocol +
   journal tests.
2. **spawnSession/closeSession:** executor bridge published by MainWindow,
   both handlers + MCP tools, connection-editor allowlist checkbox, tests.
3. **Journal UI + docs:** journal window, threat-model doc
   (`docs/agent-host/2026-07-12-acting-threat-model.md`), README + McpServer
   README updated (the MCP surface is no longer read-only), DIRECTION A3
   checkboxes.
