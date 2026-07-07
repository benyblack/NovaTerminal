# Agent Host A1 (Observe) Design

Milestone **A1** of `docs/agent-host/DIRECTION.md`: agents can list live
terminal sessions and read their screen/scrollback through the MCP server.
Observe-only, off by default.

## Goal

Expose three session-facing MCP tools backed by the running app:

- `novaterminal.list_sessions`
- `novaterminal.read_screen`
- `novaterminal.read_scrollback`

The change must:

- reuse the deterministic snapshot path (`BufferSnapshot`), not invent a new
  read path
- be a strict no-op when the `Agent Access (observe)` setting is off
- add no new dependencies to `NovaTerminal.VT` (leaf) and none from
  `NovaTerminal.Pty` to `NovaTerminal.VT`
- keep the stdio MCP server as the only agent-facing surface

## Current State

- `src/NovaTerminal.McpServer` is a standalone stdio process
  (`Program.cs`: `Host.CreateApplicationBuilder` → `AddMcpServer()`
  `.WithStdioServerTransport().WithToolsFromAssembly()`, singleton
  `RepoContext`). It is repo-facing only; it has no connection to a running
  app.
- There is **no IPC of any kind** in `src/` today (no named pipes, unix
  sockets, or single-instance activation). The control channel is new code.
- The live session registry is **MainWindow-local**: `_tabIds`,
  `_activePaneByTab`, `_paneOwnerTab`, `_layoutModelByTab` dictionaries in
  `src/NovaTerminal.App/MainWindow.axaml.cs`. `TerminalPane`
  (`Controls/TerminalPane.axaml.cs`) owns `PaneId: Guid` and its
  `ITerminalSession`. There is no service-level registry to query.
- Deterministic snapshots exist:
  `NovaTerminal.Replay.BufferSnapshot.Capture(TerminalBuffer, bool includeAttributes)`
  (`src/NovaTerminal.Replay/Replay/BufferSnapshot.cs`) produces `Lines` +
  optional `AttributeLines`; `ReplaySnapshot`
  (`src/NovaTerminal.VT/ReplayModels.cs`) is the lower-level parity format.
- Buffer reads are guarded by `TerminalBuffer.Lock`
  (`ReaderWriterLockSlim`, `NoRecursion`) with
  `EnterReadLockIfNeeded`/`ExitReadLockIfNeeded` helpers
  (`TerminalBuffer.ThreadingAndInvalidation.cs`).
- Settings: `TerminalSettings` (`src/NovaTerminal.App/Shell/TerminalSettings.cs`)
  already models opt-in experimental flags (`ExperimentalNativeSshEnabled`,
  `CommandAssistEnabled` default-false) with JSON persistence via
  `AppJsonContext`.

## Recommended Approach

Three additive pieces:

1. **`NovaTerminal.AgentHost.Contracts`** — new leaf class library: request/
   response DTOs and the wire protocol version. Referenced by both App and
   McpServer. Zero project references (enforced).
2. **App-side endpoint** — an `AgentHostService` in `NovaTerminal.App` that
   (a) holds a thread-safe session registry populated by existing pane
   lifecycle code, and (b) when the observe setting is on, listens on a
   per-user local endpoint and answers contracts requests using
   `BufferSnapshot.Capture` under the buffer read lock.
3. **McpServer client + tools** — a lazy IPC client and a new
   `SessionTools` class exposing the three MCP tools, with a clear error
   string when the app isn't running or Agent Access is off.

## Alternatives Considered

### 1. App hosts the MCP endpoint directly

Pros: no proxy hop, one process.
Cons: two MCP surfaces to document (repo-facing stdio + app endpoint);
agents must discover a socket instead of launching a stdio server (worse
client compatibility today); harder to keep the public surface stable while
the IPC contract iterates. **Rejected for A1**; can be revisited once the
contract is stable.

### 2. Read state out-of-band (replay files / shared memory)

Pros: no live endpoint in the app.
Cons: stale by construction, no session listing, and it turns the replay
format into an unversioned IPC contract. Rejected.

### 3. Screen-scraping via OS accessibility APIs (what tmux-layer tools do)

Rejected outright — the entire value proposition is structured, correct,
deterministic reads from the buffer itself.

## Architecture

```
agent ── stdio MCP ──▶ NovaTerminal.McpServer
                          │  AgentHostClient (Contracts DTOs, JSON frames)
                          ▼
              per-user local endpoint (named pipe / unix socket)
                          ▼
                    NovaTerminal.App
                    AgentHostService ── registry ◀─ TerminalPane lifecycle
                          │
                    BufferSnapshot.Capture(buffer)  [under buffer.Lock read]
```

### Contracts (`src/NovaTerminal.AgentHost.Contracts`)

- `ProtocolVersion` (int, starts at 1; server rejects unknown majors)
- `SessionInfo { Guid PaneId, Guid TabId, string Title, string ProfileName,
  string Kind /* local | ssh */, int Rows, int Cols, bool IsActive }`
- `ScreenSnapshotDto { string[] Lines, string[]? AttributeLines,
  int CursorRow, int CursorCol, bool CursorVisible, int Rows, int Cols }`
  — a 1:1 projection of `BufferSnapshot` output plus cursor state
- `ScrollbackRequest { Guid PaneId, int StartLine, int MaxLines }` (ranged;
  server caps `MaxLines`, e.g. 2000/request)
- Wire format: newline-delimited JSON frames
  `{ "v": 1, "id": n, "method": "...", "params": {...} }` /
  `{ "v": 1, "id": n, "result": {...} | "error": {...} }`. Deliberately not
  MCP or JSON-RPC-the-library — small, versioned, source-generated JSON
  (AOT-safe, matching the app's `JsonSerializerContext` usage).

### App side (`NovaTerminal.App`)

- `AgentHost/AgentSessionRegistry` — `ConcurrentDictionary<Guid, ...>` of
  pane registrations `{ PaneId, TabId, title accessor, TerminalBuffer,
  profile metadata }`. `MainWindow`/`TerminalPane` register on pane
  creation and unregister on close — the only touch points in existing code,
  mirroring how `_paneOwnerTab` is maintained today.
- `AgentHost/AgentHostService` — background listener started only when
  `TerminalSettings.AgentAccessObserveEnabled` is true (new bool, default
  `false`, same pattern as `ExperimentalNativeSshEnabled`; added to the
  McpServer `SettingsTools` known-key lists). Toggling the setting starts/
  stops the listener without restart.
- Endpoint: `NamedPipeServerStream` on Windows
  (`novaterminal-agent-<user-sid>`, `CurrentUserOnly`);
  `UnixDomainSocketEndPoint` on Linux/macOS at
  `<AppPaths runtime dir>/agent.sock`, `0600`, unlinked on exit. An
  `agent-endpoint.json` discovery file next to `settings.json` records
  `{ version, endpoint, pid }`; first app instance wins, stale files (dead
  pid) are replaced.
- Reads: resolve pane → `EnterReadLockIfNeeded(buffer.Lock)` →
  `BufferSnapshot.Capture(buffer, includeAttributes)` + cursor via existing
  accessors → release. Never blocks on the UI thread; requests are served on
  a worker with a per-request timeout so a wedged client cannot hold the
  read lock indefinitely (read is taken only for the capture itself).

### McpServer side

- `AgentHostClient` (singleton, DI alongside `RepoContext`): reads the
  discovery file, connects lazily per request batch, reconnects on failure.
- `Tools/SessionTools.cs`:
  - `novaterminal.list_sessions` — table of `SessionInfo`
  - `novaterminal.read_screen` — visible grid as text, cursor position,
    optional attributes (`includeAttributes` param)
  - `novaterminal.read_scrollback` — ranged, capped
- Unavailability is a *normal result*, not an exception: a fixed message
  explaining that NovaTerminal isn't running or `Agent Access (observe)` is
  disabled, and where to enable it. Repo-facing tools are unaffected.

### Settings UI

One switch in `SettingsWindow`: **Agent Access — allow AI agents to read
terminal sessions (observe only)**, with sub-text noting that screen content
may include sensitive output and that acting permissions do not exist yet.

## Security Notes

- Endpoint is per-user and local-only: pipe ACL `CurrentUserOnly` /
  socket file mode `0600`. No TCP listener anywhere.
- Observe-only by design: the protocol has no input/spawn methods in v1, so
  a compromised client gains nothing beyond reads (A3 adds acting behind a
  separate opt-in and its own threat-model doc).
- Screen reads can contain secrets the user has on screen; this is why the
  toggle is off by default and clearly worded. No content filtering is
  attempted (out of scope, documented).

## Testing

- **Parity (the headline invariant):** replay a recorded fixture through the
  core runner and through a live headless session; `read_screen` over IPC
  must equal `BufferSnapshot.Capture` of the replayed buffer byte-for-byte,
  on all three OSes. Add to the replay lane.
- **Off-is-off:** with `AgentAccessObserveEnabled=false` (default), no
  listener socket/pipe exists (probe fails) and no discovery file is
  written; MCP tools return the unavailable message. Headless integration
  test.
- **Architecture:** new `LayeringTests` fact — `AgentHost.Contracts` has no
  dependency on App/VT/Avalonia; new `ProjectFileLayeringTests` fact — its
  csproj has zero `ProjectReference`s; existing invariants (VT leaf, Pty ¬→
  VT, no prod → test refs) must stay green.
- **Concurrency:** stress test — continuous `read_screen` while a replay
  floods the buffer with output; no deadlock (NoRecursion lock policy),
  no torn snapshots (each capture is internally consistent), renderer
  metrics lane stays within its ceilings.
- **Protocol:** version-mismatch and malformed-frame rejection unit tests in
  a new `tests/NovaTerminal.AgentHost.Contracts.Tests` or folded into
  `McpServer.Tests`.

## Out of Scope (later milestones)

- Session status model and notifications (A2)
- `send_input` / `spawn_session` / permissions beyond the observe toggle (A3)
- Replay export tools (A4)
- Multiple concurrent app instances beyond first-wins discovery

## Suggested PR Slicing

1. Contracts library + architecture-test assertions (no behavior)
2. `AgentSessionRegistry` + pane lifecycle registration (no endpoint yet;
   covered by unit tests)
3. `AgentHostService` endpoint + settings flag + settings UI
4. McpServer `AgentHostClient` + `SessionTools` + docs
   (`README`, `docs/agent-host/`), demo recording

Each step keeps CI green and is independently revertable.
