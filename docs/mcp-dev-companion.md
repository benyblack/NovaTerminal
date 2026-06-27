# NovaTerminal MCP Dev Companion

An **experimental, local, read-only** [MCP](https://modelcontextprotocol.io) server that exposes
NovaTerminal's project knowledge (architecture, schemas, VT conformance, dev workflow) to AI coding
agents in a structured way. It lives in `src/NovaTerminal.McpServer`.

This is a **developer productivity tool**, not a user-facing terminal feature.

## What it is

- A stdio MCP server that AI agents (Claude Desktop, VS Code, etc.) can connect to.
- Read-only: it reads repository docs and validates JSON against schemas.
- Self-contained: it has **no project references** to the rest of the solution and pulls in no
  terminal, SSH, or rendering code.

## What it is NOT (v0.1 non-goals)

It does **not**, and must not:

- execute shell commands
- open SSH connections
- read live terminal buffers
- access saved credentials or private keys
- modify user profiles
- control the running NovaTerminal app
- require network access

See [mcp/security.md](mcp/security.md) for the full security model.

## Tools

See [mcp/tools.md](mcp/tools.md). v0.1 exposes:

`novaterminal.get_project_summary`, `novaterminal.get_architecture_map`,
`novaterminal.list_docs`, `novaterminal.read_doc`,
`novaterminal.get_vt_conformance_summary`,
`novaterminal.get_theme_schema`, `novaterminal.validate_theme_json`,
`novaterminal.generate_codex_prompt_for_issue`.

## How to run it locally

The server speaks MCP over **stdio**, so it is normally launched by an MCP client (not run by
hand). It locates the repository automatically by walking up to `NovaTerminal.sln`; you can
override that with the `NOVATERMINAL_REPO_ROOT` environment variable.

To sanity-check it manually:

```bash
# from the repo root
dotnet run --project src/NovaTerminal.McpServer
# then type/pipe newline-delimited JSON-RPC (initialize, notifications/initialized, tools/list)
```

### Client configuration

- Claude Desktop: [examples/mcp/claude_desktop_config.json](../examples/mcp/claude_desktop_config.json)
- VS Code: [examples/mcp/vscode_mcp_config.json](../examples/mcp/vscode_mcp_config.json)

Point `NOVATERMINAL_REPO_ROOT` at your clone so the doc-reading tools resolve.

## Status / roadmap

v0.1 is the read-only dev companion. Future phases (explicitly **out of scope** here) could add
more VT/profile helpers and, only behind an opt-in bridge, read-only views of a running app. Any
write/action capability must be opt-in and documented separately.
