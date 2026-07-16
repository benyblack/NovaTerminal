# NovaTerminal MCP Dev Companion — security model

The v0.1 server is **local-only, read-only, and offline**. It is a development aid; it must never
become an exfiltration or command-execution surface.

## Guarantees (v0.1)

- **No command execution.** The server contains no process-spawning code paths.
- **No SSH / network.** It opens no sockets and requires no network access.
- **No credentials / private keys.** It never reads the vault, profiles, `known_hosts`, or keys.
- **No live terminal access.** It cannot see terminal buffers, tabs, or selections.
- **Read-only filesystem access, confined to `docs/`.** File reads go through `RepoContext`, which:
  - resolves a single repo root (env `NOVATERMINAL_REPO_ROOT`, or by walking up to `NovaTerminal.sln`);
  - serves only files under `docs/`;
  - canonicalizes every requested path and **rejects anything that escapes `docs/`** (e.g. `../`,
    absolute paths). This is covered by tests (`RepoContextTests`).

## Architectural enforcement

- `NovaTerminal.McpServer` has **no `ProjectReference`s** to the rest of the solution, so it cannot
  call into terminal, PTY, SSH, or rendering code even by accident.
- It depends only on `ModelContextProtocol` and `Microsoft.Extensions.Hosting`.

## Future capabilities (must stay opt-in)

Any future tool that reads a running app's state (open tabs, active profile metadata, selected
text) or performs writes/actions is **sensitive**. Such capabilities must:

- be off by default and require explicit user opt-in,
- be documented separately with their own threat model,
- never expose credentials, private keys, or raw session contents.
