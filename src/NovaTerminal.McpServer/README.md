# NovaTerminal MCP Dev Companion

A local, **read-only** [Model Context Protocol](https://modelcontextprotocol.io) server that
exposes NovaTerminal's project knowledge to AI coding agents (Claude Desktop, Claude Code, VS
Code, etc.). It helps you explore the architecture, understand VT/ANSI behavior, and author or
validate theme and SSH connection-profile JSON — without leaving your editor.

This is a **developer tool**, not a user-facing NovaTerminal feature.

## Safety posture

The server is deliberately constrained and isolated:

- **Read-only.** It never executes shell commands, opens SSH connections, reads live terminal
  buffers, accesses saved credentials or private keys, modifies profiles, or controls the running
  app.
- **No network access.**
- **No coupling to the rest of the solution.** The project has **no `ProjectReference`** to any
  other NovaTerminal assembly — it only reads repo docs and validates JSON against self-contained
  schemas. (Where it mirrors a real type — e.g. the SSH profile schema — a drift-guard test in
  `tests/NovaTerminal.McpServer.Tests` keeps the copy honest.)
- **Filesystem access is confined to `docs/`.** Doc reads reject path traversal (`..`), absolute
  paths, and symlink escapes.

## Tech stack

- .NET 10 (`net10.0`), C#
- [`ModelContextProtocol`](https://www.nuget.org/packages/ModelContextProtocol) SDK
- `Microsoft.Extensions.Hosting`
- Transport: **stdio** (JSON-RPC over stdin/stdout)

## Build

Use the repo's build wrapper (raw `dotnet build` can hang when stdout is captured — see the root
`CLAUDE.md`):

```bash
# Windows / PowerShell
scripts/build.ps1 build -c Release src/NovaTerminal.McpServer

# Linux / macOS / Git Bash
scripts/build.sh build -c Release src/NovaTerminal.McpServer
```

This produces `src/NovaTerminal.McpServer/bin/Release/net10.0/NovaTerminal.McpServer.dll`.

## Run

Run the **built DLL** directly:

```bash
dotnet src/NovaTerminal.McpServer/bin/Release/net10.0/NovaTerminal.McpServer.dll
```

> ⚠️ **Do not use `dotnet run`.** It emits build/restore output to stdout, which corrupts the MCP
> JSON-RPC stream the client reads. Always launch the compiled DLL. (All logging in the server is
> pinned to stderr for the same reason.)

The server speaks stdio and is meant to be launched **by an MCP client**, not run interactively —
on its own it will simply wait for JSON-RPC input.

### Repository root discovery

Tools that read repo docs need to know where the repository is. The server resolves the root by:

1. The `NOVATERMINAL_REPO_ROOT` environment variable, if set; otherwise
2. Walking up from the current directory until it finds `NovaTerminal.sln`.

When a client launches the server from an arbitrary working directory, set
`NOVATERMINAL_REPO_ROOT` explicitly (see the example configs below).

## Client setup

Ready-to-edit example configs live in [`examples/mcp/`](../../examples/mcp/):

- **Claude Desktop** — [`examples/mcp/claude_desktop_config.json`](../../examples/mcp/claude_desktop_config.json)
- **VS Code** (`.vscode/mcp.json`) — [`examples/mcp/vscode_mcp_config.json`](../../examples/mcp/vscode_mcp_config.json)

Both follow the same shape: launch `dotnet` with the path to the built DLL and set
`NOVATERMINAL_REPO_ROOT`. Build the project once first, then point the config at the DLL. For
Claude Desktop, replace `/absolute/path/to/nova2` with your local clone path; the VS Code config
uses `${workspaceFolder}`.

Example (Claude Desktop):

```json
{
  "mcpServers": {
    "novaterminal-dev": {
      "command": "dotnet",
      "args": [
        "/absolute/path/to/nova2/src/NovaTerminal.McpServer/bin/Release/net10.0/NovaTerminal.McpServer.dll"
      ],
      "env": {
        "NOVATERMINAL_REPO_ROOT": "/absolute/path/to/nova2"
      }
    }
  }
}
```

After editing the config, restart the client; the `novaterminal-dev` tools should appear in its
tool list.

## Tools

All tools are read-only. See [`docs/mcp/tools.md`](../../docs/mcp/tools.md) for the authoritative
list and notes; the current set:

| Tool | Inputs | What it does |
|------|--------|-------------|
| `novaterminal.get_project_summary` | — | What NovaTerminal is, tech stack, assembly/module layout, key conventions. Start here. |
| `novaterminal.get_architecture_map` | — | The module-ownership map: per-assembly namespaces, dependencies, responsibilities, invariants. |
| `novaterminal.list_docs` | — | Lists Markdown docs under `docs/`. |
| `novaterminal.read_doc` | `path` | Reads a doc by path relative to `docs/` (traversal rejected). |
| `novaterminal.get_vt_conformance_summary` | — | VT/ANSI conformance status and known gaps. |
| `novaterminal.explain_escape_sequence` | `sequence` | Explains a VT/ANSI escape sequence (e.g. `ESC[2J`, `CSI ?25h`, `OSC 8`). |
| `novaterminal.generate_vt_test_plan` | `feature` | A structured VT test plan (cases, where tests live, verification). |
| `novaterminal.get_theme_schema` | — | Theme JSON schema: required fields, color formats, example. |
| `novaterminal.validate_theme_json` | `themeJson` | Validates a theme JSON; reports missing fields, invalid colors, unknown fields. |
| `novaterminal.get_connection_profile_schema` | — | SSH connection-profile JSON schema: PascalCase fields by area, integer enum mappings, defaults, example. Covers a single profile or a full `profiles.json` document. |
| `novaterminal.validate_connection_profile_json` | `profileJson` | Validates a connection-profile JSON (single profile or full document, auto-detected); reports wrong types, out-of-range integer enums/ports, missing `Name`/`Host`, and warns on unknown fields and any stray `Password`. |
| `novaterminal.generate_codex_prompt_for_issue` | `title`, `description?` | A structured implementation prompt (relevant areas, constraints, PR size, steps, tests, acceptance, risks). |
| `novaterminal.suggest_relevant_files` | `topic` | The concrete source/test files most relevant to a topic (e.g. `reflow`, `glyph atlas`, `ssh key auth`). |

Self-contained tools (no filesystem access): `get_project_summary`, `get_theme_schema`,
`validate_theme_json`, `get_connection_profile_schema`, `validate_connection_profile_json`,
`generate_codex_prompt_for_issue`. The rest read files under `docs/` and need the repo root.

### Example: validate a profile

Ask your agent: *"Validate this connection profile."* The agent calls
`validate_connection_profile_json` with your JSON. Note the on-disk format uses **PascalCase**
keys and **integer-valued enums** (call `get_connection_profile_schema` for the mappings).
Passwords are stored in the OS credential vault and must never appear in profile JSON — a stray
`Password` field is flagged.

## Development

Run the test project (targeted — the full solution test suite is slow):

```bash
scripts/build.ps1 test tests/NovaTerminal.McpServer.Tests
```

## Further reading

- [`docs/mcp-dev-companion.md`](../../docs/mcp-dev-companion.md) — overview, goals, and non-goals
- [`docs/mcp/tools.md`](../../docs/mcp/tools.md) — authoritative tool list
- [`docs/mcp/security.md`](../../docs/mcp/security.md) — security model
