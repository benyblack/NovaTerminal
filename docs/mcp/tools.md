# NovaTerminal MCP — tools (v0.4)

The server exposes **two tool families**:

- **Repo / dev-companion tools** — read-only and offline: no command execution, no network, no
  credentials; filesystem access is confined to `docs/`. Available whenever the server runs.
- **Live-session tools (agent host)** — proxy the *running* NovaTerminal app over a per-user local
  IPC endpoint and are gated by explicit, **default-off** user opt-ins (see
  [security.md](security.md) and the
  [acting threat model](../agent-host/2026-07-12-acting-threat-model.md)). With the opt-ins off,
  no live endpoint exists and these tools return guidance instead of data.

## Repo / dev-companion tools (read-only, offline)

| Tool | Inputs | Description |
|------|--------|-------------|
| `novaterminal.get_project_summary` | — | High-level summary: what NovaTerminal is, tech stack, assembly/module layout, key conventions. Orient here first. |
| `novaterminal.get_architecture_map` | — | The authoritative module-ownership map (`docs/MODULE_OWNERSHIP.md`): per-assembly namespaces, dependencies, owned responsibilities, enforced invariants. |
| `novaterminal.list_docs` | — | Lists Markdown docs under `docs/` (paths relative to `docs/`). |
| `novaterminal.read_doc` | `path` | Reads a doc by its path relative to `docs/`. Reads are confined to `docs/`; paths outside it (e.g. `../secret`) are rejected. |
| `novaterminal.get_vt_conformance_summary` | — | VT/ANSI conformance status and known terminal gaps, gathered from the repo's coverage/gap matrices. |
| `novaterminal.explain_escape_sequence` | `sequence` | Explains a VT/ANSI escape sequence (e.g. `ESC[2J`, `CSI ?25h`, `OSC 8`, `ESC c`) — name and what it does in NovaTerminal. |
| `novaterminal.generate_vt_test_plan` | `feature` | Generates a structured VT/ANSI test plan (cases, where tests live, verification) for a parser/rendering feature. |
| `novaterminal.get_theme_schema` | — | The theme JSON schema: required fields, accepted color formats, and an example. |
| `novaterminal.validate_theme_json` | `themeJson` | Validates a theme JSON string against the schema; reports missing fields, invalid colors, and unknown fields. |
| `novaterminal.get_connection_profile_schema` | — | The SSH connection-profile JSON schema: PascalCase fields by area, integer enum mappings, defaults, and an example. Accepts a single profile or a full `profiles.json` document. |
| `novaterminal.validate_connection_profile_json` | `profileJson` | Validates a connection-profile JSON (single profile or full document; auto-detected); reports wrong types, out-of-range integer enums/ports, missing `Name`/`Host`, and warns on unknown fields and any stray `Password`. |
| `novaterminal.get_settings_schema` | — | The settings.json schema: top-level fields by area (PascalCase, integer enums for embedded profiles), types, defaults, and an example. Top-level shape only. |
| `novaterminal.validate_settings_json` | `settingsJson` | Validates a settings.json string (top-level shape); reports wrong types, out-of-range numerics, malformed `DefaultProfileId`, bad collection shapes, and warns on unknown fields and any stray `Password`. Embedded profiles are not deep-validated. |
| `novaterminal.generate_codex_prompt_for_issue` | `title`, `description?` | Generates a structured implementation prompt (relevant areas, constraints, PR size, steps, tests, acceptance, risks) tailored to NovaTerminal conventions. |
| `novaterminal.suggest_relevant_files` | `topic` | Suggests the concrete source/test files most relevant to a topic/task (e.g. `reflow`, `glyph atlas`, `ssh key auth`). |

## Live-session tools (agent host)

These require NovaTerminal to be **running** with the relevant opt-in enabled. Get a `paneId` from
`list_sessions`.

### Observe — requires **Agent access (observe)** (read-only)

| Tool | Inputs | Description |
|------|--------|-------------|
| `novaterminal.list_sessions` | — | Lists live sessions: `paneId`, title, profile, kind (local/ssh), size, active flag, and status. |
| `novaterminal.read_screen` | `paneId`, `includeAttributes?` | The visible screen as deterministic text (viewport lines, cursor position/visibility, size); optional per-row attribute encodings. |
| `novaterminal.read_scrollback` | `paneId` | Scrollback history lines, oldest first. |
| `novaterminal.get_session_status` | `paneId` | What the session is doing now — running / awaitingInput / idle / exited — with a confidence tier (precise = shell-integration events; heuristic = PTY signals), in-flight command, and exit code when known. |
| `novaterminal.wait_for_events` | `sinceSeq?`, `timeoutMs?` | Long-polls the per-session event ring for status/command events after a cursor, so an agent can await completion instead of polling. |
| `novaterminal.export_replay` | `paneId` | Exports the session's recent output + resizes as a deterministic `.rec` file (replay with `NovaTerminal --replay <file>`). **Never records input.** Requires the additional **Agent replay export** sub-toggle. |

### Act — requires the separate **Agent access (act)** opt-in *on top of* observe

Every acting call — allowed or denied — is recorded in the in-app **activity journal**. SSH
targets additionally require a per-profile allowlist.

| Tool | Inputs | Description |
|------|--------|-------------|
| `novaterminal.send_input` | `paneId`, `text`, `submit?` | Types `text` into a session byte-for-byte (control characters allowed, e.g. `` = Ctrl-C). Set `submit=true` to append a carriage return (Enter) — a bare newline arrives as LF, which PowerShell/PSReadLine treats as a soft continuation rather than submit. |
| `novaterminal.spawn_session` | `profile?` | Opens a new tab running the default local profile, a named local profile, or an *allowlisted* SSH profile; returns the new `paneId`. |
| `novaterminal.close_session` | `paneId` | Closes a live pane (no confirmation dialog — the act opt-in plus the journal entry is the consent surface). |

## Notes

- `get_architecture_map`, `list_docs`, `read_doc`, and `get_vt_conformance_summary` read files from
  the repository; they need the repo root (auto-detected, or set `NOVATERMINAL_REPO_ROOT`).
- `get_project_summary`, `get_theme_schema`, `validate_theme_json`, and
  `generate_codex_prompt_for_issue` are fully self-contained (no filesystem access).
- `validate_settings_json` validates the top-level settings shape and the structure of its
  collections; it does not deep-validate embedded `Profiles`/`TabTemplateRules` entries.
- The live-session tools reach the app only through the zero-reference
  `NovaTerminal.AgentHost.Contracts` wire types over a per-user local IPC endpoint — the server
  still links no terminal, PTY, SSH, or rendering code.

## Still deferred

- **`generate_test_plan_for_change`**: largely covered by `generate_codex_prompt_for_issue`
  (its "tests to update" section) and `generate_vt_test_plan`.
- **Replay frame-stepping / image output** (`--replay --at <ms>`, PNG): `export_replay` + the
  headless renderer cover final-screen text today; frame-by-frame and images are future work.
