# Shortcut Management And Command Palette Ranking Design

## Goal

Add first-class shortcut management to Settings and make the command palette's initial list reflect persisted most-used commands.

The change must:

- cover app-level commands, pane-local commands, and Command Assist commands
- block duplicate shortcuts globally across all scopes
- persist command palette usage across app restarts
- stay in the app layer and avoid terminal core or VT parsing/rendering changes

## Current State

Today the app has three separate concerns that are only loosely connected:

1. `MainWindow` owns many app-level shortcut checks and also registers command palette commands.
2. `TerminalPane` owns pane-local and Command Assist key handling.
3. `SettingsWindow` has no shortcut-management surface.

The current command palette also uses a simple alphabetical list on open. That means:

- `Settings` has no assigned shortcut, so opening it depends on the palette.
- the initial palette list does not adapt to actual usage.
- users cannot inspect or change bindings in one place.

## Recommended Approach

Introduce a unified app-layer command catalog for bindable actions, use `TerminalSettings.Keybindings` as the persisted override source, and add a small persisted usage store for command palette ranking.

This keeps the implementation additive:

- no terminal core changes
- no suggestion rendering inside terminal grid content
- no refactor of VT parsing/rendering
- no broad input-system rewrite

## Alternatives Considered

### 1. Minimal patch over scattered hardcoded shortcuts

Add a Settings UI that edits today's existing ids and keep most routing as-is.

Pros:

- smallest upfront code change

Cons:

- preserves duplication between registration, routing, and settings UI
- makes pane-local and Command Assist bindings harder to keep consistent
- increases long-term maintenance cost

### 2. Full shortcut-manager refactor

Move all input handling immediately into a new central manager.

Pros:

- cleaner end-state if done fully

Cons:

- too broad for the feature
- higher regression risk on performance-sensitive and interactive paths
- unnecessary coupling to current delivery goals

## Architecture

### Unified bindable command catalog

Add an app-layer catalog of bindable actions. Each action should have:

- stable `Id`
- `Title`
- `Category`
- `Scope`
- `DefaultShortcut`
- optional availability predicate for commands that are contextual

Suggested scopes:

- `App`
- `Pane`
- `CommandAssist`

Examples of actions that should be cataloged in the first pass:

- `settings`
- `command_palette`
- `command_assist_toggle`
- `command_assist_help`
- `command_assist_history`
- `find`
- `new_tab`
- `close_tab`
- `close_pane`
- pane split and pane focus commands
- palette-visible app commands already registered in `MainWindow`

The catalog becomes the shared source of truth for:

- Settings UI display
- effective shortcut resolution
- duplicate validation
- command palette metadata
- execution usage tracking

### Shortcut resolution

Use `TerminalSettings.Keybindings` as the persisted override map keyed by command id. Keep default bindings in code and resolve the effective binding at runtime:

1. start from catalog default
2. apply `Keybindings` override if present
3. normalize to a canonical shortcut string format

Add an app-layer resolver/service responsible for:

- returning the effective shortcut for a command id
- enumerating all effective bindings
- validating that no duplicates exist
- resetting one binding or all bindings back to defaults

### Shortcut matching

Keep matching in app-layer code. Do not move matching into terminal core logic.

`MainWindow` and `TerminalPane` can continue to own their execution paths, but they should query the shared resolver rather than relying on duplicated fallback strings spread across handlers.

This keeps:

- UI concerns out of terminal core logic
- pane-local behavior local to `TerminalPane`
- Command Assist behavior local to the Command Assist subsystem

### Command palette usage store

Add a small persisted usage store separate from `TerminalSettings`.

Store per command id:

- use count
- last used timestamp

The store should live under the existing app data root as a separate JSON file so settings and behavioral telemetry stay decoupled.

## Settings UI

### New Shortcuts tab

Add a third tab to `SettingsWindow` named `Shortcuts`.

The page should be Avalonia-based and consistent with the existing settings UI. It should show a searchable list of bindable commands grouped by scope and category.

Each row should display:

- action title
- scope
- current effective shortcut
- default shortcut
- reset action

Optional but useful first-pass affordances:

- search box filtering by title, category, scope, or shortcut text
- small status text for validation errors

### Editing flow

Editing should be explicit:

1. user activates a shortcut field or edit button
2. UI enters capture mode for that action
3. next supported key chord is recorded
4. chord is normalized and validated
5. save the override only if valid and unique

Rejected edits should leave the previous binding unchanged.

### Conflict handling

Duplicates are blocked globally across all scopes.

If a captured shortcut is already assigned:

- do not save the new value
- keep the old value
- show inline error text identifying the conflicting command

This must apply uniformly to:

- app commands
- pane-local commands
- Command Assist commands

### Scope boundaries in UI

The first version should expose one unified surface, but still make scope visible so users understand where a command applies.

Suggested grouping:

- App
- Pane
- Command Assist

## Command Palette Ranking

### Empty query behavior

When the command palette opens with an empty query, show the most-used commands first using persisted usage data.

Secondary ordering for ties should be deterministic, for example:

1. higher use count
2. more recent use
3. category/title

### Search behavior

When the user types a query, search relevance should remain primary. Usage should act as a secondary tie-breaker among similarly relevant results.

That keeps search predictable while still making familiar commands surface earlier.

### Usage collection

Increment command usage when a command executes successfully from:

- the command palette
- its keyboard shortcut
- any shared execution path that maps to a catalog command id

This ensures ranking reflects real user behavior, not just palette clicks.

## Data Model

### Existing settings

Reuse:

- `TerminalSettings.Keybindings`

No breaking migration is needed beyond consistently honoring entries in that map.

### New persisted store

Add a dedicated JSON store for command palette usage, for example:

- `command-palette-usage.json`

This file should be tolerant of:

- missing file
- unknown command ids
- removed commands
- malformed content falling back to empty state

## Execution Model

Execution should stay where it belongs today:

- `MainWindow` executes app-level commands
- `TerminalPane` executes pane-local commands
- Command Assist actions remain within the Command Assist subsystem

The catalog should reference these execution paths without collapsing them into terminal core code.

That preserves separation of concerns while still making shortcut management unified.

## Validation Rules

### Supported first-pass bindings

Support the same general single-chord format the app already uses, including combinations such as:

- `Ctrl+Shift+P`
- `Ctrl+Space`
- `Alt+Left`
- `Ctrl+OemPlus`

The first pass should not attempt:

- multi-step chords
- per-profile bindings
- imported shortcut schemes

### Duplicate policy

No duplicate effective shortcuts anywhere in the catalog.

This includes duplicates across different scopes. The system should reject them rather than trying to resolve precedence.

## Testing Strategy

Prefer deterministic tests focused on domain and service logic.

Add tests for:

- catalog completeness and stable ids for bindable commands
- effective binding resolution from defaults plus overrides
- global duplicate detection across scopes
- shortcut normalization and parsing for supported chords
- reset-to-default behavior
- usage store load/save behavior
- palette empty-state ranking by persisted usage
- palette search ordering where usage breaks relevance ties
- `Settings` having a default binding and being invokable through the shared path

Avoid fragile UI snapshot dependencies unless the repo already relies on them for this area.

## Migration

Migration should be minimal:

- existing users keep current behavior until they edit shortcuts
- existing `Keybindings` entries remain valid and are honored
- usage store starts empty if the file does not exist

No data migration should be required for terminal profiles, terminal core state, or Command Assist history.

## First-Pass Scope

### In scope

- add `Shortcuts` tab to Settings
- define unified bindable command catalog
- route effective shortcuts through shared resolution
- assign a default shortcut for `Settings`
- support app, pane-local, and Command Assist bindings
- block duplicate shortcuts globally
- persist command palette usage across restarts
- rank empty-query palette view by most-used commands
- use usage as search tie-breaker

### Out of scope

- multi-stroke key chords
- per-profile shortcut schemes
- import/export of keymaps
- fuzzy conflict precedence rules
- terminal core input refactors
- rendering suggestions inside the terminal grid

## Recommendation Summary

Implement shortcut management as an additive app-layer capability built on:

- a unified bindable command catalog
- `TerminalSettings.Keybindings` for overrides
- a separate persisted command palette usage store

This resolves both reported gaps while preserving the current architectural boundaries:

- shortcut discovery and customization become first-class in Settings
- `Settings` no longer depends on palette-only access
- command palette open state reflects real user habits over time
