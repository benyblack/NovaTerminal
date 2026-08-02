# Command Assist V2 Phased Plan

Date: 2026-08-01
Status: Draft for review
Design: `2026-08-01-command-assist-v2-design.md`

**Goal:** Execute the V2 rebuild in six phases, each independently shippable behind the existing `CommandAssistEnabled = false` default, with the flag flip as the final gate.

**Ground rules:** All builds/tests via `scripts/build.ps1` / `scripts/build.sh` (never raw `dotnet`). Each phase ends with the full CommandAssist test suite green plus the phase's new tests. Detailed per-task TDD implementation plans are written per phase at kickoff (M4.2 style); this document fixes scope, order, and exit criteria.

---

## Phase 0 — Foundation refactor (no behavior change)

Closes #114. Pure restructuring; every existing test must pass unmodified (namespace updates aside).

**Status: task 4 outstanding; everything else has shipped.** Tasks 1, 2 and 7 landed in PR #276
(Phase 0a); tasks 3, 5 and 6 in PR #279 (Phase 0b).

Tasks:
1. **[done — #276]** Create `src/NovaTerminal.CommandAssist/` project (Avalonia-free). Move Domain, Models, Storage, ShellIntegration, ViewModels, Application core.
2. **[done — #276]** Introduce `AssistKey`/`AssistModifiers` abstraction for `CommandAssistKeyRouter`; map from `Avalonia.Input` in `TerminalPane`. Introduce plain geometry records (`AssistPoint/AssistRect/AssistSize`) for `CommandAssistAnchorCalculator`; convert at the App boundary.
3. **[done — #279]** Replace static `CommandAssistInfrastructure` with `CommandAssistServices` composed at the App root and injected into `TerminalPane`. (Injected at the single `MainWindow.WirePane` funnel, so session-restored panes get it too.)
4. Split `CommandAssistController` (854 L): `AssistSessionStateMachine` (enum state: Hidden / PassiveBubble / PopupBrowse / Search / Help / Fix — replaces the 7 bools), `CapturePipeline`, `SuggestionOrchestrator` (debounce + `CancellationToken` refresh, replacing `_refreshVersion`).
5. **[done — #279]** Single ranking: delete `JsonHistoryStore.Score` (stores return candidates), delete dead `HistorySuggestionEngine`, `CommandAssistBarView`, unused ctors, `TryUpdateExitCodeAsync`, `CanExecuteDirectly`.
6. **[done — #279]** Storage: JSONL append + in-memory index + compaction; one-time `history.json` migration; keep `AppJsonContext` source-gen. (One store instance per process: the cap is mutable, never an instance swap.)
7. **[done — #276]** Update `NamespaceAlignmentTests` / architecture tests and `docs/MODULE_OWNERSHIP.md` for the new assembly.

Exit criteria: full suite green; `NovaTerminal.App` references `NovaTerminal.CommandAssist`; no `Avalonia.*` using in the new assembly (enforce with an architecture test); #114 closed.

Known follow-up carried out of Phase 0b: the JSONL store assumes a single process owns the history
file. Multiple NovaTerminal processes sharing one `history.jsonl` is unsupported (the old
stateless whole-file store tolerated it by accident). Needs a file lock or a per-process log
segment before it can be claimed as supported.

## Phase 1 — Truthful query state

**Status: tasks 1 and 2 shipped (Phase 1a).**

Tasks:
1. **[done — Phase 1a]** Emit `OSC 133;B` from all four bootstrap builders; extend the four `*BootstrapBuilderTests` and the shell-harness integration tests. Preserve bail-out conditions and bash DEBUG-trap guard semantics.
2. **[done — Phase 1a]** Parser/tracker: wire `133;B` through `AnsiParser` → `ShellLifecycleTracker.HandleCommandStarted` (currently dead) with mark position (row/col).
3. `GridQueryReader` in the new assembly: extract command text between last `B` mark and cursor from the buffer, handling wrapped logical lines and scrolled viewports. Exhaustive buffer-level unit tests first (wrap, resize/reflow, multiline continuation, prompt redraw, cleared screen).
4. `SuggestionOrchestrator` consumes `GridQueryReader` when marks are live; delete the shadow buffer (`TextInputObserved`/`BackspaceObserved` mirroring). Heuristic Enter-capture stays for history in non-integrated sessions.
5. Degraded mode: no marks → path suggestions + explicit `Ctrl+R` history search only; prefix-dependent features off.
6. `CommandAssistInsertionPlanner` computes against grid truth; add tests for post-`Ctrl+U`, post-history-recall, post-Tab-completion insertion (the desync cases that broke V1).

Exit criteria: desync test matrix green on all four shells; shadow buffer code deleted; smoke scenarios 1–8 re-validated.

Phase 1a notes (for the task-3 `GridQueryReader` author):
- The B mark is carried as `ShellIntegrationEvent.MarkPosition` (`ShellMarkPosition`: `Row`,
  `Column`, `AbsoluteRow`, `IsAltScreen`, `Generation`). `Row` is the buffer's current row index
  and goes stale on scrollback eviction; `AbsoluteRow` (`TotalRowsEvicted + Row`) is the stable
  identity and re-derives the live row as `AbsoluteRow - TotalRowsEvicted`.
- Staleness has **two** cases and only one of them shows up in the row number:
  - *Eviction* — `TotalRowsEvicted` only grows, so `AbsoluteRow - TotalRowsEvicted` goes
    negative and the mark is visibly dead.
  - *Coordinate-space reset* — `ScrollbackPages.Clear()` zeroes **both** counters. It is
    reached from CSI 3J (what `clear(1)` sends with the `E3` capability — i.e. routinely),
    RIS, the user's clear-buffer action, and reflow. Afterwards a pre-reset `AbsoluteRow`
    resolves to a large *positive* row holding unrelated content, with nothing anomalous about
    it. `Generation` (`ScrollbackPages.Generation`, a process-global monotonic epoch bumped in
    `Clear()` and on construction) is the detector: **the reader must reject any mark whose
    `Generation` differs from `buffer.Scrollback.Generation`, and only then treat a negative
    derived row as "aged out".**
- B rides inside PS1/PROMPT/`fish_prompt`/the pwsh prompt string, so every prompt repaint
  (including post-resize and post-clear) re-emits it with fresh coordinates. The reader should
  treat the newest mark as truth rather than caching one.
- The controller still ignores `CommandStarted`; nothing consumes the position yet. The pane
  short-circuits `CommandStarted` before the Command Assist dispatcher for that reason — Phase
  1b has to remove that early-out (`TerminalPane.OnShellIntegrationEventObserved`).

## Phase 2 — Marks-based anchoring + SSH parity

Tasks:
1. Anchor source becomes the last in-viewport `133;A` mark row when present; geometric `CommandAssistPromptHint` heuristic demoted to fallback. `CommandAssistAnchorCalculator` gains a `HasMarkAnchor` input.
2. Strip the SSH mitigation stack for mark-anchored sessions: no suppression bands, no correction passes (`MaxSshAssistCorrectionPasses` path), no opacity games. Keep a single conservative fallback for markless sessions: lower-band bubble, no popup auto-open.
3. Remote integration snippets: `assets/shell-integration/nova-integration.{sh,ps1}` + docs page + "copy install command" in Settings. Runtime detection (`_hasObservedShellIntegrationMarker`) lifts SSH restrictions: capture, fix, trusted anchor on; `FileSystemPathSuggestionProvider` stays off for remote.
4. Delete/simplify the band-threshold constants that caused #232's 0.005 sensitivity where marks make them redundant; document surviving thresholds.

Exit criteria: SSH + instrumented remote passes smoke scenarios with zero `[Corrected]` diagnostics; markless SSH degrades gracefully; anchor calculator tests cover both anchor sources.

## Phase 3 — Visible usefulness

Tasks:
1. Auto-open policy v2: passive bubble with top-1 merged suggestion after ≥2 chars, ~75 ms debounce; Escape suppresses for current command; popup still intent-only. Policy behind `CommandAssistPassiveBubbleEnabled` (default true when master flag on); M4.3-quiet behavior as fallback.
2. Bind `ShortcutHintText` in `CommandAssistBubbleView`; verify content reflects rebound shortcuts.
3. Popup interactivity: rows become selectable (mouse hover + click-to-accept), `ScrollViewer` + scroll-into-view, remove hard-coded `maxResults: 5` in favor of scrolling cap.
4. Shortcuts: move pin off `Ctrl+Shift+P` to a new catalogued binding; add `Esc`/`Up`/`Down`/`Ctrl+Enter` to catalog under `ShortcutScope.CommandAssist`; migration for existing shortcut config.
5. Gating decoupled: master flag alone gates the feature; history flag gates capture only. Wire `CommandAssistAutoHideInAltScreen` for real or delete it (decide at phase kickoff).
6. Settings UI group: master, history + Clear history button (`IHistoryStore.ClearAsync` finally called), shell integration, passive bubble.
7. Perf benchmark (new, spec §12 targets): first-paint <16 ms, incremental <30 ms, no typing jank; wire into CI as a regression guard.

Exit criteria: type-two-chars-see-value demo works on all four shells; benchmark in CI; updated smoke checklist passes.

## Phase 4 — Real content

Tasks:
1. Stderr capture: buffer the `133;C`→`133;D` output region (bounded: last 40 lines / 8 KB), redact via `SecretsFilter`, populate `CommandFailureContext.ErrorOutput` (removes the hard-coded `null` at the TerminalPane call site).
2. Expand `HeuristicErrorInsightService`: per-shell command-not-found patterns, permission-denied, `./` hint (now reachable), git/docker/npm/dotnet failure signatures. Target: useful suggestion for top-10 failure classes.
3. `CommandKnowledgeService` with ordered sources: (a) build-time tldr-pages-derived catalogue (≥200 commands, <2 MB, CC-BY attribution in About), (b) local probing (`man -w`, `Get-Help`, `--help`) for "open full help" actions. Replaces `LocalCommandDocsProvider` + `SeedRecipeProvider`; closes #250.
4. Snippet management UI in Settings (list/edit/delete; `ISnippetStore.RemoveAsync` gets a caller).

Exit criteria: Fix mode demo across failure classes; Help useful for arbitrary common commands; catalogue size + attribution verified.

## Phase 5 — AI seam (interfaces only)

Tasks:
1. `IAssistContentProvider` + `AssistCapabilities` + request/response records in the CommandAssist assembly; redaction enforced in the orchestrator before any provider call.
2. Adapt `HeuristicErrorInsightService` and `CommandKnowledgeService` to implement the interface; orchestrator queries providers uniformly.
3. Settings + empty states for unconfigured capabilities ("AI assist not configured"). No network code, no provider implementations.

Exit criteria: local providers run through the seam with zero behavior change; architecture test forbids network references in the assembly.

## Phase 6 — Flag flip

1. Verify all six re-enable criteria from the design doc.
2. Update `CommandAssistDefaultDisabledTests` → `CommandAssistDefaultEnabledTests`; flip `TerminalSettings.CommandAssistEnabled = true`; changelog + docs refresh (`CommandAssist.md` rewritten to match V2 reality, §14 keyboard table fixed).
3. New smoke checklist executed on Windows + Unix-over-SSH; record results in `docs/command-assist/`.

---

## Sequencing notes

- Phases 0–2 are strictly ordered (each builds on the previous). Phases 3 and 4 are independent of each other and can interleave after Phase 2; Phase 5 needs Phase 4's providers.
- The M4.3 plan docs (`2026-03-11-command-assist-m4-3-*.md`) are implemented but still marked "In progress" — mark them Completed when Phase 0 lands to stop the status drift.
- Known-stale figures: issue #114's "~12 bools" is actually 7, and both July triage docs repeat #114's 4,766 LOC / 959-line-controller numbers; measured at `c083803` it is ~4,192 LOC / 854 lines. Do not re-quote the stale pair when scoping.
