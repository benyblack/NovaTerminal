# Command Assist M4 Helper Surfaces Design

Date: 2026-03-09
Status: Approved design

## Goal

Add M4 helper surfaces to Command Assist so the pane-local assist can help when the user does not remember exact syntax, can recover from failed commands, and can explicitly ask for explanation of selected output.

M4 scope:
- help mode
- examples and recipes
- fix mode after failed commands
- selected-output explain entry point
- no mandatory AI dependency

## Existing Context

The current implementation already has the right high-level shape:

- `TerminalPane` owns pane-local Command Assist state and already hosts the assist overlay.
- `CommandAssistController` already owns pane-local query state, suggestion state, history capture, shell-integration updates, and alt-screen hiding.
- `TerminalPane` already exposes command lifecycle context through `CommandStarted`, `CommandFinished`, `LastExitCode`, current working directory, shell/profile/session metadata, and remote-host metadata.
- `TerminalView` already exposes selected text through `GetSelectedText()` and selection state through `HasSelection()`.
- The assist UI is already rendered in Avalonia as an overlay, not in the terminal grid.

This means M4 should extend the current Command Assist subsystem rather than introduce a second helper surface architecture.

## Recommended Approach

Recommended option: extend the existing pane-local assist overlay into a unified helper surface with mode-specific content for `Suggest`, `Search`, `Help`, and `Fix`.

Why:
- it fits the current architecture cleanly
- it keeps all helper behavior pane-local and contextual
- it avoids a second focus/navigation system
- it preserves the existing alternate-screen hiding behavior
- it keeps the UI out of the terminal grid

Rejected alternatives:
- a separate side panel as the primary M4 experience
- a command-palette-only helper flow

Both would add more UI/state complexity than M4 needs.

## Architecture

M4 stays inside `NovaTerminal.App.CommandAssist`.

### Domain / provider seams

Add under `src/NovaTerminal.App/CommandAssist/Domain`:

- `ICommandDocsProvider`
- `IRecipeProvider`
- `IErrorInsightService`

These remain deterministic, local, and non-AI for M4.

### Application layer

Add under `src/NovaTerminal.App/CommandAssist/Application`:

- `CommandAssistModeRouter`
- `CommandAssistResultBuilder`
- `CommandAssistContextSnapshot`
- optional `RecognizedCommandParser`

Responsibilities:
- choose between `Suggest`, `Search`, `Help`, and `Fix`
- shape docs/recipes/error insights into the current assist result model
- keep mode logic out of the Avalonia view layer

### Models

Add under `src/NovaTerminal.App/CommandAssist/Models`:

- `CommandAssistMode`
- `CommandHelpQuery`
- `CommandFailureContext`
- `CommandHelpItem`
- `CommandFixSuggestion`
- `CommandAssistContextSnapshot`

Extend:
- `AssistSuggestionType` with `Recipe`, `Doc`, `Fix`
- `AssistSuggestion` with `Description`

Recommendation: keep `AssistSuggestion` as the common UI row contract rather than creating a second list model just for helper surfaces.

### UI

Keep using:
- `CommandAssistBarView`
- `CommandAssistBarViewModel`
- the existing overlay host in `TerminalPane`

The current result list and details section should become mode-aware instead of being replaced.

## Data Flow

### Suggest / Search

Current behavior remains intact:
- typing updates the query
- history/snippet ranking remains the fast path

### Help mode

Triggered explicitly from a shortcut or a pane action.

Input context:
- current query text
- recognized primary command token
- shell kind
- current working directory
- profile/session metadata
- optional selected output text

Flow:
1. `TerminalPane` asks the controller to open help.
2. The controller builds a `CommandHelpQuery`.
3. Docs and recipe providers return local helper content.
4. `CommandAssistResultBuilder` shapes results into `AssistSuggestion` rows.
5. The overlay shows help content in the same result list/details surface.

### Fix mode

Triggered from command completion when there is high-confidence local insight.

Input context:
- last submitted or accepted command
- exit code
- shell kind
- cwd
- remote/local metadata
- optional selected output text when explicitly invoked

Flow:
1. `TerminalPane` observes `CommandFinished`.
2. For non-zero exit, it builds a `CommandFailureContext`.
3. `IErrorInsightService` analyzes that context.
4. If confidence is high, the controller enters `Fix` mode.
5. If confidence is low, the bar should expose a subtle fix affordance rather than force-open.

### Explain Selection

Triggered explicitly from terminal selection UI.

Flow:
1. `TerminalPane` checks `TermView.HasSelection()`.
2. It gets text via `TermView.GetSelectedText()`.
3. The controller opens a help/fix flavored helper mode using that text as explicit context.

No background scraping of terminal output is needed for M4.

## Provider Strategy

M4 should ship with deterministic local providers only.

### Docs provider

Use a small curated local command help catalog plus recent history variants for recognized commands.

The initial corpus should stay intentionally small and high-value rather than pretending to be a full shell manual.

### Recipe provider

Use a local seed-backed provider with examples for:
- common Git flows
- file/path navigation
- search/filtering basics
- container basics
- PowerShell and POSIX flavored variants where needed

### Error insight service

Use conservative heuristics only.

High-confidence triggers:
- command-not-found patterns
- obvious path-not-found patterns
- known executable invocation mistakes
- simple shell mismatch patterns
- nearest known command suggestions from docs/snippets/history when edit distance is low

Avoid speculative diagnosis.

## UI Behavior

The assist remains visually separate from the terminal.

Behavior:
- `Suggest` and `Search` remain the default modes while typing
- `Help` is explicit
- `Fix` may auto-open only for high-confidence failures
- helper content must still auto-hide in alternate-screen/fullscreen TUI scenarios

Mode-aware empty states:
- `Help`: no local help found
- `Fix`: no likely local fix found

Add a pane context menu action:
- `Explain Selection`

Recommended keyboard additions:
- keep existing `Ctrl+Space`, `Ctrl+R`, `Esc`, `Tab`, `Up/Down`
- add one explicit help shortcut in `MainWindow`
- keep helper execution explicit and separate from insertion

## Boundaries

These should remain untouched:
- VT parser behavior unless an unrelated regression is found
- renderer and draw operations
- terminal buffer/grid behavior
- PTY transport
- shell integration provider contracts except consuming their outputs

M4 must remain an App/Avalonia subsystem.

## Risks

### Product / UX risks

- fix mode becomes noisy if it opens on every non-zero exit
- the helper list becomes cluttered if docs/recipes/history are mixed without clear ranking and badges
- “Explain Selection” becomes confusing if it tries to infer too much from arbitrary output

### Technical risks

- accidental UI/key-routing regressions in the existing assist overlay
- duplicated result-model logic if helper surfaces fork the current list contract
- coupling helper behavior to shell integration internals instead of pane/session outputs

### Mitigations

- only auto-open fix mode for high-confidence results
- keep a single result-row contract where possible
- keep provider interfaces small and deterministic
- add focused controller tests for mode routing
- preserve alt-screen hide behavior as a hard rule

## Testing Strategy

Add deterministic tests for:
- docs provider lookup
- recipe provider filtering
- error insight heuristics
- mode router decisions
- result shaping for help/fix rows
- controller mode transitions
- explicit explain-selection behavior
- fix auto-open gating on confidence
- alt-screen hiding with helper modes active

Keep rerunning:
- existing Command Assist controller tests
- shell integration tests
- alternate-screen tests
- key-routing tests

## Definition Of Done

M4 is complete when:
- the assist can open `Help` and `Fix` modes without leaving the pane-local overlay model
- a failed command can produce a useful local fix suggestion in high-confidence cases
- recognized commands can surface local docs/examples/recipes
- selected terminal output can explicitly open helper content
- alternate-screen hiding still works
- no helper content is rendered into the terminal grid
- no AI dependency is required
