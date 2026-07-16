# Agent-host smoke test (pre-0.4.0)

Manual verification of A1–A4 on a real build before tagging 0.4.0. Layered — stop
after any layer. Each check lists the action, the expected result, and the red flag.

Paths assume the repo at its current location; adjust as needed. Config/state lives
under `%LOCALAPPDATA%\NovaTerminal` (settings.json, agent-endpoint.json, recordings\).

## 0. Build & launch

- [ ] Build everything:
  ```
  scripts/build.ps1 build NovaTerminal.sln
  ```
  Expected: 0 errors. Red flag: any error (a warning flood is normal).

- [ ] Launch the app (run the built exe directly — the wrapper doesn't do `run`):
  ```
  src\NovaTerminal.App\bin\Debug\net10.0\NovaTerminal.exe
  ```
  Expected: window opens, a shell tab is live. Red flag: crash → check
  `%LOCALAPPDATA%\NovaTerminal\logs\startup_error.txt`.

- [ ] Build the MCP server (Release) and wire it to Claude Code:
  ```
  scripts/build.ps1 build -c Release src/NovaTerminal.McpServer
  claude mcp add novaterminal -- dotnet "<REPO>\src\NovaTerminal.McpServer\bin\Release\net10.0\NovaTerminal.McpServer.dll"
  ```
  (Any MCP client works; do NOT use `dotnet run` — it corrupts stdio.) In a Claude
  Code session, confirm the `novaterminal.*` tools appear.

## 1. Off-is-off (default state)

With **all** agent toggles off (fresh settings):

- [ ] Ask the agent: run `novaterminal.list_sessions`.
  Expected: the "unavailable / enable Agent access (observe)" guidance, not data.
- [ ] Confirm there is **no** `%LOCALAPPDATA%\NovaTerminal\agent-endpoint.json` (or it is
  empty). Red flag: an endpoint file exists while observe is off.

## 2. Settings toggles persist (UI)

Settings → the Agent access rows:

- [ ] Verify three toggles exist: **Agent access (observe)**, **Agent replay
  export** (indented), **Agent access (act)** (indented). Toggle observe on, others
  off; Save.
- [ ] Reopen Settings — observe still on. Check `%LOCALAPPDATA%\NovaTerminal\settings.json`:
  `AgentAccessObserveEnabled: true`, `AgentReplayExportEnabled: false`,
  `AgentAccessActEnabled: false`. Red flag: values don't round-trip.
- [ ] With observe now on, `agent-endpoint.json` appears next to settings.json.

## 3. Observe + status (A1/A2) — observe ON only

Have a couple of tabs open; run something interactive (e.g. `vim` or a `ping -t`).

- [ ] `novaterminal.list_sessions` → lists panes with paneId, title, kind, size,
  status. Red flag: empty while tabs are open, or wrong kind.
- [ ] `novaterminal.read_screen <paneId>` → the visible grid matches what you see,
  including cursor position. Red flag: stale/garbled content, wrong wrapping.
- [ ] `novaterminal.read_scrollback <paneId>` → history lines, oldest first.
- [ ] Start a long command (`ping -n 20 localhost`), then
  `novaterminal.get_session_status <paneId>` → `running`; after it finishes →
  `idle`/`awaitingInput`. Red flag: status stuck on the wrong state.
- [ ] `novaterminal.wait_for_events` with the long command running → returns a
  `commandFinished` (or status) event when it completes, rather than timing out
  every call. Red flag: never delivers the completion event.

## 4. Replay export (A4) — enable "Agent replay export"

- [ ] With observe + replay-export on, produce some output in a pane, then
  `novaterminal.export_replay <paneId>`.
  Expected: returns a path under `%LOCALAPPDATA%\NovaTerminal\recordings\agent-exports\`
  and an event count. Red flag: `exportDisabled` (toggle didn't apply) or a path
  that doesn't exist.
- [ ] Confirm the file exists and, opening it, that it contains `data`/`resize`
  lines but **no `input` lines** (privacy). Red flag: any `"type":"input"`.
- [ ] Replay it headlessly through the CLI:
  ```
  dotnet src\NovaTerminal.Cli\bin\Debug\net10.0\NovaTerminal.Cli.dll --replay "<exported .rec path>"
  ```
  Expected: prints the final screen; exit code 0. Red flag: crash, or an empty
  screen for a session that clearly had output.
- [ ] Turn "Agent replay export" **off**, retry `export_replay` → `exportDisabled`.

## 5. Act gating (A3) — the security-critical checks

**5a. Act still OFF (observe on):**
- [ ] `novaterminal.send_input` (any paneId, text `"echo hi\r"`) → **`actDisabled`**;
  nothing is typed into the terminal. Red flag: the text actually runs.
- [ ] `novaterminal.spawn_session` and `novaterminal.close_session` likewise →
  `actDisabled`.

**5b. Turn "Agent access (act)" ON:**
- [ ] `send_input <local paneId>` `"echo agent-was-here\r"` → the command runs in
  that pane; result reports bytes sent. Red flag: nothing typed, or wrong pane.
- [ ] `spawn_session` (no profile) → a new tab opens; returns a paneId. `spawn_session`
  with a **local profile name** → tab for that profile.
- [ ] `close_session <paneId>` → that pane/tab closes **without** a confirmation
  dialog. Red flag: a modal blocks it, or it reports success but the pane stays.

**5c. SSH allowlist (no live host needed):**
- [ ] Create an SSH connection profile (host can be anything). Leave "Allow AI agent
  access to this connection" (Advanced tab) **unchecked**.
- [ ] `spawn_session "<that SSH profile name>"` → **`profileNotAllowed`**; no tab
  opens. Red flag: it opens/connects anyway.
- [ ] Check the box, Save, retry → the spawn is permitted (the tab opens even if the
  host is unreachable; a connect error in the pane is fine — the gate passed).
- [ ] Uncheck it again, Save, edit any other field, Save, reopen → the box is still
  unchecked (revoke sticks; the allowlist round-trips).

## 6. Activity journal (A3) — "nothing is silent"

- [ ] New-tab menu (`+`) → **Agent Activity…** opens a window.
- [ ] It lists the recent actions from steps 5a/5b — including the **denied**
  `actDisabled` / `profileNotAllowed` attempts, newest first, each with method,
  outcome, and target. Red flag: denied attempts missing, or the window empty after
  you've acted.
- [ ] Refresh picks up new entries after another `send_input`.

## 7. Revocation

- [ ] Turn **Agent access (observe)** off (Save). `agent-endpoint.json` is emptied,
  and any `novaterminal.*` tool now returns the unavailable guidance — including the
  act tools (act rides on observe being up). Red flag: tools still work after observe
  is off.

---

### Notes
- If a tool returns "could not be parsed", the app and MCP server are from different
  builds — rebuild both.
- The CLI (`NovaTerminal.Cli`) is present in a local build but is **not** in the
  release zip today; the 0.4.0 release must add it for step 4's `--replay` to work
  for end users (tracked as the pre-release bundle fix).
- These are the surfaces verified by build/logic tests but not visually in this
  cycle: the three toggles, the SSH allowlist checkbox, and the Agent Activity
  window. Steps 2, 5c, and 6 are the highest-value manual confirmations.
