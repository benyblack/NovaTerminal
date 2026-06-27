# NovaTerminal MCP Dev Companion — tools (v0.3)

All tools are **read-only**. None execute commands, touch credentials, or access live sessions.

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
| `novaterminal.generate_codex_prompt_for_issue` | `title`, `description?` | Generates a structured implementation prompt (relevant areas, constraints, PR size, steps, tests, acceptance, risks) tailored to NovaTerminal conventions. |
| `novaterminal.suggest_relevant_files` | `topic` | Suggests the concrete source/test files most relevant to a topic/task (e.g. `reflow`, `glyph atlas`, `ssh key auth`). |

## Notes

- `get_architecture_map`, `list_docs`, `read_doc`, and `get_vt_conformance_summary` read files from
  the repository; they need the repo root (auto-detected, or set `NOVATERMINAL_REPO_ROOT`).
- `get_project_summary`, `get_theme_schema`, `validate_theme_json`, and
  `generate_codex_prompt_for_issue` are fully self-contained (no filesystem access).

## Still deferred

- **`generate_test_plan_for_change`**: largely covered by `generate_codex_prompt_for_issue`
  (its "tests to update" section) and `generate_vt_test_plan`.
- **Running-app bridge tools** (`list_open_tabs`, `get_active_profile_metadata`,
  `get_selected_text`, …): sensitive; must remain opt-in and documented separately.
