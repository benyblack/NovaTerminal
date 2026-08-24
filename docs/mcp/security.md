# NovaTerminal MCP — security model

The server has **two tool families** with different security postures.

## Repo / dev-companion tools — local-only, read-only, offline

These are a development aid and must never become an exfiltration or command-execution surface:

- **No command execution.** The server contains no process-spawning code paths for these tools.
- **No SSH / network.** They open no sockets and require no network access.
- **No credentials / private keys.** They never read the vault, profiles, `known_hosts`, or keys.
- **Read-only filesystem access, confined to `docs/`.** Reads go through `RepoContext`, which:
  - resolves a single repo root (env `NOVATERMINAL_REPO_ROOT`, or by walking up to `NovaTerminal.sln`);
  - serves only files under `docs/`;
  - canonicalizes every requested path and **rejects anything that escapes `docs/`** (`../`,
    absolute paths). Covered by `RepoContextTests`.

## Live-session tools (agent host) — explicit, default-off opt-ins

The observe/act tools proxy the **running** NovaTerminal app over a **per-user local IPC endpoint**
(a `CurrentUserOnly` named pipe on Windows; a `0600` unix-domain socket under the app-data dir on
macOS/Linux). They are gated by opt-ins in the app's settings:

- **Observe** (`list_sessions`, `read_screen`, `read_scrollback`, `get_session_status`,
  `wait_for_events`, `export_replay`, `capture_screen`) requires **Agent access (observe)**.
  Read-only. Replay export additionally requires the **Agent replay export** sub-toggle and **never
  records typed input**. Screenshots additionally require the **Agent screenshots** sub-toggle: a
  rendered image discloses strictly more than the text `read_screen` returns — inline images
  (sixel), the theme, everything drawn on the grid — so it is its own decision rather than riding
  the observe toggle, and every capture (allowed or denied) is recorded in the **activity journal**
  alongside the acting calls. Captures render the pane's grid offscreen from its buffer: they are
  not screen grabs, so no other window, pane, or piece of app chrome can appear in one.
- **Act** (`send_input`, `spawn_session`, `close_session`) requires a **separate** **Agent access
  (act)** opt-in *on top of* observe. SSH targets additionally require a **per-profile allowlist**.
  Every acting call — allowed or denied — is recorded in an in-app **activity journal**.
- **Visibility while it happens.** Panes an agent may act on carry an agent segment in
  their status bar; the segment turns blue and reads "agent reading" on any agent read
  (readScreen, readScrollback, getSessionStatus, captureScreen, exportReplay — an export
  writes out the pane's retained flight recording, more of its output than a single
  `read_screen`, so it counts as a read of that pane), and turns
  amber and reads "agent typed" when an agent writes into that pane (sendInput,
  spawnSession, closeSession).
  The amber state persists for at least 10 seconds even if you look at the pane immediately
  — so an agent typing into a pane you are already watching cannot flash past you — and
  then clears. Clicking the segment opens the activity journal. Tab headers carry a
  keyboard badge for writes (eye badge for reads too, if
  **Settings → Agent Access → Tab indicator** is set to `All`), and a window-level light
  shows whenever agent access is enabled, brightening while an agent holds a `waitForEvents`
  long poll open or while a pane that carries no segment of its own is being read. That
  second condition is what covers the panes you deliberately left off the act allowlist:
  they show no status-bar segment, and under the default `WritesOnly` tab setting no badge
  either, so without it a read of exactly those panes would be visible nowhere.
  The activity journal remains the retrospective
  record; these are the live ones. There is no way to silence them — the way to have no
  indicator is to turn agent access off.
- With both toggles off, **no endpoint exists** and the live-session tools return guidance, not data.

Full analysis of the acting surface: the
[acting threat model](../agent-host/2026-07-12-acting-threat-model.md).

## Architectural enforcement

- `NovaTerminal.McpServer`'s only cross-assembly dependency is the zero-reference
  `NovaTerminal.AgentHost.Contracts` leaf (the IPC wire types). It links **no** terminal, PTY, SSH,
  or rendering code — the live-session tools reach the app purely over that IPC contract, never by
  calling into it in-process.
- Otherwise it depends only on `ModelContextProtocol` and `Microsoft.Extensions.Hosting`.
- Dev-companion schemas that mirror real types are kept honest by drift-guard tests in
  `tests/NovaTerminal.McpServer.Tests`.
