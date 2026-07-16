# NovaTerminal MCP server

A local, stdio [MCP](https://modelcontextprotocol.io) server (`src/NovaTerminal.McpServer`) that
exposes NovaTerminal to AI coding agents (Claude Code, Claude Desktop, VS Code, …). It began as a
read-only "dev companion" and now also fronts the **agent host** — opt-in access to live terminal
sessions. Two tool families:

1. **Repo / dev-companion tools** — read-only and offline: project knowledge (architecture,
   schemas, VT conformance, dev workflow). A developer-productivity aid; always available.
2. **Live-session tools (agent host)** — observe, and behind a separate opt-in, act on the running
   app's terminal sessions. A user-facing feature, **off by default**.

## Repo / dev-companion tools

- **Read-only.** Read repository docs and validate theme / SSH-profile / settings JSON against schemas.
- **Offline and sandboxed.** No command execution, no SSH/network, no credentials, no live-session
  access; filesystem reads are confined to `docs/`.

## Live-session tools (agent host)

Proxy the **running** NovaTerminal app over a per-user local IPC endpoint, gated by explicit,
default-off opt-ins in the app's settings:

- **Observe** (opt-in) — read live sessions: `list_sessions`, `read_screen`, `read_scrollback`,
  `get_session_status`, `wait_for_events`, `export_replay`.
- **Act** (a *separate* opt-in, on top of observe) — `send_input`, `spawn_session`,
  `close_session`. SSH targets require a per-profile allowlist, and every acting call — allowed or
  denied — is recorded in an in-app activity journal.

With both toggles off there is no live endpoint at all. See [mcp/security.md](mcp/security.md) and
the [acting threat model](agent-host/2026-07-12-acting-threat-model.md).

## Tools

See [mcp/tools.md](mcp/tools.md) for the authoritative list of both families.

## How to run it locally

The server speaks MCP over **stdio**, so it is normally launched by an MCP client (not run by
hand). The dev-companion tools locate the repository automatically by walking up to
`NovaTerminal.sln`; you can override that with the `NOVATERMINAL_REPO_ROOT` environment variable.
The live-session tools need the NovaTerminal app running with the opt-ins enabled.

Build once, then run the built DLL (clients should point at the DLL, **not** `dotnet run` —
`run` emits build/restore output to stdout, which corrupts the JSON-RPC stream):

```bash
# from the repo root
dotnet build -c Release src/NovaTerminal.McpServer
dotnet src/NovaTerminal.McpServer/bin/Release/net10.0/NovaTerminal.McpServer.dll
```

### Client configuration

- Claude Code: `claude mcp add novaterminal -- dotnet "<path-to-repo>/src/NovaTerminal.McpServer/bin/Release/net10.0/NovaTerminal.McpServer.dll"`
- Claude Desktop: [examples/mcp/claude_desktop_config.json](../examples/mcp/claude_desktop_config.json)
- VS Code: [examples/mcp/vscode_mcp_config.json](../examples/mcp/vscode_mcp_config.json)

Point `NOVATERMINAL_REPO_ROOT` at your clone so the doc-reading tools resolve. To use the
live-session tools, enable **Settings → Agent access (observe)** in NovaTerminal (and the
**Agent access (act)** sub-toggle to allow typing/spawning/closing).

## Status

Both the read-only dev companion and the agent-host observe/act surface (milestones A1–A4) ship as
of 0.4.0. Remaining follow-ups are tracked in
[agent-host/known-limitations.md](agent-host/known-limitations.md) and
[agent-host/DIRECTION.md](agent-host/DIRECTION.md).
