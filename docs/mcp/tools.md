# NovaTerminal MCP Dev Companion — tools (v0.1)

All tools are **read-only**. None execute commands, touch credentials, or access live sessions.

| Tool | Inputs | Description |
|------|--------|-------------|
| `novaterminal.get_project_summary` | — | High-level summary: what NovaTerminal is, tech stack, assembly/module layout, key conventions. Orient here first. |
| `novaterminal.get_architecture_map` | — | The authoritative module-ownership map (`docs/MODULE_OWNERSHIP.md`): per-assembly namespaces, dependencies, owned responsibilities, enforced invariants. |
| `novaterminal.list_docs` | — | Lists Markdown docs under `docs/` (paths relative to `docs/`). |
| `novaterminal.read_doc` | `path` | Reads a doc by its path relative to `docs/`. Reads are confined to `docs/`; paths outside it (e.g. `../secret`) are rejected. |
| `novaterminal.get_vt_conformance_summary` | — | VT/ANSI conformance status and known terminal gaps, gathered from the repo's coverage/gap matrices. |
| `novaterminal.get_theme_schema` | — | The theme JSON schema: required fields, accepted color formats, and an example. |
| `novaterminal.validate_theme_json` | `themeJson` | Validates a theme JSON string against the schema; reports missing fields, invalid colors, and unknown fields. |
| `novaterminal.generate_codex_prompt_for_issue` | `title`, `description?` | Generates a structured implementation prompt (relevant areas, constraints, PR size, steps, tests, acceptance, risks) tailored to NovaTerminal conventions. |

## Notes

- `get_architecture_map`, `list_docs`, `read_doc`, and `get_vt_conformance_summary` read files from
  the repository; they need the repo root (auto-detected, or set `NOVATERMINAL_REPO_ROOT`).
- `get_project_summary`, `get_theme_schema`, `validate_theme_json`, and
  `generate_codex_prompt_for_issue` are fully self-contained (no filesystem access).

## Tools intentionally deferred past v0.1

`explain_escape_sequence`, `generate_vt_test_plan`, connection-profile schema/validation,
`suggest_relevant_files`, `generate_test_plan_for_change`, and any "running app" bridge tools
(`list_open_tabs`, `get_selected_text`, …). The latter are sensitive and must remain opt-in.
