# MCP Dev Companion — Connection-Profile Schema & Validation Tools (v0.3)

**Date:** 2026-06-27
**Status:** Design approved, ready for implementation plan
**Component:** `src/NovaTerminal.McpServer`

## Summary

Add two read-only MCP tools to the NovaTerminal MCP Dev Companion that let a
developer or AI client discover and validate the connection-profile JSON format:

- `novaterminal.get_connection_profile_schema`
- `novaterminal.validate_connection_profile_json`

These were previously deferred (see `docs/mcp/tools.md` → "Still deferred") on the
grounds that the profile format had "no stable documented schema" and "contains a
sensitive `Password` field." Investigation of the codebase shows both concerns are
already resolved by the existing design, so the work is lower-risk than the roadmap
note implied (see Background). This brings the server's tool count from **11 → 13**.

## Background — the two profile models

There are two profile types in the codebase, and only one is persisted:

- **`SshProfile`** (`src/NovaTerminal.Platform/Ssh/Models/SshProfile.cs`) is the model
  actually serialized to disk at `%LOCALAPPDATA%\NovaTerminal\ssh\profiles.json`. It has
  a **stable, source-generated JSON contract** (`SshJsonContext`): camelCase property
  names, enums-as-strings, `WriteIndented`, wrapped in an `SshStoreDocument` carrying a
  `schemaVersion` field (current value `1`). It has **no `Password` field** — passwords
  live in the credential vault keyed by `ProfileId` (`VaultService`).
- **`TerminalProfile`** (`src/NovaTerminal.App/Shell/TerminalProfile.cs`) is the app-layer
  model that *does* declare a `Password` property, but it is `[JsonIgnore]` and never
  serialized.

Therefore:
- A "stable documented schema" effectively already exists (the source generator).
- The "sensitive `Password` field" is already excluded from JSON.

The tools target the **`SshProfile` / `SshStoreDocument` on-disk format** — the JSON a
developer would author or inspect in `profiles.json`.

## Goals

- Let a client retrieve a human-readable description of the profile JSON format.
- Let a client validate a pasted profile JSON (single profile *or* full document) and
  receive actionable, tiered feedback.
- Keep the server's existing security posture: read-only, no `ProjectReference` to the
  rest of the solution, no process/network/credential access.

## Non-goals

- No formal JSON Schema (draft 2020-12) document — descriptive prose + an annotated
  example only, matching the existing `get_theme_schema` pattern.
- No separate hand-written schema markdown file — the tool *is* the canonical schema doc.
- No reading or writing of the user's real `profiles.json` — the validator operates only
  on JSON passed in as a string argument.
- No validation of vault/password storage — out of scope and intentionally so.

## Architectural constraint

The MCP server deliberately has **no `ProjectReference`** to the rest of the solution
(security isolation). The tools therefore **cannot import `SshProfile`**; they parse with
`System.Text.Json` (`JsonDocument` / `JsonNode`) against hand-written knowledge of the
field names, types, and enum values — exactly like the existing `validate_theme_json`.

This reintroduces a drift risk (a new `SshProfile` field would be unknown to the tool).
It is mitigated by a reflection-based **drift-guard test** (see Testing) that lives in the
*test* project, not the server.

## Tool 1 — `get_connection_profile_schema`

- **Input:** none.
- **Output:** descriptive text, fields grouped by area, each with type / enum values /
  default, followed by a complete annotated example covering both the single-profile and
  full-document shapes.

Field groups (from `SshProfile`):

- **Identity / org:** `id` (GUID), `name`, `groupPath`, `notes`, `accentColor`, `tags[]`
- **Connection:** `host`, `user`, `port` (default 22), `backendKind` (`OpenSsh` | `Native`)
- **Auth:** `authMode` (`Default` | `Agent` | `IdentityFile`), `identityFilePath`,
  `rememberPasswordInVault`
- **Jump hosts:** `jumpHops[]` → `{ host, user, port }`
- **Port forwarding:** `forwards[]` →
  `{ kind: Local | Remote | Dynamic, bindAddress, sourcePort, destinationHost, destinationPort }`
- **Multiplexing:** `muxOptions` →
  `{ enabled, controlMasterAuto, controlPath, controlPersistSeconds }`
- **Session / shell:** `serverAliveIntervalSeconds` (default 30),
  `serverAliveCountMax` (default 3), `extraSshArgs`, `workingDirectory`,
  `remoteShellKind` (`Auto` | `Bash` | `Zsh` | `Fish` | `Pwsh`)
- **Document wrapper:** `schemaVersion` (int, current = 1), `profiles[]`

## Tool 2 — `validate_connection_profile_json`

- **Input:** `profileJson` (string).
- **Output:** a `VALID` / `INVALID` report mirroring `validate_theme_json`: a header line,
  then grouped error and warning lines, each with a field path
  (e.g. `profiles[2].forwards[0].kind`).

### Auto-detection

If the parsed root object has a `profiles` array → treat as `SshStoreDocument` and
validate each entry. Otherwise → treat as a single `SshProfile`.

### Tiered validation rules

**Errors (→ `INVALID`):**

- JSON parse failure.
- Root is not a JSON object.
- Wrong JSON type for a known field (e.g. `port: "22"`, `tags` not an array).
- Unknown enum value for `backendKind`, `authMode`, `remoteShellKind`, or
  `forwards[].kind`.
- `port` or `jumpHops[].port` outside 1–65535.
- `sourcePort` or `destinationPort` outside 0–65535.
- `serverAliveIntervalSeconds` or `serverAliveCountMax` < 1.
- `controlPersistSeconds` < 0.
- Missing or blank required `host`.
- Missing required `name`.
- (Document shape) missing `schemaVersion`, or `profiles` is not an array.

**Warnings (still `VALID`):**

- Unknown / extra field (top-level or nested).
- Malformed `id` GUID.
- Unknown `schemaVersion` value (≠ 1) — forward-compatibility note.
- `authMode: IdentityFile` with a blank `identityFilePath`.
- A `Local` or `Remote` forward with `sourcePort` or `destinationPort` = 0.
- A `password` field present anywhere — **security flag**; passwords are vault-managed and
  must never appear in profile JSON.

**Required fields:** `name` + `host` for a single profile; `schemaVersion` + `profiles[]`
for a document. Every other field is optional (has a default).

Validity rule: zero errors → `VALID` (warnings do not fail validation); any error →
`INVALID`.

## Placement & implementation notes

- New tool class alongside the existing static tool classes in
  `src/NovaTerminal.McpServer`, following the same `[McpServerTool]` static-method pattern
  as the theme tools.
- A code comment in the new tool class points at
  `src/NovaTerminal.Platform/Ssh/Models/SshProfile.cs` as the source of truth, noting the
  drift-guard test.
- Logging stays on stderr (stdio transport requirement); no new dependencies.

## Testing

New file: `tests/NovaTerminal.McpServer.Tests/ConnectionProfileToolsTests.cs`
(xUnit v3, matching `ThemeToolsTests` style).

- **Schema tool:** non-empty output; contains each field-group heading; the embedded
  example parses as JSON and passes its own validator (self-consistency).
- **Validator happy paths:** minimal valid single profile; full profile with
  `jumpHops` / `forwards` / `muxOptions`; full `SshStoreDocument` with multiple profiles.
- **Validator errors:** parse failure; non-object root; bad enum (`authMode: "Foo"`);
  `port: 0` and `port: 70000`; `serverAliveCountMax: 0`; missing `host`; missing `name`;
  document missing `schemaVersion`; `profiles` not an array; wrong type (`port: "22"`).
- **Validator warnings (still VALID):** unknown field; `password` present (security flag);
  malformed `id`; `authMode: IdentityFile` with blank path; unknown `schemaVersion`.
- **Robustness:** null / empty input; nested path reporting
  (`profiles[2].forwards[0].kind`).
- **Auto-detect:** the same payload recognized correctly as single vs document.

**Drift guard:** add a **test-only** `ProjectReference` from
`tests/NovaTerminal.McpServer.Tests` (not the server) to `NovaTerminal.Platform`, plus a
reflection test asserting:

- the documented field set matches `SshProfile`'s public properties (and nested types'
  properties), and
- the enum value lists used by the tools match the actual enums
  (`SshBackendKind`, `SshAuthMode`, `RemoteShellKind`, `PortForwardKind`).

If someone adds or renames an `SshProfile` field, this test fails in CI and points at the
MCP tool to update.

## Documentation updates

- `docs/mcp/tools.md`: add the two tools to the tools table; **remove** the
  connection-profile entry from the "Still deferred" section.
- `docs/mcp-dev-companion.md`: bump tool count 11 → 13; note as the v0.3 increment.

## CI

The `NovaTerminal.McpServer.Tests` project is already registered in the unit-test loop, so
no new project registration is needed. Verify `ci.yml` needs no path changes for the new
test file (it should not, since the project is already listed). The new test-only
`ProjectReference` to `NovaTerminal.Platform` must build cleanly in the CI test job.

## Out of scope / future

- Running-app read-only bridge tools (`list_open_tabs`, `get_active_profile_metadata`, …)
  remain deferred — sensitive, opt-in, require a separate threat model.
- A formal machine-readable JSON Schema document could be added later if a consuming
  linter needs it.
