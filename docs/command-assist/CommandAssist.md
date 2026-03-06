# NovaTerminal Command Assist UI

## Goal

Add a dedicated command assistance surface that helps users:
- find and reuse previous commands,
- discover likely next commands,
- access snippets and recipes,
- get contextual help,
- optionally use AI for explanation and repair,

without mixing suggestion text into the terminal grid itself.

This should feel modern, fast, and terminal-native rather than intrusive.

---

## 1. Product position

### Core idea
NovaTerminal remains a serious terminal emulator.

The new feature is a separate UI layer called **Command Assist** that attaches to a terminal pane and activates only when relevant.

This is **not**:
- a shell replacement,
- a permanent chat sidebar,
- an always-on AI copilot,
- an inline ghost text system inside the terminal grid.

This **is**:
- a contextual command helper,
- a searchable history surface,
- a snippet/recipe launcher,
- a command/error explainer,
- an optional AI-assisted workflow surface.

---

## 2. UX model

### Primary surface: bottom assist bar
Each terminal pane can host a compact **bottom docked assist bar**.

It appears when:
- the user starts typing at a prompt,
- the user presses a shortcut like `Ctrl+Space`,
- the last command failed,
- the user explicitly opens search/help mode.

It hides when:
- the session enters alternate screen mode,
- a fullscreen TUI is active,
- the command completes and no relevant suggestion is needed,
- the user dismisses it.

### Secondary surface: command palette
A global or per-pane command palette can expose:
- history search,
- snippets,
- saved workflows,
- helper actions,
- AI actions.

Shortcut example:
- `Ctrl+Shift+P` for command palette
- `Ctrl+R` for history-focused mode
- `Ctrl+Space` for context assist

### Tertiary surface: side helper panel
For richer content:
- command docs,
- examples,
- error explanations,
- AI-generated suggestions,
- selected-output analysis.

This should be optional and not the default typing surface.

---

## 3. Main interaction modes

### Mode A: Suggest
Triggered while the user is typing at a prompt.

Shows:
- top history matches,
- prefix-based matches,
- same-directory matches,
- pinned snippets,
- recent successful commands.

Primary actions:
- Insert
- Replace input
- Execute
- Pin
- Copy

### Mode B: Search
Expanded fuzzy search over:
- command history,
- snippets,
- saved recipes,
- team/shared templates later.

Shows ranked results with metadata.

### Mode C: Help
Activated explicitly or when the current command is recognized.

Shows:
- usage examples,
- common flags,
- recent variants from user history,
- short distilled help.

### Mode D: Fix
Activated after a command exits non-zero or when stderr appears significant.

Shows:
- likely typo corrections,
- missing flag hints,
- cwd-related suggestions,
- optional AI explanation.

### Mode E: Ask AI
Explicitly invoked only.

Examples:
- “Create a find command for files over 500MB modified this week”
- “Explain this grep pipeline”
- “Fix this PowerShell command”
- “Turn this bash command into pwsh”

---

## 4. Detailed UI spec

### 4.1 Collapsed assist bar
Compact row at bottom of terminal pane.

Contains:
- current mode label
- top suggestion text
- 2–4 quick actions
- tiny hint strip for hotkeys

Example:
- `Suggest | git checkout main | Tab insert | Enter run | ↑↓ browse | Esc close`

### 4.2 Expanded assist panel
Opens above the bar, still inside pane bounds.

Sections:
- result list
- metadata area
- action footer

Each result row may show:
- command text
- badges: `Recent`, `Pinned`, `Same cwd`, `Worked`, `Snippet`, `AI`
- timestamp
- shell
- cwd or shortened path
- success/failure indicator

### 4.3 Result metadata
When a result is selected:
- full command preview
- when last used
- how often used
- success rate
- shell/profile/host
- source type: History / Snippet / AI / Recipe

### 4.4 Empty states
Examples:
- “No matching history”
- “No snippets yet”
- “AI assist unavailable offline”
- “Shell integration not detected; using heuristic mode”

---

## 5. Visual behavior rules

### Must
- stay visually separate from terminal content,
- never write suggestion text into the terminal buffer,
- animate lightly and quickly,
- remain readable in compact pane sizes,
- support theme integration.

### Must not
- obscure too much terminal output,
- appear over alternate-screen TUIs,
- steal focus unexpectedly,
- auto-run commands without explicit confirmation.

### Recommended size behavior
- collapsed height: ~32–40 px
- expanded panel height: 160–280 px typical
- max width: pane width
- responsive compaction for narrow panes

---

## 6. Ranking model

Use a weighted ranking pipeline.

### Signals
- exact prefix match
- token prefix match
- fuzzy similarity
- recency
- frequency
- same current working directory
- same shell
- same profile
- same remote host/session
- prior success
- pin/snippet boost
- command length penalty for noisy long entries

### Example scoring formula
Conceptually:

```text
score =
  prefixScore * 5 +
  tokenMatch * 3 +
  fuzzyScore * 2 +
  recencyWeight +
  frequencyWeight +
  cwdBoost +
  shellBoost +
  successBoost +
  pinBoost
```

Do not overfit early. Keep ranking explainable.

---

## 7. Data model

### 7.1 Command history entry

```csharp
public sealed record CommandHistoryEntry(
    string Id,
    string CommandText,
    DateTimeOffset ExecutedAt,
    string ShellKind,
    string? WorkingDirectory,
    string? ProfileId,
    string? SessionId,
    string? HostId,
    int? ExitCode,
    TimeSpan? Duration,
    bool IsRemote,
    bool IsRedacted,
    string Source // Heuristic, ShellIntegration, Imported, SnippetExpansion
);
```

### 7.2 Suggestion item

```csharp
public sealed record AssistSuggestion(
    string Id,
    AssistSuggestionType Type,
    string DisplayText,
    string InsertText,
    string? Description,
    IReadOnlyList<string> Badges,
    double Score,
    string? WorkingDirectory,
    DateTimeOffset? LastUsedAt,
    int? ExitCode,
    bool CanExecuteDirectly
);
```

### 7.3 Snippet item

```csharp
public sealed record CommandSnippet(
    string Id,
    string Title,
    string CommandTemplate,
    string? Description,
    string ShellKind,
    IReadOnlyList<string> Tags,
    bool IsPinned,
    bool RequiresInput
);
```

---

## 8. Architecture

Keep this out of renderer/VT core.

### Proposed subsystem layout

#### Application layer
- `CommandAssistController`
- `CommandAssistViewModel`
- `AssistOverlayHost`
- `AssistInteractionRouter`

#### Domain/services
- `HistoryStore`
- `HistoryIndexer`
- `SuggestionEngine`
- `SnippetStore`
- `RecipeProvider`
- `CommandClassifier`
- `ErrorInsightService`
- `SecretsFilter`
- `ShellContextTracker`
- `CommandBoundaryTracker`

#### Optional providers
- `IAiAssistProvider`
- `ICommandDocsProvider`
- `IShellIntegrationProvider`

#### Pane/session integration
- `ITerminalSessionContext`
- `ITerminalInputObserver`
- `ITerminalCommandEventSource`

---

## 9. Shell integration strategy

This is the difference between “nice demo” and “feels real”.

### Level 1: heuristic mode
Works without shell integration.

Possible signals:
- local keystrokes
- Enter press
- visible prompt heuristics
- paste detection
- simple command line capture

Pros:
- works broadly
- fast to ship

Cons:
- inaccurate boundaries
- weak cwd awareness
- harder multiline handling

### Level 2: integrated mode
Add shell integration scripts/plugins for:
- PowerShell
- bash
- zsh
- fish

Want structured events for:
- prompt ready
- current cwd
- command accepted
- command completed
- exit code
- optional duration

This enables much better ranking and timing.

---

## 10. Security and privacy

### Rules
- redact obvious secrets before persistence,
- allow disabling history capture entirely,
- allow per-profile opt-out,
- allow excluded command patterns,
- support “private session” mode,
- do not send command text to AI unless explicitly allowed.

### Secret detection examples
- `--password`
- `token=`
- `Authorization: Bearer`
- AWS keys
- JWT-like blobs
- connection strings
- `sshpass`
- cloud CLI secret flags

### Storage
- local persistent store
- encrypted if feasible for sensitive metadata
- bounded retention settings
- user-clearable

---

## 11. TUI / alternate screen behavior

Command Assist must disappear automatically when:
- alternate screen is entered,
- fullscreen TUI is active,
- mouse/keyboard focus is clearly inside a TUI app.

Examples:
- `vim`
- `nvim`
- `htop`
- `lazygit`
- `btop`
- `mc`

Do not try to be clever here. Hide early, hide safely.

---

## 12. Performance requirements

This feature must feel immediate.

### Targets
- collapsed assist display after trigger: ideally < 16 ms from ready state
- incremental search update: < 30 ms on common history sizes
- no UI jank during typing
- no measurable impact on terminal render loop

### Design notes
- prebuild an index
- query off UI thread
- debounce lightly
- cache recent contexts
- avoid giant object churn
- keep view model diffs small

---

## 13. Avalonia component breakdown

### Suggested components

#### Views
- `CommandAssistBarView`
- `CommandAssistPanelView`
- `CommandAssistResultListView`
- `CommandHelpPanelView`
- `CommandFixPanelView`
- `CommandPaletteView`

#### ViewModels
- `CommandAssistBarViewModel`
- `CommandAssistPanelViewModel`
- `CommandAssistResultItemViewModel`
- `CommandHelpViewModel`
- `CommandFixViewModel`

#### Services
- `ICommandAssistService`
- `IHistorySearchService`
- `ISnippetService`
- `ICommandDocsService`
- `IAssistTelemetry`

---

## 14. Suggested keyboard model

### Defaults
- `Ctrl+Space` → open/toggle assist for current pane
- `Ctrl+R` → history search mode
- `Ctrl+Shift+P` → command palette
- `Tab` → insert selected suggestion
- `Enter` → execute selected suggestion if focus is in assist list and user explicitly navigated there
- `Esc` → dismiss
- `Up/Down` → move selection
- `Ctrl+Enter` → force execute selected suggestion
- `Alt+Enter` → insert without execution

Keep insert and execute clearly separate.

---

## 15. Suggested rollout roadmap

### M1 — history foundation
Deliver:
- persistent history store
- history capture in heuristic mode
- secret redaction
- bottom assist bar
- fuzzy history search
- basic ranking

Exit criteria:
- user can reopen recent commands fast
- command assist feels useful without AI

### M2 — richer suggestions
Deliver:
- prefix/token/fuzzy ranking
- cwd-aware ranking
- success-aware ranking
- pinned snippets
- metadata badges
- improved keyboard navigation

Exit criteria:
- top 3 suggestions usually feel relevant
- UX is faster than manual shell history for common cases

### M3 — shell integration
Deliver:
- PowerShell integration first
- bash/zsh/fish after
- structured prompt/command events
- better cwd/exit code tracking
- robust multiline handling

Exit criteria:
- command boundaries are trustworthy in integrated shells

### M4 — helper surfaces
Deliver:
- help mode
- command examples
- docs extraction
- error/fix mode
- selected-output explain action

Exit criteria:
- useful even when user does not remember exact syntax

### M5 — AI layer
Deliver:
- explicit AI provider integration
- NL → command
- explain command
- fix failed command
- summarize selected output

Exit criteria:
- AI adds value without becoming noisy or defaulting itself into everything

---

## 16. Monetization-friendly extensions later

Possible premium features:
- synced command history
- team snippet libraries
- shared workflows
- org policies/redaction rules
- remote-host aware suggestions
- execution analytics
- AI command repair packs
- per-project command spaces

This is where the dedicated UI becomes strategically stronger than simple inline completion.

---

## 17. Risks

### Product risks
- feels too heavy for terminal purists
- poor ranking makes it feel dumb
- AI overreach erodes trust

### Technical risks
- weak shell integration
- accidental overlay in TUIs
- history pollution from pasted scripts
- secret leakage
- UI coupling to terminal internals

### Mitigations
- strong defaults
- explicit AI
- alternate-screen auto-hide
- privacy controls
- separate subsystem boundaries

---

## 18. Recommended first implementation choice

If I were sequencing this for NovaTerminal, I would do:

**M1 + M2 first, PowerShell-first shell integration in M3**

Reason:
- high user value quickly
- manageable complexity
- NovaTerminal gets a visible modern UX win
- no need to contaminate VT/rendering core

---

## 19. Final recommendation

The best version of this for NovaTerminal is:

**a bottom docked command assist bar, backed by history/snippets/context ranking, with a command palette for search and an optional side panel for help/AI.**

That gives you the Warp-like UX direction while still keeping NovaTerminal serious, modular, and monetizable.
