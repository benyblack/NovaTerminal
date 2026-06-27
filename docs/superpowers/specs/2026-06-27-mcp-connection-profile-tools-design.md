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
  a **stable, source-generated JSON contract** (`SshJsonContext`), wrapped in an
  `SshStoreDocument` carrying a `SchemaVersion` field (current value `1`). It has **no
  `Password` field** — passwords live in the credential vault keyed by `ProfileId`
  (`VaultService`).

  **Wire format (verified against `SshJsonContext` + `JsonSshProfileStoreTests`):**
  `SshJsonContext` sets only `WriteIndented = true` — **no** `PropertyNamingPolicy` and
  **no** `JsonStringEnumConverter`. Therefore the on-disk JSON uses **PascalCase keys**
  (verbatim CLR property names, e.g. `"Profiles"`, `"Host"`, `"BackendKind"`) and
  serializes **enums as integers**, not strings. (The `JsonSshProfileStoreTests` corrupt-file
  test writes `{ "Profiles": [ ... ] }` and the password test asserts `DoesNotContain("\"Password\":")`,
  both confirming PascalCase.) Any camelCase / string-enum examples seen elsewhere in the
  codebase come from unrelated JSON contexts and do not apply here.
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
  example only, matching the existing `get_theme_schema` pattern. Because enums are
  integers on the wire, the descriptive schema must spell out each int→name mapping.
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

Field groups (from `SshProfile`) — **PascalCase keys, integer-valued enums**:

- **Identity / org:** `Id` (GUID string), `Name`, `GroupPath`, `Notes`, `AccentColor`, `Tags[]`
- **Connection:** `Host`, `User`, `Port` (int, default 22),
  `BackendKind` (int: `0`=OpenSsh, `1`=Native)
- **Auth:** `AuthMode` (int: `0`=Default, `1`=Agent, `2`=IdentityFile), `IdentityFilePath`,
  `RememberPasswordInVault` (bool)
- **Jump hosts:** `JumpHops[]` → `{ Host, User, Port }`
- **Port forwarding:** `Forwards[]` →
  `{ Kind (int: 0=Local, 1=Remote, 2=Dynamic), BindAddress, SourcePort, DestinationHost, DestinationPort }`
- **Multiplexing:** `MuxOptions` →
  `{ Enabled (bool), ControlMasterAuto (bool), ControlPath, ControlPersistSeconds (int) }`
- **Session / shell:** `ServerAliveIntervalSeconds` (int, default 30),
  `ServerAliveCountMax` (int, default 3), `ExtraSshArgs`, `WorkingDirectory`,
  `RemoteShellKind` (int: `0`=Auto, `1`=Bash, `2`=Zsh, `3`=Fish, `4`=Pwsh)
- **Document wrapper:** `SchemaVersion` (int, current = 1), `Profiles[]`

## Tool 2 — `validate_connection_profile_json`

- **Input:** `profileJson` (string).
- **Output:** a `VALID` / `INVALID` report mirroring `validate_theme_json`: a header line,
  then grouped error and warning lines, each with a field path
  (e.g. `Profiles[2].Forwards[0].Kind`).

### Auto-detection

If the parsed root object has a `Profiles` property → treat as `SshStoreDocument` and
validate each entry under the path `Profiles[i].`. Otherwise → treat as a single
`SshProfile`.

### Tiered validation rules

**Errors (→ `INVALID`):**

- JSON parse failure.
- Root is not a JSON object.
- Wrong JSON type for a known field (e.g. `Port: "22"`, `Tags` not an array).
- Enum field (`BackendKind`, `AuthMode`, `RemoteShellKind`, `Forwards[].Kind`) that is not
  a JSON integer, or is an integer outside its defined range. The message includes the
  int→name mapping as a hint.
- `Port` or `JumpHops[].Port` outside 1–65535.
- `SourcePort` or `DestinationPort` outside 0–65535.
- `ServerAliveIntervalSeconds` or `ServerAliveCountMax` < 1.
- `ControlPersistSeconds` < 0.
- Missing or blank required `Host`.
- Missing required `Name`.
- (Document shape) `Profiles` present but not a JSON array.

**Warnings (still `VALID`):**

- Unknown / extra field (top-level or nested).
- Malformed `Id` GUID.
- Missing, zero, or negative `SchemaVersion` (the store auto-defaults it to 1).
- `SchemaVersion` > 1 — forward-compatibility note.
- `AuthMode` = 2 (IdentityFile) with a blank `IdentityFilePath`.
- A `Local` (Kind 0) or `Remote` (Kind 1) forward with `SourcePort` or `DestinationPort` = 0.
- A `Password` field present anywhere (case-insensitive) — **security flag**; passwords are
  vault-managed and must never appear in profile JSON.

**Required fields:** `Name` + `Host` for a single profile; a `Profiles` array for a
document (`SchemaVersion` is recommended but warned, not required). Every other field is
optional (has a default).

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

- **Schema tool:** non-empty output; contains each field-group heading and each enum
  int→name mapping; the embedded example parses as JSON and passes its own validator
  (self-consistency).
- **Validator happy paths:** minimal valid single profile (`Name` + `Host`); full profile
  with `JumpHops` / `Forwards` / `MuxOptions` and integer enums; full `SshStoreDocument`
  (`SchemaVersion` + `Profiles[]`) with multiple profiles.
- **Validator errors:** parse failure; non-object root; enum out of range (`AuthMode: 9`);
  enum non-integer (`BackendKind: "OpenSsh"`); `Port: 0` and `Port: 70000`;
  `ServerAliveCountMax: 0`; missing `Host`; missing `Name`; `Profiles` not an array;
  wrong type (`Port: "22"`).
- **Validator warnings (still VALID):** unknown field; `Password` present (security flag);
  malformed `Id`; `AuthMode: 2` with blank `IdentityFilePath`; missing `SchemaVersion`;
  `SchemaVersion: 2`.
- **Robustness:** null / empty input; nested path reporting
  (`Profiles[2].Forwards[0].Kind`).
- **Auto-detect:** a single-profile payload vs a `{ "Profiles": [...] }` document
  recognized correctly.

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
