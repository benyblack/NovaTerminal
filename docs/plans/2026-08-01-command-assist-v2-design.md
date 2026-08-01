# Command Assist V2 Design

Date: 2026-08-01
Status: Draft for review
Supersedes: `docs/command-assist/CommandAssist.md` M5 section; updates the M4.x behavior baseline.
Related issues: #114 (extract assembly), #250 (recipe catalogue), #232 (closed; threshold sensitivity remains).

## Goal

Re-establish Command Assist as a feature worth enabling by default: visibly useful while typing, correct about what the user has actually typed, functional over SSH, and backed by real content instead of a 7-entry demo catalogue — while keeping the foundations that already work (shell integration bootstraps, history capture + redaction, the M4.2 bubble/popup UX model).

This is a core rebuild, not a UX rewrite and not a from-scratch reimplementation.

## Why it was disabled (assessment)

Commit `4e2e6b8` (PR #90, 2026-06-03) disabled `CommandAssistEnabled` by default for 0.3 with the rationale "the feature isn't in good shape yet" — no defect list. The recheck on 2026-08-01 identified the concrete causes:

1. **Nearly silent by default.** The M4.3 auto-open policy shows only filesystem path rows during passive typing; history and snippets require `Ctrl+Space` / `Ctrl+R`. A user who enables the feature and types normally sees almost nothing. This is the single biggest "not usable" driver, and it was a deliberate policy choice, not a bug.
2. **The shadow query buffer desyncs.** `CommandAssistController` mirrors only TextInput/Backspace/Enter/Paste. Arrow keys, `Ctrl+U`/`Ctrl+W`, shell history recall, and the shell's own Tab completion all silently desync it — and ranking, insertion planning (`CommandAssistInsertionPlanner` requires strict prefix-extension), and help-token extraction are all built on it. This is the deepest correctness flaw.
3. **SSH is a second-class citizen by construction.** Shell integration injection is local-only, so SSH panes get heuristic capture and a distrusted prompt anchor (`IsCommandAssistPromptAnchorReliable` returns false for every SSH pane). The 2026-03-11/12 history shows ~20 commits fighting SSH anchor jitter; the surviving mitigation stack (conservative fallback band, suppression heuristics, up to 6 opacity-0 correction passes) is fragile — issue #232 showed the band threshold is sensitive to 0.005 depending on font metrics.
4. **Content is thin and Fix mode is inert.** `LocalCommandDocsProvider` and `SeedRecipeProvider` cover 7 command tokens. `TerminalPane` hard-codes `ErrorOutput: null` (TerminalPane.axaml.cs:2844), so two of `HeuristicErrorInsightService`'s three branches are unreachable; what remains is Levenshtein correction against a 7-entry command list.
5. **UI gaps.** No mouse selection or click-to-accept (rows are an `ItemsControl`), no popup scrolling, the bubble's shortcut hint strip (`ShortcutHintText`) is never bound, and `Ctrl+Shift+P` is both command-palette and pin-toggle.
6. **Architecture debt.** 854-line god controller with 7 mode/state bools, static mutable `CommandAssistInfrastructure` service locator, ranking duplicated between engine and store, `JsonHistoryStore` rewriting the whole file per append, refresh via version counter instead of `CancellationToken`, no debounce.

What is worth keeping: the four shell-integration bootstraps (OSC 133/OSC 7, base64 command payloads), history capture with secret redaction and provenance, the M4.2 prompt-adjacent bubble/popup model and `CommandAssistAnchorCalculator` geometry, shell-first Tab ownership, and 276 passing tests.

## Design pillars

### Pillar 1 — Truthful command-line state (replaces the shadow buffer)

The terminal already renders the ground truth. V2 reads the query from the buffer instead of mirroring keystrokes:

- **Emit `OSC 133;B`** (command-start / prompt-end) from all four bootstraps. Today no bootstrap emits it and `ShellIntegrationEventType.CommandStarted` is dead code.
- **`GridQueryReader`** (new): given the last `B` mark position and the current cursor, extract the live command text from the terminal buffer, handling wrapped logical lines. Every editing action the shell performs — arrows, `Ctrl+U`, history recall, native Tab completion — updates the grid, so the reader is always right.
- **Insertion stays additive.** `CommandAssistInsertionPlanner` keeps the suffix-only rule, now computed against grid truth instead of a guess.
- **Degraded mode** when no integration marks are present (non-integrated local shells, un-instrumented remotes): path suggestions and explicit history search only. The shadow buffer is deleted, not kept as a fallback — degraded mode simply does not offer prefix-dependent features.

### Pillar 2 — Marks-based anchoring (fixes SSH placement)

Anchor reliability today is a per-session-type guess. In V2 it is a per-prompt fact:

- When a `133;A` (prompt-ready) mark exists in the viewport, the prompt row is *known* — anchor to it directly, regardless of local vs SSH.
- The geometric heuristic (`CommandAssistPromptHint` + band thresholds) becomes the fallback for un-instrumented sessions only.
- The SSH conservative-fallback stack (suppression bands, correction passes, opacity games) shrinks to: marks present → trusted anchor; marks absent → bubble in the safe lower band, no popup auto-open, no correction passes.

### Pillar 3 — Remote integration

Injection cannot cross SSH, but marks can:

- Ship `nova-shell-integration.{sh,ps1}` as installable snippets (documented, plus a "copy install command" affordance in Settings) that users source on remote hosts. The parser already consumes OSC 133/7 from any source; capture, cwd, exit codes, and trusted anchoring then work identically over SSH.
- Detect remote marks at runtime (`_hasObservedShellIntegrationMarker` already exists) and lift SSH restrictions dynamically: `FileSystemPathSuggestionProvider` stays disabled for remote (local FS is wrong), but history capture, fix mode, and trusted anchoring turn on.

### Pillar 4 — Visible usefulness (auto-open policy v2)

Replace "silent unless summoned" with "quiet but present":

- **Passive typing:** after ≥ 2 typed characters (debounced ~75 ms), the bubble shows the top-1 merged suggestion (history + path). No popup. Escape hides it for the rest of the command.
- **Popup** still requires intent: `Down`/browse, `Ctrl+Space`, `Ctrl+R`, or high-confidence Fix.
- **Hint strip:** bind the existing `ShortcutHintText` in the bubble so the shortcuts are learnable in-product.
- **Optional ghost text (stretch):** fish-style inline dim suggestion rendered as an overlay at the cursor position (never into grid content), off by default on shells with native predictors (pwsh + PSReadLine predictions).
- Alt-screen behavior unchanged (hide immediately); `CommandAssistAutoHideInAltScreen` is either wired for real or removed from settings — no more phantom flag.

### Pillar 5 — Real content

- **Fix mode gets stderr.** Capture the output region between `133;C` and `133;D` marks (bounded, e.g. last 40 lines / 8 KB, redacted). On non-zero exit, populate `CommandFailureContext.ErrorOutput` for real. Expand `HeuristicErrorInsightService`: per-shell command-not-found message patterns, permission-denied, `./` invocation, git/docker/npm common failure signatures.
- **`CommandKnowledgeService`** (new) replaces the hard-coded docs/recipe providers with pluggable sources, queried in order: (a) bundled offline catalogue generated from tldr-pages (hundreds of commands, CC-BY, regenerable at build time), (b) local probing (`Get-Help`, `man -w`, `--help` availability) for "open full help" actions, (c) the AI provider seam (Pillar 6) when configured. Seed recipes (#250) fold into the bundled catalogue.
- **Snippets become manageable:** list/edit/delete UI in Settings; `ISnippetStore.RemoveAsync` and `IHistoryStore.ClearAsync` finally get callers (the latter satisfies the spec's privacy requirement for user-clearable history).

### Pillar 6 — AI seam (design now, build later)

Per ROADMAP.md, Command Assist is "the only user-facing AI-adjacent surface." V2 defines the seam without shipping a provider:

```csharp
public interface IAssistContentProvider
{
    AssistCapabilities Capabilities { get; }   // Explain | SuggestFix | NlToCommand | EnrichDocs
    Task<AssistContentResult> QueryAsync(AssistContentRequest request, CancellationToken ct);
}
```

- `AssistContentRequest` carries only redacted data (`SecretsFilter` runs before the seam, always), plus shell kind, cwd, exit code, bounded stderr excerpt.
- Providers are opt-in per capability in Settings; empty states ("AI assist not configured") are part of V2 UI.
- Local heuristics (`HeuristicErrorInsightService`, `CommandKnowledgeService`) implement the same interface, so the orchestrator has one code path.
- No network code, no API clients, no model selection in V2 — that is a separate milestone with its own design.

### Pillar 7 — Architecture

- **Extract `NovaTerminal.CommandAssist` assembly** (#114). Blockers are exactly two files: `CommandAssistKeyRouter` (introduce an `AssistKey`/`AssistModifiers` abstraction mapped from `Avalonia.Input` at the App boundary) and `CommandAssistAnchorCalculator` (introduce plain geometry records). Domain, Models, Storage, ShellIntegration, ViewModels move as-is.
- **Kill the static service locator.** `CommandAssistInfrastructure` becomes a composed `CommandAssistServices` built once at the App composition root and passed to panes.
- **Split the controller** along the seams it already has: `AssistSessionStateMachine` (enum-based state per #114: Hidden / PassiveBubble / PopupBrowse / Search / Help / Fix), `CapturePipeline` (heuristic + structured history capture, dedup, enrichment), `SuggestionOrchestrator` (scope resolution, debounced `CancellationToken`-based refresh, single ranking).
- **One ranking implementation.** Stores return candidates; only `CommandAssistSuggestionEngine` scores. Delete `JsonHistoryStore.Score` and the dead `HistorySuggestionEngine`.
- **Storage:** append-only JSONL with in-memory index and periodic compaction (AOT-safe via `AppJsonContext`), replacing whole-file rewrite per command. Same on-disk directory, one-time migration from `history.json`.
- **Delete dead surface:** `CommandAssistBarView`, unused ctors, `TryUpdateExitCodeAsync` shim, `CanExecuteDirectly` (until an execute action actually exists).

## Keyboard and gating decisions

- `Tab` remains shell-owned; `Ctrl+Enter` remains insert-only. No execute-from-assist action in V2 (revisit with AI milestone).
- Pin/unpin moves off `Ctrl+Shift+P` (collides with command palette) to its own catalogued, rebindable binding. `Esc`/`Up`/`Down`/`Ctrl+Enter` enter the shortcut catalog as rebindable entries under `ShortcutScope.CommandAssist`.
- Gating decoupled: `CommandAssistEnabled` alone gates the feature; `CommandAssistHistoryEnabled` gates only capture/history suggestions (today history-off silently kills the whole UI).
- Settings UI grows from one checkbox to a small group: master toggle, history capture + Clear history, shell integration, passive bubble on/off, ghost text (if built).
- Update `docs/command-assist/CommandAssist.md` §14 to match shipped keyboard reality (stale since `acd1cd0`).

## Re-enable-by-default criteria

Flip `CommandAssistEnabled` to `true` only when all of:

1. Grid-truth query reading live for all four integrated shells; shadow buffer deleted.
2. Marks-based anchoring live; SSH with instrumented remote shows no placement corrections in the diagnostic log across the smoke scenarios.
3. Passive bubble policy shipped with kill switch; no measurable typing latency regression (spec targets: <16 ms first paint, <30 ms incremental, no input jank — add a benchmark, which has never existed).
4. Fix mode produces a useful suggestion for the top-10 common failure classes with real stderr.
5. Knowledge catalogue ≥ 200 commands.
6. Smoke checklist (updated from `CommandAssist_SmokeTest_Scenarios_2026-03-14.md`) passes on Windows + one Unix shell over SSH.

## Risks

- **Grid reading of wrapped/multiline input** is the hardest new correctness surface — mitigate with exhaustive buffer-level unit tests before wiring to the controller (same approach that made `CommandAssistAnchorCalculator` solid).
- **Bootstrap changes regress user shells** — the existing bail-out conditions and prompt-preservation tests must extend to the `133;B` additions; keep the one-shot DEBUG-trap guard semantics in bash.
- **Passive bubble may still feel noisy** — ship behind a sub-setting with the M4.3-quiet behavior as the fallback policy; decide on telemetry-free dogfooding.
- **tldr-derived catalogue licensing/size** — CC-BY attribution in About; generate at build time to a compact binary/JSON form; budget < 2 MB.
- **Scope creep toward AI** — the seam is interfaces + empty states only; any provider implementation is explicitly out of V2.

## Out of scope for V2

AI provider implementations and network access; natural-language command mode (Mode E); execute-from-assist; per-profile privacy opt-outs beyond the existing redaction + new Clear history (tracked for a follow-up); telemetry.
