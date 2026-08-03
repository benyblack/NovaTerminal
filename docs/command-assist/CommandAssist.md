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

#### Shipped: the passive bubble (auto-open policy v2, V2 Phase 3b)

What Mode A actually does while you type, in an integrated session:

- **After two typed characters**, debounced ~75 ms, the bubble shows the **top-1 row of the merged
  history + path ranking**. One row, no popup. `Down` opens the list if you want the rest.
- **Below two characters** — including backspacing back down to one — the bubble goes away and no
  ranking runs at all. One character's worth of prefix ranks to "the command you run most often
  starting with that letter", which is noise wearing a suggestion's clothes.
- **`Esc` takes it down for the rest of that command line.** Not just for one frame: the next keystroke
  used to rebuild the surface, which made `Esc` useless on a bubble nobody asked for. The suppression is
  cleared by the next prompt — `OSC 133;B` — so any way of ending a line clears it, not only a local
  `Enter`: `Ctrl+C`, PSReadLine's own line-clearing `Escape`, a pasted submission, a broadcast send.
  `Ctrl+Space`, `Ctrl+R`, Help and Fix all still open after an `Esc` — suppression only applies to
  surfaces the user did not request. `Esc` pressed inside the ~75 ms debounce window, before anything is
  on screen, also cancels the pass and suppresses the line, and still reaches the shell.
- **Ranking uses the line up to the cursor, not the whole painted line.** PSReadLine's inline prediction
  is painted as ordinary cells past the cursor, and the grid cannot tell them from text you typed — so
  the bubble ranks on `AssistQuerySnapshot.TextBeforeCursor`, and the two-character floor measures that
  too. Insertion is a different question and still refuses while a prediction is showing (see
  `CommandAssist_ShellIntegration_Gaps.md`); the hint strip drops its "insert" clause rather than
  promising a key that will do nothing.
- **The popup is still intent-only.** Nothing auto-opens it: `Down`, a click, `Ctrl+Space`, `Ctrl+R` or
  a high-confidence Fix.
- **Degraded (markless) sessions get no passive bubble.** There is no readable command line, so there is
  no query, so nothing clears the two-character floor. Unchanged from Phase 2 — an explicit `Ctrl+R`
  still opens and shows the context-ordered recency list.
- **Alt screen**: no surface at all, unconditionally. See §11.

This is a deliberate reversal of M4.3's "silent unless summoned" policy, which scoped the passive path
to filesystem completions only. That kept the feature invisible for most commands, which is the problem
Phase 3 exists to solve. The kill switch (`CommandAssistPassiveBubbleEnabled`, below) puts the passive
scope back to paths-only.

It is a *scope* control, not a time machine: the other passive-path policies from this phase still apply
with it off, so a one-character line offers no path completions either, where M4.3 would have. Exempting
the two-character floor when only paths are in scope was considered and not taken — one character is not
a path fragment worth ranking, and a floor that depends on the resolved scope is a floor that can
disagree with the scope about which pass it belongs to.

**The debounce** lives in `SuggestionOrchestrator` and is applied to typing-triggered passes only: a
burst of *n* keystrokes costs one grid read, one store recall and one ranking pass instead of *n* of
each, because each new keystroke cancels the previous pass through the `CancellationTokenSource` that
already handled supersession. Explicit surfaces are never debounced — `Ctrl+R` is one deliberate act
with nothing to coalesce, and 75 ms of nothing after it is pure latency.

#### Shipped: the settings group (V2 Phase 3b)

Settings → Terminal, under "Command assistant":

| Setting | Default | What it gates |
| --- | --- | --- |
| `CommandAssistEnabled` | `false` | The master flag. Everything. |
| `CommandAssistPassiveBubbleEnabled` | `true` | Whether the passive bubble may draw on history. Off = passive scope is paths only. |
| `CommandAssistHistoryEnabled` | `true` | Command capture **and** history-sourced suggestions — nothing else. Paths, Help and Fix work with it off. |
| `CommandAssistShellIntegrationEnabled` | `true` | Whether Nova instruments local shells it starts. |

The group also carries **Clear history**, which is the first caller `IHistoryStore.ClearAsync` has ever
had. It arms on the first click ("Confirm clear") and clears on the second, and it goes through the one
live store instance rather than opening a second one over the same file. A successful clear also dismisses
every pane's assist surface, so rows that were on screen when the store was emptied do not linger as
something the user can still accept. The remote shell-integration snippet copy affordance from Phase 2b
sits directly below.

**Clear history also deletes the pre-V2 files**, and that is a privacy fix rather than tidiness. V1's
Enter-time capture read a keystroke mirror with no echo check at all, so a password typed at a
non-echoing prompt in a markless session *was* written to `history.json` verbatim — `SecretsFilter` is
pattern-based and a bare secret has no pattern. `JsonlHistoryStore` migrates that file into
`history.jsonl` unfiltered and renames the source to `history.json.bak`, so before this a user who
suspected a secret in their history and pressed the only button offered still had it on disk in the
backup, under a confirmation prompt that said "this deletes every recorded command". `ClearAsync` now
removes `history.jsonl`, `history.json` and `history.json.bak`; the confirmation copy says so.

If you ran a Nova build from before PR #286 (V2 Phase 1c, 2026-08-02), assume anything you typed at a
hidden prompt in a `cmd.exe` or un-instrumented SSH pane may be in your history, and clear it. Builds
from #286 onward cannot capture it: see the echo gate below.

`CommandAssistHistoryEnabled` used to be a second master flag by accident: `IsCommandAssistFeatureEnabled`
required it, so turning capture off killed the bubble, the popup, Help, Fix and path suggestions too.
Phase 3b decoupled them.

`CommandAssistAutoHideInAltScreen` was **deleted** in Phase 3b rather than wired up. It was never read —
alt-screen hiding is unconditional — and a switch that could disable it would only ever let the assist
paint over `vim`. Unknown keys are ignored when `settings.json` is loaded, so a file that still carries
it loads fine.

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

### Shipped: context scoping and the empty-query bands (V2 Phase 3a, revised in the PR #290 review)

`CommandAssistSuggestionEngine` has two paths, and the difference matters more than any individual
weight.

With **a text query** the context terms are nudges: same-context is worth 30, same-profile 20, same cwd
12, against text-match tiers of prefix 120 / token prefix 70 / contains 25 / subsequence 12. What the
user typed decides the order; affinity breaks ties. A partition here would rank a same-host subsequence
match above a local prefix match, which reads as the list ignoring the query.

With **no query** (`Ctrl+R`, or an explicit bubble at an empty prompt) there is nothing to match on, so
the same terms partition instead. In descending order:

| Band | Boost | What is in it |
| --- | --- | --- |
| This context, this profile | 1000 + 200 | History from this host (or local history on a local pane) run under this profile. |
| This context | 1000 | History from this host, other profiles. Pinned snippets ride here too: pinning means "in scope everywhere", and a snippet has no host to compare. |
| Snippets and same profile | 200 | **Unpinned snippets**, and history from this profile that ran somewhere else. |
| Everything else | ~1–6 | Frequency and recency only: other hosts, other machines. |

Nothing is filtered out — a command you remember running elsewhere is still in the list, below the fold,
because that is why a shared history exists.

**Why unpinned snippets sit in the same-profile band.** They are user-authored text somebody chose to
save, which is worth more than another machine's recency; they are not evidence about *this* host, which
is what the top band is for. The trade-off is stated rather than hidden: with more same-context history
rows than the popup shows, an unpinned snippet is below the fold, and pinning (`Ctrl+Shift+P`) is the
one-keystroke answer. The alternative — hoisting every snippet above this host's own history — makes the
list say "snippets first" for users who never asked for that.

**What "this context" means, and the one spelling trap in it.** On a remote pane it is the host id; on a
local pane it is "not on somebody else's machine" (there is no local host id to compare). The host id is
**the configured `Profile.SshHost` string, verbatim** — not a resolved address, not a canonical name. So
two profiles pointing at the same box as `10.0.0.5` and `build.example` do not share a band, and neither
do `build` and `build.example`. That is a deliberate consequence of using configuration rather than
resolution (resolution is a network call on a ranking path, and it changes under DHCP), and it fails in
the safe direction: an unrecognised context is not a context, so the list falls back to recency, which
is unhelpful rather than wrong.

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
    int? ExitCode
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
Shell integration providers ship for:
- PowerShell (`-File <bootstrap>`)
- bash (`--rcfile <bootstrap>`)
- zsh (`ZDOTDIR` env-override + generated `.zshrc`)
- fish (`XDG_CONFIG_HOME` env-override + generated `config.fish`)

Each bootstrap emits the normalized structured events:
- prompt ready (`OSC 133;A`)
- current cwd (`OSC 7`)
- command accepted (`OSC 133;C;<base64-utf8>`)
- command completed with exit code and duration (`OSC 133;D;<exit>;<durationMs>`)

These markers feed the shared lifecycle tracker and Command Assist controller
without per-shell branching beyond the launch-plan step.

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

**Not configurable, as of V2 Phase 3b.** `CommandAssistAutoHideInAltScreen` existed in the settings
schema and was never read by anything; the hiding has always been unconditional. It was deleted rather
than wired up, because the only thing a user could do with it is switch off the rule that stops the
overlay painting over `vim`. Unknown keys are ignored on load, so an existing `settings.json` that still
lists it is unaffected.

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

### Shipped: the regression tripwire (V2 Phase 3b)

`tests/NovaTerminal.App.Tests/Performance/CommandAssistPerformanceTests.cs`, run by CI with the rest of
the suite. It is named honestly: a tripwire, not a benchmark. The repo's real BenchmarkDotNet project
(`tests/NovaTerminal.Benchmarks`) is not run by CI, and what this phase needed was something that fails
loudly when a change makes the assist an order of magnitude slower.

Four measurements, with the thresholds set well above the targets above:

| Measurement | Tripwire | Baseline (dev box, Debug) |
| --- | --- | --- |
| Ranking 5000 entries against `git st` | p95 < 30 ms | p50 2.0 ms, p95 3.3 ms |
| Ranking 5000 entries, empty query | p95 < 30 ms | p50 1.6 ms, p95 3.3 ms |
| Keystroke handling on the caller's thread | < 1 ms mean | 0.002 ms/key over 200 keys, 0 recalls while debouncing |
| Keystroke → view-model content (no rendering) | p95 < 50 ms | p50 0.10 ms, p95 0.20 ms |

5000 entries is the default retention cap and a deliberately pessimistic bound: the store's recall gate
hands the engine at most 200 candidates, so production never ranks the whole file. Rendering is not
measured — a headless unit test cannot honestly time an Avalonia layout and draw pass, so the "first
paint" figure covers everything up to the view-model write and says so.

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

## 14. Keyboard model

### Shipped (V2 Phase 3a, extended in 3b)

This is what the product does. The original speculative table follows it, kept because the roadmap
still refers to it.

Every row below is a **catalogued, rebindable** shortcut as of Phase 3b (`ShortcutScope.CommandAssist`
in `ShortcutCatalog`, editable in Settings → Shortcuts). The "Command id" column is the key in
`settings.json`'s `Keybindings` map.

| Key | Command id | Owner | Effect |
| --- | --- | --- | --- |
| `Ctrl+Space` | `command_assist_toggle` | app | Toggle the explicit assist session for the focused pane. |
| `Ctrl+R` | `command_assist_history` | app | Open history search (popup, recency list scoped to this pane's context). |
| `Ctrl+Shift+H` | `command_assist_help` | app | Help for the command on the line, or for the selection. |
| `Ctrl+Shift+S` | `command_assist_pin` | app, falls through when no row can be pinned | Pin/unpin the selected row. Moved off `Ctrl+Shift+P` in Phase 3b — see below. |
| `Down` | `command_assist_selection_down` | assist, while a surface is visible | Move the selection down; opens the popup on the first move. |
| `Up` | `command_assist_selection_up` | assist while the popup is open, or on a surface the user summoned; **otherwise the shell** | Move the selection up. See below. |
| `Enter` | `command_assist_accept` | **assist, while the popup is open with a row selected and the overlay is actually rendered**; otherwise the shell | Insert the selected suggestion. See below. |
| `Ctrl+Enter` | `command_assist_insert` | assist, while a surface is visible | Insert the selected suggestion. Works in every state, including Help and Fix. |
| `Esc` | `command_assist_dismiss` | assist, while a surface is visible | Dismiss, and suppress the passive bubble for the rest of this command line. |
| `Tab` | — | **always the shell** | Shell completion. Command Assist never takes it. |

**Pin moved to `Ctrl+Shift+S`.** `Ctrl+Shift+P` is the command palette's, and the two used to share it:
`MainWindow` tried the pin first and opened the palette only when the pin declined, so whether the
palette opened depended on whether an assist row happened to be selected. The palette now owns the chord
unconditionally. `Ctrl+Shift+S` was picked over the design doc's suggested `Ctrl+Alt+P` because
`TerminalView` turns any `Alt`+key into an ESC-prefixed sequence for the shell and marks the event
handled, so an `Alt` chord never reaches the window's shortcut handler at all.

**The five in-surface keys are matched on their exact modifiers.** Before Phase 3b the router tested the
key and ignored the modifiers for everything except `Enter`, so `Ctrl+Down` and `Alt+Up` were swallowed
by the assist even though several line editors act on them. Rebinding these five is constrained in two
ways the Settings UI cannot express:

- **Representability.** Command Assist models `Escape`, `Up`, `Down` and `Enter`, so an override naming
  any other key falls back to the default rather than silently matching nothing. `Tab` is modelled
  internally — the router needs to be able to answer "not mine" about it — but is *not* accepted as a
  rebind target, because the row above is a promise: `Tab` is the shell's completion key and taking it is
  the most disruptive thing this feature could do.
- **The terminal gets the key first.** These chords are matched inside the pane while a surface is open,
  after `TerminalView` has had its turn. A chord the terminal encodes for the shell — most `Ctrl`+letter
  combinations, anything `Alt`+key (turned into an ESC-prefixed sequence and marked handled), and
  anything the kitty keyboard protocol encodes for a full-screen program — will look bound in Settings
  and never fire. The Shortcuts tab says so; there is no validation that can decide it, because whether a
  key reaches the assist depends on what the running program has asked for.

The hint strip renders whatever binding is actually in force, formatted by the same normalizer the
Shortcuts editor writes bindings with, so a rebind reads identically in both places.

Mouse, in the popup: hover highlights, a single click selects, and a double click — or a click on the
row that is already selected — inserts. The row list scrolls. Right-click and middle-click are
swallowed by the popup: neither means anything to the list, and left to bubble they would open the pane
context menu over it or paste into the shell underneath.

**`Down` browses suggestions while typing; `Up` remains shell history.** The entry into the row list is
one-directional in the passive states — the typing bubble and the bubble-only Fix hint, the two surfaces
the user did not ask for. `Down` has no meaning at a shell prompt, so taking it costs nothing; `Up` is
history recall in every shell there is, and taking it cost a great deal: the assist consumed the key,
opened its popup as a side effect of clamping the selection to row 0, and the `Enter` that followed
inserted a suggestion instead of submitting the command. This matches fish and PSReadLine, where
history recall is `Up`'s and nothing takes it.

Once the popup is open, `Up` and `Down` both navigate it, in every mode — the user is demonstrably in
the list. `Up` at the top row is a no-op that opens nothing and arms nothing. And on a surface the user
summoned by name (`Ctrl+Space`, `Ctrl+R`, Help, a confident Fix popup) both arrows are assist-owned from
the first keypress, because there the list *is* what was asked for.

**The `Enter` rule, precisely.** `Enter` is Command Assist's only when *all* of: a surface is visible,
the popup is open, a row is selected, the mode is Suggest or Search, and the pane's overlay host is
genuinely rendered (visible, non-zero opacity). In Suggest mode that state is only reachable by the user
having moved the selection, and in Search mode only because they pressed `Ctrl+R`; typing closes the
popup, so the ordinary type-a-command-and-press-`Enter` flow never reaches it. Help and Fix are
excluded — their rows are documentation and diagnoses rather than a command line being composed, and a
Fix popup is on screen right after a submission, where the next `Enter` is most likely aimed at the
shell.

The rendered-overlay term is not redundant with visibility. `TerminalPane` hides the overlay host on its
own authority when the conservative anchor check produces no layout, and dims it to zero opacity while a
placement correction settles; both bypasses are waived only for a *user-requested* surface, and a
passive popup is not one. Without the term, a passive popup on a short markless-SSH pane could own
`Enter` at zero pixels — nothing on screen, and the command line silently not submitting. The hint strip
reads the same predicate, so it stops promising `Enter` at the same moment.

Only a *completely unmodified* `Enter` is taken. `Shift+Enter` is a newline in several line editors,
and under the kitty keyboard protocol's disambiguate tier every modified `Enter` is a distinct `CSI u`
sequence the shell may act on. "Unmodified" includes `Meta` (Windows/Super/Cmd): it is mapped across the
App boundary for no other reason than this rule, since a dropped modifier makes `Win+Enter` look
unmodified to the router while the pane sees it for what it is.

If the insertion is refused (the line cannot be read, a keystroke is still unechoed, the cursor is
mid-line, the text was pasted), `Enter` is *not* consumed and reaches the shell, which submits as it
always did. A refusal must not turn `Enter` into a dead key.

There is still no execute-from-assist action: insertion sends text to the shell's line editor and
stops, and the user presses `Enter` themselves. Insert and execute stay separate.

### Original proposal (historical)

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

Differences from what shipped, and why: `Tab` stays shell-owned (taking the completion key from the
shell is a bigger loss than the convenience is worth); `Enter` inserts rather than executes, and the
"user explicitly navigated there" condition became a state-machine predicate rather than a focus
question, because the assist surface never takes keyboard focus from the terminal; `Ctrl+Enter` is
insert, not execute; `Alt+Enter` is unassigned.

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
Delivered:
- PowerShell, bash, zsh, and fish providers each emitting the normalized
  OSC 7 / OSC 133;A/C/D lifecycle (PowerShell via `-File`, bash via
  `--rcfile`, zsh via `ZDOTDIR` env-override, fish via `XDG_CONFIG_HOME`)
- structured cwd, accepted-command, and completion events with exit code
  and duration enrichment
- base64-encoded accepted-command payloads so multiline submissions survive
- env-override plumbing through `ShellIntegrationLaunchPlan`, `RustPtySession`,
  and the `pty_spawn_with_envs` Rust FFI

Exit criteria met:
- command boundaries are trustworthy in integrated shells; heuristic capture
  remains the fallback for unsupported or user-overridden shell configurations

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
