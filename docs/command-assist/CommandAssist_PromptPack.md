# NovaTerminal Command Assist — Codex / Antigravity Prompt Pack

Use these prompts milestone-by-milestone. They are written to push the coding agent toward a clean architecture, strict boundaries, and TDD-first implementation.

---

## Prompt 1 — Architecture and codebase reconnaissance

You are working on NovaTerminal.

Your task is to design and prepare a new feature called **Command Assist** with a Warp-like dedicated UI, but **without** inline ghost text rendered into the terminal grid.

Goals:
- add a bottom-docked assist UI per terminal pane,
- support searchable command history,
- support ranked suggestions,
- keep it completely separate from VT parsing and render-core internals,
- design for future shell integration, snippets, docs/help, and AI providers.

Constraints:
- do not couple this to renderer internals,
- do not mutate terminal buffer contents for suggestions,
- do not introduce feature logic into the VT engine,
- preserve alternate-screen / fullscreen TUI behavior by auto-hiding assist UI,
- design for Avalonia UI + C# architecture consistency.

What to do:
1. Inspect the current solution structure and identify the best projects/namespaces for:
   - history persistence
   - command assist domain services
   - pane/session integration
   - Avalonia views/viewmodels
   - settings/config
2. Produce a concise architecture note with:
   - proposed folders/files
   - interfaces to add
   - event/data flow
   - dependencies and boundaries
3. Identify all likely integration points with current terminal session lifecycle, input handling, profile/session state, and overlay UI host infrastructure.
4. Identify risks where the feature could accidentally leak into rendering core, and explicitly avoid them.
5. Do not implement yet unless small scaffolding is clearly needed.

Output format:
- "Current architecture findings"
- "Recommended subsystem placement"
- "Required interfaces and models"
- "Integration points"
- "Risk list"
- "First implementation slice"

---

## Prompt 2 — Milestone M1 foundation with strict TDD

Implement **M1: history foundation** for NovaTerminal Command Assist.

Requirements:
- persistent command history store,
- heuristic-mode history capture,
- secrets redaction filter,
- bottom assist bar UI scaffold,
- fuzzy history search,
- basic ranking,
- per-pane activation,
- no AI,
- no shell integration scripts yet.

Rules:
- TDD first,
- add failing tests before production code,
- keep renderer and VT engine untouched except for minimal safe integration interfaces if necessary,
- avoid speculative abstractions beyond what M1 needs,
- ensure assist UI auto-hides in alternate screen mode.

Implementation scope:
1. Add domain models:
   - CommandHistoryEntry
   - AssistSuggestion
2. Add services:
   - IHistoryStore / HistoryStore
   - ISecretsFilter / SecretsFilter
   - ISuggestionEngine / basic history-backed SuggestionEngine
3. Add persistence:
   - local on-disk store with bounded retention
   - safe handling for redacted commands
4. Add heuristic capture path:
   - capture likely executed commands from local typed input / Enter workflow where feasible
   - clearly label source as Heuristic
5. Add Avalonia scaffold:
   - CommandAssistBarView
   - CommandAssistBarViewModel
   - assist bar host attached to terminal pane
6. Add history search mode triggered by Ctrl+R or equivalent command binding.
7. Add tests for:
   - history persistence
   - secret redaction
   - ranking order
   - alternate-screen auto-hide behavior
   - assist bar activation/deactivation

Deliverables:
- code changes
- test coverage
- short architecture summary of what was added
- list of known limitations of heuristic mode

---

## Prompt 3 — M2 ranked suggestions and snippets

Implement **M2: richer suggestions** for NovaTerminal Command Assist.

Requirements:
- prefix + token + fuzzy ranking,
- cwd-aware ranking,
- success-aware ranking,
- pinned snippets,
- badges/metadata,
- keyboard navigation refinement.

Rules:
- preserve M1 public contracts unless there is a strong reason to revise them,
- any contract changes must be justified and covered by tests,
- no AI yet,
- no shell integration scripts yet,
- maintain UI responsiveness.

Implementation tasks:
1. Extend ranking model to include:
   - exact prefix
   - token prefix
   - fuzzy score
   - recency
   - frequency
   - same cwd boost
   - same shell/profile boost where available
   - success boost
   - pinned snippet boost
2. Add snippet subsystem:
   - CommandSnippet model
   - ISnippetStore / local store
   - ability to pin and unpin suggestions/snippets
3. Expand assist panel UI:
   - result list
   - metadata preview
   - badges such as Recent / Same cwd / Worked / Pinned / Snippet
4. Add keyboard behaviors:
   - Up/Down select
   - Tab insert
   - Esc dismiss
   - explicit execute action separated from insert
5. Add tests for:
   - ranking relevance
   - pinned behavior
   - cwd influence
   - successful-command preference
   - keyboard interaction logic

Output:
- implementation summary
- list of files changed
- tests added
- any ranking tuning notes

---

## Prompt 4 — M3 PowerShell-first shell integration

Implement **M3 shell integration**, starting with PowerShell first.

Goals:
- move from heuristic command capture toward structured command lifecycle events,
- improve prompt detection, cwd tracking, exit-code tracking, and multiline command handling,
- keep graceful fallback to heuristic mode.

Requirements:
- design a generic shell integration contract,
- implement PowerShell integration first,
- do not break non-integrated shells,
- expose structured events for prompt ready, command accepted, command completed, cwd changed, exit code, duration if available.

Tasks:
1. Add interfaces such as:
   - IShellIntegrationProvider
   - ShellCommandEvent / PromptReadyEvent / CwdChangedEvent
2. Design how shell integration scripts are injected/configured/documented.
3. Implement PowerShell integration path with minimal operational friction.
4. Ensure command history entries produced by shell integration are clearly marked as ShellIntegration source.
5. Update ranking/context pipeline to prefer structured data when available.
6. Preserve fallback to heuristic mode when integration is unavailable.
7. Add tests for:
   - event parsing / ingestion
   - cwd updates
   - exit code capture
   - multiline handling
   - fallback behavior

Output:
- implementation
- setup notes for PowerShell integration
- known gaps for bash/zsh/fish

---

## Prompt 5 — M4 help and fix surfaces

Implement **M4 helper surfaces** for Command Assist.

Requirements:
- help mode,
- examples mode,
- fix mode after failed commands,
- selected-output explain entry point,
- still no mandatory AI dependency.

Tasks:
1. Add command help abstractions:
   - ICommandDocsProvider
   - RecipeProvider
   - ErrorInsightService
2. Support these UI modes:
   - Help
   - Fix
   - Search
   - Suggest
3. Trigger fix mode when:
   - exit code non-zero, or
   - obvious command-not-found / typo patterns are detected.
4. Add examples / recipes surface for recognized commands.
5. Add explain-selected-output action hook from terminal selection UI.
6. Keep all helper content outside terminal grid.
7. Add tests for:
   - mode switching
   - fix trigger behavior
   - result shaping
   - UI visibility rules in alternate screen mode

Output:
- implementation summary
- extensibility notes for future docs providers
- list of non-AI heuristics used in fix mode

---

## Prompt 6 — M5 optional AI provider

Implement **M5 optional AI provider** for Command Assist.

Requirements:
- explicit opt-in,
- clear visual labeling for AI suggestions,
- no automatic transmission of command text without user consent,
- support:
  - NL → command
  - explain command
  - fix failed command
  - summarize selected output

Tasks:
1. Add:
   - IAiAssistProvider
   - consent/settings gate
   - AI-tagged suggestion/result types
2. Ensure AI is only used when explicitly invoked or when user settings allow it for certain flows.
3. Keep local history/snippet suggestions as the default fast path.
4. Add tests for:
   - consent gating
   - provider absence
   - labeling and source tagging
   - fallback to non-AI paths
5. Produce a short security/privacy note in code comments or docs.

Output:
- implementation summary
- safety/privacy notes
- settings and UX notes

---

## Prompt 7 — Hardening and polish pass

Perform a hardening pass on NovaTerminal Command Assist.

Focus:
- performance,
- privacy,
- TUI coexistence,
- UX polish,
- maintainability.

Tasks:
1. Audit for renderer/VT-core leakage and remove any improper coupling.
2. Verify assist UI never appears during alternate screen / fullscreen TUI apps.
3. Audit history persistence for:
   - secret leakage
   - retention limits
   - clear-history behavior
4. Profile search/ranking path for typing latency and optimize obvious hotspots.
5. Audit keyboard/focus behavior so assist UI never steals focus incorrectly.
6. Improve empty states, badges, and compact layout behavior for narrow panes.
7. Add or strengthen tests around:
   - rapid typing
   - pane switching
   - session disposal
   - remote session metadata
   - settings toggles

Output:
- issues found
- fixes applied
- remaining backlog
- recommended release criteria

---

## Prompt 8 — PR review checklist prompt

Review the Command Assist implementation branch deeply.

Check:
- architecture boundaries
- correctness
- privacy/security
- alternate-screen behavior
- ranking quality
- UI/UX consistency
- settings discoverability
- performance risks
- test quality
- future extensibility

Return:
1. Critical issues
2. Important issues
3. Nice-to-have improvements
4. Suggested follow-up milestones
5. Merge readiness verdict

Be concrete. Reference exact files and code paths where possible.
