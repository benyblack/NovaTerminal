# Ntilde MCP — tools (v0.4)

The server exposes **two tool families**:

- **Repo / dev-companion tools** — read-only and offline: no command execution, no network, no
  credentials; filesystem access is confined to `docs/`. Available whenever the server runs.
- **Live-session tools (agent host)** — proxy the *running* Ntilde app over a per-user local
  IPC endpoint and are gated by explicit, **default-off** user opt-ins (see
  [security.md](security.md) and the
  [acting threat model](../agent-host/2026-07-12-acting-threat-model.md)). With the opt-ins off,
  no live endpoint exists and these tools return guidance instead of data.

## Repo / dev-companion tools (read-only, offline)

| Tool | Inputs | Description |
|------|--------|-------------|
| `ntilde.get_project_summary` | — | High-level summary: what Ntilde is, tech stack, assembly/module layout, key conventions. Orient here first. |
| `ntilde.get_architecture_map` | — | The authoritative module-ownership map (`docs/MODULE_OWNERSHIP.md`): per-assembly namespaces, dependencies, owned responsibilities, enforced invariants. |
| `ntilde.list_docs` | — | Lists Markdown docs under `docs/` (paths relative to `docs/`). |
| `ntilde.read_doc` | `path` | Reads a doc by its path relative to `docs/`. Reads are confined to `docs/`; paths outside it (e.g. `../secret`) are rejected. |
| `ntilde.get_vt_conformance_summary` | — | VT/ANSI conformance status and known terminal gaps, gathered from the repo's coverage/gap matrices. |
| `ntilde.explain_escape_sequence` | `sequence` | Explains a VT/ANSI escape sequence (e.g. `ESC[2J`, `CSI ?25h`, `OSC 8`, `ESC c`) — name and what it does in Ntilde. |
| `ntilde.generate_vt_test_plan` | `feature` | Generates a structured VT/ANSI test plan (cases, where tests live, verification) for a parser/rendering feature. |
| `ntilde.get_theme_schema` | — | The theme JSON schema: required fields, accepted color formats, and an example. |
| `ntilde.validate_theme_json` | `themeJson` | Validates a theme JSON string against the schema; reports missing fields, invalid colors, and unknown fields. |
| `ntilde.get_connection_profile_schema` | — | The SSH connection-profile JSON schema: PascalCase fields by area, integer enum mappings, defaults, and an example. Accepts a single profile or a full `profiles.json` document. |
| `ntilde.validate_connection_profile_json` | `profileJson` | Validates a connection-profile JSON (single profile or full document; auto-detected); reports wrong types, out-of-range integer enums/ports, missing `Name`/`Host`, and warns on unknown fields and any stray `Password`. |
| `ntilde.get_settings_schema` | — | The settings.json schema: top-level fields by area (PascalCase, integer enums for embedded profiles), types, defaults, and an example. Top-level shape only. |
| `ntilde.validate_settings_json` | `settingsJson` | Validates a settings.json string (top-level shape); reports wrong types, out-of-range numerics, malformed `DefaultProfileId`, bad collection shapes, and warns on unknown fields and any stray `Password`. Embedded profiles are not deep-validated. |
| `ntilde.generate_codex_prompt_for_issue` | `title`, `description?` | Generates a structured implementation prompt (relevant areas, constraints, PR size, steps, tests, acceptance, risks) tailored to Ntilde conventions. |
| `ntilde.suggest_relevant_files` | `topic` | Suggests the concrete source/test files most relevant to a topic/task (e.g. `reflow`, `glyph atlas`, `ssh key auth`). |
| `ntilde.backup_export` | `destinationPath`, `rootDirectory?` | Exports configuration (settings, themes, connections, workspaces, policy, snippets — never passwords) to a `.ntildebackup` file at `destinationPath` (must be absolute; a relative path is rejected rather than resolved against the server's working directory). Reads/writes the app data root (`rootDirectory`, default the current user's Ntilde directory) rather than `docs/`. |
| `ntilde.backup_list` | `rootDirectory?` | Lists automatic configuration snapshots, newest first, with id, reason, timestamp, and size. |

Deliberately absent: `backup_import` / `backup_restore`. Both would replace the user's live
configuration, and an out-of-process agent doing that silently is a destructive action the user
never sees — so import and restore stay confined to the in-app Settings page and the `backup` CLI
verb, where the user is present. `backup_export` and `backup_list` are read-only and carry no such
risk.

## Live-session tools (agent host)

These require Ntilde to be **running** with the relevant opt-in enabled. Get a `paneId` from
`list_sessions`.

### Observe — requires **Agent access (observe)** (read-only)

| Tool | Inputs | Description |
|------|--------|-------------|
| `ntilde.list_sessions` | — | Lists live sessions: `paneId`, title, profile, kind (local/ssh), size, active flag, and status. |
| `ntilde.read_screen` | `paneId`, `includeAttributes?` | The visible screen as deterministic text (viewport lines, cursor position/visibility, size); optional per-row attribute encodings. |
| `ntilde.read_scrollback` | `paneId`, `startLine?`, `maxLines?` | Scrollback history lines, oldest first. `startLine` (default 0 = oldest retained line) and `maxLines` (default 200, server-capped) page through the history. |
| `ntilde.get_session_status` | `paneId` | What the session is doing now — running / awaitingInput / idle / exited — with a confidence tier (precise = shell-integration events; heuristic = PTY signals), in-flight command, and exit code when known. |
| `ntilde.wait_for_events` | `sinceSeq?`, `timeoutMs?` | Long-polls the per-session event ring for status/command events after a cursor, so an agent can await completion instead of polling. |
| `ntilde.export_replay` | `paneId` | Exports the session's recent output + resizes as a deterministic `.rec` file (replay with `Ntilde --replay <file>`). **Never records input.** Requires the additional **Agent replay export** sub-toggle. |
| `ntilde.capture_screen` | `paneId`, `inline?`, `maxWidth?`, `scale?`, `mode?` | Captures the pane as a PNG and returns its path (plus the image itself when `inline=true`). `mode=render` (default) re-renders offscreen from the buffer, so a minimized or occluded pane captures identically, no other window can leak in, and the same screen yields the same bytes; `mode=live` photographs the on-screen control instead, which carries the background image and window opacity but needs the pane visible and is not reproducible. `scale` (1–3) renders more device pixels per point — a 1x capture of an 80×24 pane is only ~640×384; `maxWidth` shrinks the result after the fact. Needs no permission beyond observe; the pane's agent indicator lights on a capture. |

### Act — requires the separate **Agent access (act)** opt-in *on top of* observe

Every acting call — allowed or denied — is recorded in the in-app **activity journal**. SSH
targets additionally require a per-profile allowlist.

| Tool | Inputs | Description |
|------|--------|-------------|
| `ntilde.send_input` | `paneId`, `text`, `submit?` | Types `text` into a session byte-for-byte (control characters allowed, e.g. `` = Ctrl-C). Set `submit=true` to append a carriage return (Enter) — a bare newline arrives as LF, which PowerShell/PSReadLine treats as a soft continuation rather than submit. |
| `ntilde.spawn_session` | `profile?` | Opens a new tab running the default local profile, a named local profile, or an *allowlisted* SSH profile; returns the new `paneId`. |
| `ntilde.close_session` | `paneId` | Closes a live pane (no confirmation dialog — the act opt-in plus the journal entry is the consent surface). |

## Notes

- `get_architecture_map`, `list_docs`, `read_doc`, and `get_vt_conformance_summary` read files from
  the repository; they need the repo root (auto-detected, or set `NTILDE_REPO_ROOT`).
- `get_project_summary`, `get_theme_schema`, `validate_theme_json`, and
  `generate_codex_prompt_for_issue` are fully self-contained (no filesystem access).
- `validate_settings_json` validates the top-level settings shape and the structure of its
  collections; it does not deep-validate embedded `Profiles`/`TabTemplateRules` entries.
- `capture_screen` renders through the same `TerminalSnapshotRenderer` the golden-PNG render tests
  pin down, always at 1:1 (never the monitor's DPI scaling) and with no blink phase, selection, or
  render HUD, so two captures of an unchanged buffer are byte-identical on a given machine. The
  image is the pane's grid only — no window chrome, no other panes.
- The live-session tools reach the app only through the zero-reference
  `Ntilde.AgentHost.Contracts` wire types over a per-user local IPC endpoint — the server
  still links no terminal, PTY, SSH, or rendering code.
- `backup_export` and `backup_list` reach the zero-reference `Ntilde.Backup` leaf directly
  (no IPC, no opt-in toggle — they only ever read/export, never import or restore) and never touch
  secret storage; a bundle carries connection profiles without password material.

## Still deferred

- **`generate_test_plan_for_change`**: largely covered by `generate_codex_prompt_for_issue`
  (its "tests to update" section) and `generate_vt_test_plan`.
- **Replay frame-stepping** (`--replay --at <ms>`): `export_replay` + the headless renderer cover
  final-screen text today; stepping frame by frame is future work. Still images of a *live* pane are
  covered by `capture_screen`; PNG output from a *replay* file would reuse the same
  `TerminalSnapshotRenderer`.
