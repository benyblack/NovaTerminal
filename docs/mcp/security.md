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
  `wait_for_events`, `export_replay`) requires **Agent access (observe)**. Read-only. Replay export
  additionally requires the **Agent replay export** sub-toggle and **never records typed input**.
- **Act** (`send_input`, `spawn_session`, `close_session`) requires a **separate** **Agent access
  (act)** opt-in *on top of* observe. SSH targets additionally require a **per-profile allowlist**.
  Every acting call — allowed or denied — is recorded in an in-app **activity journal**.
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
