# Command Assist M4 Helper Surfaces Implementation Plan

Date: 2026-03-09
Status: Ready for implementation

## Scope

Implement Command Assist M4 as an additive extension of the existing pane-local assist overlay.

In scope:
- help mode
- examples and recipes
- fix mode for failed commands
- selected-output explain entry point
- deterministic local providers only

Out of scope:
- AI providers
- online docs fetching
- side-panel redesign
- non-helper Command Assist rewrites

## Constraints

- keep UI in Avalonia
- keep helper content outside terminal grid/buffer content
- keep VT/render core untouched
- preserve alternate-screen/fullscreen TUI auto-hide
- prefer additive changes over refactors
- keep provider interfaces small and explicit

## Proposed Files

### Add

`src/NovaTerminal.App/CommandAssist/Models`
- `CommandAssistMode.cs`
- `CommandHelpQuery.cs`
- `CommandFailureContext.cs`
- `CommandHelpItem.cs`
- `CommandFixSuggestion.cs`
- `CommandAssistContextSnapshot.cs`

`src/NovaTerminal.App/CommandAssist/Domain`
- `ICommandDocsProvider.cs`
- `IRecipeProvider.cs`
- `IErrorInsightService.cs`
- `LocalCommandDocsProvider.cs`
- `SeedRecipeProvider.cs`
- `HeuristicErrorInsightService.cs`

`src/NovaTerminal.App/CommandAssist/Application`
- `CommandAssistModeRouter.cs`
- `CommandAssistResultBuilder.cs`
- `RecognizedCommandParser.cs`

`tests/NovaTerminal.Tests/CommandAssist`
- `LocalCommandDocsProviderTests.cs`
- `SeedRecipeProviderTests.cs`
- `HeuristicErrorInsightServiceTests.cs`
- `CommandAssistModeRouterTests.cs`

### Modify

- `src/NovaTerminal.App/CommandAssist/Application/CommandAssistController.cs`
- `src/NovaTerminal.App/CommandAssist/Application/CommandAssistInfrastructure.cs`
- `src/NovaTerminal.App/CommandAssist/Models/AssistSuggestion.cs`
- `src/NovaTerminal.App/CommandAssist/Models/AssistSuggestionType.cs`
- `src/NovaTerminal.App/CommandAssist/ViewModels/CommandAssistBarViewModel.cs`
- `src/NovaTerminal.App/CommandAssist/Views/CommandAssistBarView.axaml`
- `src/NovaTerminal.App/Controls/TerminalPane.axaml`
- `src/NovaTerminal.App/Controls/TerminalPane.axaml.cs`
- `src/NovaTerminal.App/MainWindow.axaml.cs`
- `tests/NovaTerminal.Tests/CommandAssist/CommandAssistControllerTests.cs`
- `tests/NovaTerminal.Tests/CommandAssist/CommandAssistLayoutTests.cs`
- `tests/NovaTerminal.Tests/CommandAssist/TerminalPaneCommandAssistShortcutTests.cs`

## TDD Sequence

### Phase 1: Domain providers

Add failing tests first for:
- docs lookup by recognized command
- recipe lookup/filtering by command and shell kind
- error insight analysis for:
  - command not found
  - obvious path not found
  - shell mismatch
  - typo correction candidate

Then implement:
- `LocalCommandDocsProvider`
- `SeedRecipeProvider`
- `HeuristicErrorInsightService`

Verification target:
- provider tests only

### Phase 2: Mode routing and result shaping

Add failing tests first for:
- mode router choosing `Help` for explicit help requests
- mode router choosing `Fix` only for high-confidence failures
- result builder shaping docs/recipes/fixes into assist rows with badges and descriptions

Then implement:
- `CommandAssistModeRouter`
- `CommandAssistResultBuilder`
- `RecognizedCommandParser`

Verification target:
- provider + router/result tests

### Phase 3: Controller extension

Add failing tests first for:
- `OpenHelp(...)`
- `HandleCommandFailure(...)`
- `ExplainSelection(...)`
- mode transitions between `Suggest`, `Search`, `Help`, and `Fix`
- no helper activation in alt-screen

Then implement:
- controller APIs for M4 helper flows
- mode-aware use of docs/recipes/fix providers
- confidence gating for auto-open fix mode

Verification target:
- controller tests

### Phase 4: Pane integration

Add failing tests first for:
- command failure in `TerminalPane` triggering fix evaluation
- selection-based explain action calling into the controller
- helper actions not appearing in alt-screen

Then implement:
- `TerminalPane` adapter path for failure context
- `Explain Selection` context-menu entry
- selected-text handoff from `TerminalView`

Verification target:
- pane integration tests and existing key-routing regressions

### Phase 5: Avalonia UI extension

Add failing or headless tests first for:
- mode label changes
- help/fix details rendering
- empty states for help/fix
- helper content still hosted in the existing overlay and still hidden in alt-screen

Then implement:
- mode-aware viewmodel fields
- detail/description rendering in the existing assist UI
- empty-state text and helper affordances

Verification target:
- headless Command Assist UI tests

### Phase 6: Main window shortcuts

Add or extend tests for:
- new help shortcut routing to the active pane
- existing command palette shortcut still working when helper actions are unavailable

Then implement:
- one explicit help shortcut in `MainWindow`
- keep all helper logic pane-local

Verification target:
- targeted shortcut tests

## Heuristic Rules For M4 Fix Mode

Use local non-AI heuristics only.

### High-confidence auto-open candidates

- `command not found`
- `is not recognized as an internal or external command`
- executable/path not found patterns
- low edit-distance command typo against known commands from:
  - docs seed
  - recipe seed
  - pinned snippets
  - recent successful history

### Suggestions to generate

- corrected command name
- likely path invocation form such as `./foo`
- shell-correct invocation form such as `pwsh -File script.ps1`
- relevant recipe/example row for the failed command family

### Low-confidence cases

Do not auto-open.

Instead:
- keep the bar dormant or subtle
- allow explicit user-open fix mode

## Seed Data Strategy

Keep M4 small and curated.

Initial docs/recipe seed should cover:
- Git basics
- path and file operations
- grep/find/select-string style search
- container basics
- common PowerShell and POSIX flavored examples

Do not try to ship a full command encyclopedia in M4.

## Verification Plan

Targeted verification after implementation:
- `dotnet test` for all `tests/NovaTerminal.Tests/CommandAssist/*`
- shell integration regressions
- alternate-screen regressions
- existing key-routing tests
- compile the App project in Release

Recommended targeted filters:
- `CommandAssist`
- `ShellIntegration`
- `AlternateScreen`
- `HeadlessUI`

## Risks To Watch During Implementation

- noisy fix auto-open behavior
- controller bloat from mixing ranking and helper orchestration without a result builder
- UI complexity ballooning beyond the current overlay
- accidental command-palette interference
- accidental dependence on shell integration details instead of pane lifecycle outputs

## Exit Criteria

M4 is ready when:
- help mode returns useful local docs/examples for recognized commands
- fix mode produces useful local suggestions for high-confidence failures
- explain-selection is available from terminal selection UI
- all helper content remains outside the terminal grid
- alternate-screen hiding still works
- no AI dependency is introduced
