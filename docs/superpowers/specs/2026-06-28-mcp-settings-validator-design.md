# MCP Dev Companion — Settings Schema & Validation Tools (v0.4)

**Date:** 2026-06-28
**Status:** Design approved, ready for implementation plan
**Component:** `src/NovaTerminal.McpServer`

## Summary

Add two read-only MCP tools that let a developer or AI client discover and validate the
NovaTerminal app **settings** JSON (`settings.json`), completing the theme → connection-profile
→ settings validation trio:

- `novaterminal.get_settings_schema`
- `novaterminal.validate_settings_json`

This brings the server's tool count from **13 → 15** (v0.4).

## Background — wire format (verified)

`TerminalSettings` (`src/NovaTerminal.App/Shell/TerminalSettings.cs`) is serialized via
`AppJsonContext` (`src/NovaTerminal.App/Shell/AppJsonContext.cs`) to
`%LOCALAPPDATA%\NovaTerminal\settings.json` (override dir: `NOVATERM_APPDATA_ROOT`).

`AppJsonContext` sets `WriteIndented = true` and two color converters, but **no**
`PropertyNamingPolicy` and **no** `JsonStringEnumConverter`. Therefore the on-disk JSON uses
**PascalCase keys** (verbatim CLR property names) and serializes **enums as integers** — the
same wire format as the connection-profile tools. Confirmed by `TerminalSettingsFontTests`
(round-trips PascalCase `FontFamily`). Two properties are `[JsonIgnore]` and never serialized:
`ThemeManager`, `ActiveTheme`.

## Scope — top-level + structural (depth A)

`settings.json` is large: ~30 scalar fields plus an embedded `Profiles: List<TerminalProfile>`
(the *app-layer* `TerminalProfile` — Command/Arguments/Type/SshHost/…, a different and larger
model than the platform `SshProfile` the connection-profile tool validates), `TabTemplateRules`,
and a `Keybindings` dictionary.

This feature validates the **top-level settings fields + the structural shape of the
collections only**. It does **not** deep-validate each embedded `TerminalProfile` /
`ForwardingRule` / `TabTemplateRule` entry. Deep per-profile validation is explicitly deferred
(possible follow-up); re-implementing an app-profile validator distinct from the existing
`SshProfile` one is out of scope here.

## Goals

- Retrieve a human-readable description of the settings JSON format (PascalCase, integer enums).
- Validate a pasted `settings.json` for the mistakes people actually make: wrong types,
  out-of-range numerics, malformed GUID, malformed collection shapes, unknown fields.
- Keep the server's posture: read-only, no `ProjectReference`, no process/network/credential access.

## Non-goals

- No deep validation of embedded `Profiles` / `TabTemplateRules` / `ForwardingRule` entries.
- No value-set validation of the "enum-like string" fields (see below) — type-check only.
- No formal JSON Schema document — descriptive prose + an annotated example (matching the
  existing schema tools).
- No reflection drift-guard test for settings (see Testing for why).
- No reading/writing of the user's real `settings.json` — the validator works on a string argument.

## Tool 1 — `get_settings_schema`

- **Input:** none.
- **Output:** descriptive text — top-level fields grouped by area (appearance, behavior,
  command-assist, background, collections), each with type / default / notes (integer-enum and
  enum-like markers) — followed by a complete annotated example using PascalCase keys and
  integer enums.

## Tool 2 — `validate_settings_json`

- **Input:** `settingsJson` (string).
- **Output:** a `VALID` / `INVALID` report mirroring `validate_theme_json`: a header line, then
  grouped error and warning lines (`  - <message>`), trailing whitespace trimmed. Validity =
  zero errors (warnings never fail). The root is a single object — no document-wrapper
  auto-detection.

### Known top-level fields (PascalCase)

Scalars and their JSON types:
- **bool:** `EnableLigatures`, `EnableComplexShaping`, `CursorBlink`, `BellAudioEnabled`,
  `BellVisualEnabled`, `SmoothScrolling`, `EnableLinkDetection`, `QuakeModeEnabled`,
  `CommandAssistEnabled`, `CommandAssistHistoryEnabled`, `CommandAssistAutoHideInAltScreen`,
  `CommandAssistShellIntegrationEnabled`, `CommandAssistPowerShellIntegrationEnabled`,
  `ExperimentalNativeSshEnabled`
- **number (double):** `FontSize`, `WindowOpacity`, `WheelLinesPerNotch`, `BackgroundImageOpacity`
- **integer:** `MaxHistory`, `CommandAssistMaxHistoryEntries`
- **string:** `FontFamily`, `ThemeName`, `BackgroundImagePath`, `GlobalHotkey`
- **string (enum-like; type-checked only):** `BlurEffect`, `CursorStyle`, `PaneClosePolicy`,
  `BackgroundImageStretch`
- **GUID string:** `DefaultProfileId`
- **object (string→string):** `Keybindings`
- **array:** `TabTemplateRules`, `Profiles`

The known-field set is **hand-maintained** in the tool (mirrors `TerminalSettings`' serialized
properties, excluding the two `[JsonIgnore]` ones), with a comment pointing at
`TerminalSettings.cs` as the source of truth.

### Tiered validation rules

**Errors (→ `INVALID`):**

- JSON parse failure.
- Root is not a JSON object.
- Wrong JSON type for a known field (e.g. `FontSize` not a number, an integer field not an
  integer, a bool field not a boolean, a string field not a string).
- Numeric out of range: `FontSize <= 0`; `WindowOpacity` or `BackgroundImageOpacity` outside
  0–1; `MaxHistory < 0`; `CommandAssistMaxHistoryEntries < 0`.
- Malformed `DefaultProfileId` (present, a string, but not a valid GUID) — unrecoverable: the
  store deserializes it straight into a `Guid`, and the parse failure breaks loading the whole
  `settings.json` (same hazard Codex flagged for the profile `Id`).
- `Profiles` or `TabTemplateRules` present but not a JSON array.
- `Keybindings` present but not a JSON object, or any of its values not a string.

**Warnings (still `VALID`):**

- Unknown / extra top-level field.
- `WheelLinesPerNotch <= 0` (runtime falls back to 3.0).
- A `Password` field present anywhere at the top level (case-insensitive) — security flag,
  consistent with the other validators (passwords are vault-managed).

**No required fields:** an empty object `{}` is **VALID** — every setting has a default and
`LoadFromPath` tolerates omissions.

### Enum-like string fields — type-check only

`BlurEffect`, `CursorStyle`, `PaneClosePolicy`, `BackgroundImageStretch` are typed as `string`
but parsed against constrained sets in code (`ParseCursorStyle`, `Enum.TryParse<Stretch>`, UI
comboboxes, loose `ToLowerInvariant` compares), with **case-insensitive matching, aliases**
(e.g. `"bar"`/`"beam"` for cursor), and **graceful fallback** to a default on unknown input.
Their valid sets are fuzzy, partly external (Avalonia `Stretch`), UI-defined, and one even has a
misleading source comment. Value-set validation would be drift-prone and false-positive-prone for
no real safety gain (unknown values are non-fatal). The validator therefore **only type-checks**
these as strings and does not validate their values. The schema text lists the common values for
guidance, marked non-authoritative.

## Placement & implementation notes

- New static tool class in `src/NovaTerminal.McpServer` (e.g. `SettingsTools`), same
  `[McpServerToolType]` / `[McpServerTool]` pattern, `System.Text.Json` (`JsonDocument`)
  parsing, no new dependencies — exactly like the theme and connection-profile tools.
- A comment names `src/NovaTerminal.App/Shell/TerminalSettings.cs` as the source of truth for
  the hand-maintained field list, and notes the `[JsonIgnore]` exclusions.

## Testing

New file `tests/NovaTerminal.McpServer.Tests/SettingsToolsTests.cs` (xUnit v3, theme-tests style):

- **Schema tool:** non-empty; contains the area headings and key field names; the embedded
  example parses as JSON and passes its own validator (self-consistency).
- **Validator happy paths:** `{}` (all-defaults) is VALID; a full representative settings object
  (PascalCase, integer enums, a sample `Profiles` entry, `Keybindings`, `TabTemplateRules`) is VALID.
- **Validator errors:** parse failure; non-object root; wrong types (`FontSize: "big"`,
  `MaxHistory: 1.5`, `EnableLigatures: "yes"`, `Keybindings: []`, `Profiles: {}`);
  `FontSize: 0`; `WindowOpacity: 2`; `BackgroundImageOpacity: -1`; `MaxHistory: -1`;
  `CommandAssistMaxHistoryEntries: -1`; malformed `DefaultProfileId`; a `Keybindings` value
  that isn't a string.
- **Validator warnings (still VALID):** unknown field; `WheelLinesPerNotch: 0`; a top-level
  `Password` field.
- **Robustness:** null / empty input; an enum-like string with an arbitrary value is accepted
  (type-check only, no value warning).

**No reflection drift-guard for settings.** A reflection guard would require a test-only
`ProjectReference` to `NovaTerminal.App` (which pulls in Avalonia and the full app graph) — too
heavy for a field-name check. Instead the known-field list is hand-maintained with a pointer
comment to `TerminalSettings.cs`. (This matches the already-accepted hand-maintained nature of
the enum-like-string sets and numeric ranges, which aren't reflectable anyway.)

## Documentation

- `docs/mcp/tools.md`: add the two tools to the table; bump the version heading to v0.4.
- `docs/mcp-dev-companion.md`: add the two tool names to the list.

## Out of scope / future

- Deep validation of embedded `Profiles` / `TabTemplateRules` / `ForwardingRule` entries.
- Running-app read-only bridge tools — still deferred (needs a threat model).
