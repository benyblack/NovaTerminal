# Command Assist V2 Phased Plan

Date: 2026-08-01
Status: Draft for review
Design: `2026-08-01-command-assist-v2-design.md`

**Goal:** Execute the V2 rebuild in six phases, each independently shippable behind the existing `CommandAssistEnabled = false` default, with the flag flip as the final gate.

**Ground rules:** All builds/tests via `scripts/build.ps1` / `scripts/build.sh` (never raw `dotnet`). Each phase ends with the full CommandAssist test suite green plus the phase's new tests. Detailed per-task TDD implementation plans are written per phase at kickoff (M4.2 style); this document fixes scope, order, and exit criteria.

---

## Phase 0 — Foundation refactor (no behavior change)

Closes #114. Pure restructuring; every existing test must pass unmodified (namespace updates aside).

**Status: complete.** Tasks 1, 2 and 7 landed in PR #276 (Phase 0a); tasks 3, 5 and 6 in PR #279
(Phase 0b); task 4 in Phase 0c.

Tasks:
1. **[done — #276]** Create `src/NovaTerminal.CommandAssist/` project (Avalonia-free). Move Domain, Models, Storage, ShellIntegration, ViewModels, Application core.
2. **[done — #276]** Introduce `AssistKey`/`AssistModifiers` abstraction for `CommandAssistKeyRouter`; map from `Avalonia.Input` in `TerminalPane`. Introduce plain geometry records (`AssistPoint/AssistRect/AssistSize`) for `CommandAssistAnchorCalculator`; convert at the App boundary.
3. **[done — #279]** Replace static `CommandAssistInfrastructure` with `CommandAssistServices` composed at the App root and injected into `TerminalPane`. (Injected at the single `MainWindow.WirePane` funnel, so session-restored panes get it too.)
4. **[done — Phase 0c]** Split `CommandAssistController`: `AssistSessionStateMachine` (enum state: Hidden / PassiveBubble / PassivePopup / ExplicitBubble / ExplicitPopup / HistorySearch / Help / FixHint / FixPopup — the sketched six split further because explicitness has to survive a popup round trip and Fix has two popup variants), `AssistSessionContext` (the environment facts that are not session state), `CapturePipeline`, `SuggestionOrchestrator` (`CancellationToken` refresh replacing `_refreshVersion`; the debounce is deliberately deferred to Phase 3, where it is a policy decision rather than a mechanism swap).
5. **[done — #279]** Single ranking: delete `JsonHistoryStore.Score` (stores return candidates), delete dead `HistorySuggestionEngine`, `CommandAssistBarView`, unused ctors, `TryUpdateExitCodeAsync`, `CanExecuteDirectly`.
6. **[done — #279]** Storage: JSONL append + in-memory index + compaction; one-time `history.json` migration; keep `AppJsonContext` source-gen. (One store instance per process: the cap is mutable, never an instance swap.)
7. **[done — #276]** Update `NamespaceAlignmentTests` / architecture tests and `docs/MODULE_OWNERSHIP.md` for the new assembly.

Exit criteria: full suite green; `NovaTerminal.App` references `NovaTerminal.CommandAssist`; no `Avalonia.*` using in the new assembly (enforce with an architecture test); #114 closed.

Known follow-up carried out of Phase 0b: the JSONL store assumes a single process owns the history
file. Multiple NovaTerminal processes sharing one `history.jsonl` is unsupported (the old
stateless whole-file store tolerated it by accident). Needs a file lock or a per-process log
segment before it can be claimed as supported.

## Phase 1 — Truthful query state

**Status: complete.** Tasks 1 and 2 shipped in Phase 1a; task 3 in Phase 1b; tasks 4, 5 and 6 in
Phase 1c; task 7 — raised by the Phase 1c review as a pre-flag-flip requirement — in Phase 1d. The
one item the phase still carries to Phase 6 is the deferred smoke-scenario re-validation in the exit
criteria, which waits on the flag flip because the feature is still default-off.

Tasks:
1. **[done — Phase 1a]** Emit `OSC 133;B` from all four bootstrap builders; extend the four `*BootstrapBuilderTests` and the shell-harness integration tests. Preserve bail-out conditions and bash DEBUG-trap guard semantics.
2. **[done — Phase 1a]** Parser/tracker: wire `133;B` through `AnsiParser` → `ShellLifecycleTracker.HandleCommandStarted` (currently dead) with mark position (row/col).
3. **[done — Phase 1b]** `GridQueryReader`: extract command text between last `B` mark and cursor from the buffer, handling wrapped logical lines and scrolled viewports. Exhaustive buffer-level unit tests first (wrap, resize/reflow, multiline continuation, prompt redraw, cleared screen). *Landed in `NovaTerminal.VT`, not the CommandAssist assembly as sketched here* — the extraction is pure buffer walking and `LayeringTests` forbids CommandAssist from referencing VT; Command Assist consumes it at the App boundary via `TerminalPane.TryGetGridCommandLine`.
4. **[done — Phase 1c]** `SuggestionOrchestrator` consumes `GridQueryReader` when marks are live; delete the shadow buffer (`TextInputObserved`/`BackspaceObserved` mirroring). Heuristic Enter-capture stays for history in non-integrated sessions. *Amended:* the pass resolves its own query (callers no longer hand one in) and reads on its worker rather than on the keystroke, per the PR #285 review's settled-boundary point. Enter-capture stays but its source is now the grid, so it works for an instrumented session's first command and captures **nothing** in a markless one — see the Phase 1c notes below.
5. **[done — Phase 1c]** Degraded mode: no marks → path suggestions + explicit `Ctrl+R` history search only; prefix-dependent features off. *Amended:* with no query the path provider returns nothing, so degraded passive suggestions are empty in practice; `Ctrl+R` shows the recency list and is browse-only.
6. **[done — Phase 1c]** `CommandAssistInsertionPlanner` computes against grid truth; add tests for post-`Ctrl+U`, post-history-recall, post-Tab-completion insertion (the desync cases that broke V1). *Amended:* the planner also gained four refusal rules (no snapshot, cursor off the end, multiline, right prompt trimmed).
7. **[done — Phase 1d]** **Markless capture-only accumulator (ii-strict).** Restore Enter-time history capture for sessions with no `OSC 133` marks, as a *poisoned* line accumulator that lives entirely in `TerminalPane`. See "Phase 1c follow-up" below for the design and the reasoning, and the Phase 1d notes for what shipped.

Exit criteria: desync test matrix green on all four shells; shadow buffer code deleted; smoke scenarios 1–8 re-validated. *Status: the desync matrix is green at three levels (reader, controller seam, real pane driven by escape sequences), the shadow buffer is deleted, and task 7 landed in Phase 1d. Smoke scenarios 1–8 are re-validated at the flag-flip phase, since the feature is still default-off and the M4.2 scenario doc still describes V1 behavior; that is the only Phase 1 item Phase 6 step 1 still has to check.*

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
  short-circuits `CommandStarted` before the Command Assist dispatcher for that reason. That
  early-out survived Phase 1b (the reader takes the mark straight off the parser callback, not
  off the dispatcher); **Phase 1c has to remove it** when the orchestrator starts pulling grid
  truth on the event (`TerminalPane.OnShellIntegrationEventObserved`).

Phase 1b notes (for the task-4 orchestrator author):
- `GridQueryReader.TryReadCommandLine(buffer, mark, out GridCommandLine)` lives in
  `NovaTerminal.VT`; the App-side seam is `TerminalPane.TryGetGridCommandLine(out …)`, which
  pairs it with the newest mark (`_latestCommandStartMark`, kept under a gate because the
  parser callback runs on the PTY read thread). Nothing calls the seam yet.
- `GridCommandLine` carries `Text`, `CursorOffset` (always a valid index into `Text` — the
  cursor is routinely mid-line), `IsMultiline`, `RightPromptTrimmed`, `StartRow`, `EndRow`.
- **The result is only meaningful between `B` and the following `C`.** The reader cannot tell
  "still typing" from "the command ran and this is its output"; lifecycle gating is the
  consumer's job. Two backstops: the pane drops `_latestCommandStartMark` on `133;D`, so the seam
  goes dark between a command finishing and the next prompt (it is deliberately *kept* across
  `133;C`, when the input line is still on screen and still what the mark describes), and a
  `MaxSpanRows` cap (512) bounds the damage for shells that emit `B` without a matching `D`.
- **Multiline is decision (b): raw text, hard breaks as `'\n'`, `IsMultiline` set.** Nothing
  identifies continuation-prompt cells (`PS2`/`PROMPT2`) as prompt rather than input, so they
  are *in* the text. Treat multiline text as opaque — history/display only, never a typed
  prefix. One documented gap: if the cursor sits on an earlier logical line of a continuation
  entry, the span stops at the end of that line and `IsMultiline` stays clear.
- **RPROMPT** is excluded from the final row only, and only when all five hold: content ends
  within 2 columns of the right edge; the gap starts at or after the cursor (nothing left of the
  cursor is ever discarded); the gap is the *widest* blank run in that region, so a multi-segment
  right prompt is trimmed whole rather than cut at its own internal gap; the gap is >= 2 cells
  and strictly wider than the badge it separates; and the badge is at most `Cols / 3` wide. The
  last two are what stop a double space inside typed input that reaches the right edge from
  eating the tail of the line when the cursor is at Home. Unrecognised right prompts are returned
  as extra text; that direction is recoverable, deleting typed input is not.

Phase 1c notes (for the Phase 2 author, and for anyone reading the deleted surface later):
- The consumption contract — lifecycle gate, settled reads, insertion refusals — is written up in
  `docs/command-assist/CommandAssist_ShellIntegration_Gaps.md` under "Added In V2 Phase 1c".
- The query crosses into the CommandAssist assembly as `AssistQuerySnapshot?` through a
  `Func<AssistQuerySnapshot?>` the controller is constructed with. `TerminalPane` supplies it and
  maps `GridCommandLine` to it, because `LayeringTests` forbids CommandAssist from referencing VT.
  Null means "unknown", not "empty", and that distinction is load-bearing in the planner.
- **Deleted:** `CommandAssistController.HandleTextInput(string)`, `HandleBackspace()`,
  `HandlePastedText(string)`; the `ViewModel.QueryText` writes in `TryAcceptSelection` and in the
  typing/paste handlers; the `query` parameter on `SuggestionOrchestrator.Refresh`; the
  `CommandStarted` early-out in `TerminalPane.OnShellIntegrationEventObserved`. **Kept:**
  `AssistSessionStateMachine.IsCurrentSubmissionSuppressed`, because paste suppression is a
  provenance fact the grid cannot reconstruct, not a query fact.
- `TerminalPane.ArmShellIntegrationTracker()` was extracted out of `ApplyShellIntegrationLaunchPlan`
  and is the hook Phase 2 task 3 needs: arming the tracker on *observed* marks (rather than only on
  a launch plan we created) is what makes a user-instrumented remote deliver events at all. Doing
  that also makes the bare-`133;C` edge in the gaps doc reachable, so close it in the same change.
- The `CommandStarted` event now costs one dispatcher hop per prompt repaint. If that ever shows up
  in the Phase 3 benchmark, the fix is to make the gate idempotent at the pane rather than to
  restore the early-out.

### Phase 1c follow-up — task 7: markless capture-only accumulator (ii-strict)

**The hole.** Phase 1c deleted V1's Enter-time history capture along with the shadow buffer, and put
nothing in its place. That is correct for instrumented sessions — the grid is strictly better than
the mirror ever was — but a session with no `OSC 133` marks now captures **nothing**, ever. That is
not a corner: it is `cmd.exe`, every shell whose bootstrap bailed out (`pwsh -File`, `bash -c`,
`zsh -f`, `fish -N`), and **every SSH session**, because SSH launch plans skip provider injection
entirely and Phase 2 task 3 only lifts that for hosts the *user* has instrumented. For all of those,
history stays empty forever, which means `Ctrl+R` is an empty box, Fix has no prior command to
reason about, and ranking has no corpus. Shipping the flag flip in that state would present a
feature that does nothing for a large share of real sessions.

**The design (ii-strict).** A line accumulator in `TerminalPane`, used *only* as the Enter-time
submission text, never as the query:

- `TextInputObserved` appends the text; `BackspaceObserved` chops one character.
- **Any unmodeled key poisons the buffer.** `KeyDownInterceptor` already sees every key the pane
  does not own; arrows, `Home`, `End`, `Delete`, `Tab`, `PgUp`/`PgDn`, F-keys and any `Ctrl`/`Alt`
  chord Command Assist does not own all set the poison flag. Paste poisons.
- `Enter` and `Ctrl+C` reset it (text and flag).
- At `Enter`: `submitted = grid text if available else (poisoned ? null : accumulator)`. Grid truth
  always wins where it exists; the accumulator is consulted only where there is none.

It feeds the existing `HandleEnterAsync(string?)` seam and requires **zero** changes inside
`NovaTerminal.CommandAssist` — the assist assembly keeps its "the host tells me, or nothing" contract
and cannot tell the two sources apart. It is deletable in one commit once Phase 2 gives SSH real
marks.

**Why this is not the desync class coming back.** The PR body's counterargument against restoring
the mirror is sound, and it is an argument about the *unpoisoned* variant. V1's mirror answered
"what is on the line?" with a guess, and its failure mode was **silent wrongness**: the Fix-mode
caption was concatenated into it, Tab completions were truncated to what the user had typed, and
`Ctrl+U` left it holding text that was no longer on screen — all of which were written to permanent
history as commands the user had run. A poisoned accumulator cannot do that. Every edit it cannot
model turns it off, so its only two outcomes are "exactly the characters the user typed, in order"
and "nothing". Failure mode "captures nothing", never "captures something false" — which is the same
bar Phase 1c set for the grid reader, met by a different mechanism.

**Ordering.** Anywhere before the flag flip. It does not block Phase 2, and Phase 2 shrinks the
population it serves (instrumented remotes stop needing it) without emptying it (`cmd.exe` and
bailed-out bootstraps remain).

### Phase 1d notes — what task 7 actually shipped

`MarklessSubmissionAccumulator` (`src/NovaTerminal.App/Controls/`) plus wiring in `TerminalPane`.
Zero changes inside `NovaTerminal.CommandAssist`, as designed: the assist assembly still sees one
`HandleEnterAsync(string?)` and cannot tell the two sources apart.

- **The poison classification is an allow-list, not the deny-list the design sketched.** The
  interceptor is handed `(Key, KeyModifiers)`, and a deny-list of "arrows, Home, End, Delete, Tab,
  page keys, F-keys, unowned chords" fails *open* on anything nobody thought of. Inverted, it fails
  closed: a key press leaves the buffer alone only if it is a modifier key, `Enter` or `Backspace`
  **with no modifiers at all**, `Ctrl+C` (which resets), a key Command Assist consumed, or a
  printable key with no `Ctrl`/`Alt`/`Meta` **or with `Ctrl+Alt`, which on Windows is how Avalonia
  reports `AltGr`**. Everything else poisons. An allow-list missing a harmless key costs one
  capture; a deny-list missing a line-editing key writes a false command to history.
- **Two modifier rules changed in review, both for reasons that live outside this class.**
  - *`Enter`/`Backspace` require `KeyModifiers.None`, not merely "no `Alt`".* With the kitty
    keyboard protocol's disambiguate tier active, `TerminalView` encodes a modified Enter or
    Backspace as CSI u (`Ctrl+Backspace` → `CSI 127;5u`, `Shift+Enter` → `CSI 13;2u`) and returns
    early, so `EnterObserved` / `BackspaceObserved` never fire: the accumulator would keep every
    character while a kitty-aware editor deleted a word. Fail-closed at the cost of one capture per
    `Ctrl+Backspace`; gating on live kitty state instead would make the classification depend on
    terminal state that can change mid-line.
  - *`Ctrl+Alt` plus a **text-producing** key does not poison.* Avalonia reports `AltGr` as
    `Control|Alt`, so the literal rule cost German, French, Nordic, Turkish and Polish users every
    `@`, `{`, `[`, `\`, `|` and `~` — i.e. essentially every capture. It stays fail-closed because
    `TerminalView` sends *nothing* to the PTY for that combination (`EncodeKittyKey` and
    `EncodeAltKey` both return null on the `Control|Alt` pair; the legacy `Ctrl` branch requires
    `!Alt`), so the only route to the shell is the composed `WM_CHAR`, which arrives as
    `OnTextInput` and is appended. `Ctrl+Alt` plus a non-text key still poisons.
- **The echo gate (review blocker).** `TerminalView.OnTextInput` fires per keystroke
  unconditionally, so at a hidden `password:` prompt inside a markless session the accumulator ends
  up clean and holding the password, with no grid snapshot to outrank it — and `SecretsFilter` is
  pattern-based, so a bare secret would reach `history.jsonl`. `TerminalPane` therefore requires the
  accumulated text to be **painted on the grid ending at the cursor** before it is used
  (`GridQueryReader.TryReadTextEndingAtCursor`: cursor row plus soft-wrapped predecessors, under the
  buffer read lock, compared as text so wide characters count once). A visible markless prompt
  always satisfies this — only the `B` mark is missing, not the text — so correct captures pay
  nothing, and every doubt (no buffer, alt screen, unresolved cursor, unlanded or partial echo)
  resolves to no capture.
- **`Ctrl+Shift+P` is only conditionally assist-consumed.** The window's shortcut handler calls
  `TryToggleCommandAssistPinShortcut`, which routes without observing; when nothing can be pinned
  the route returns false and the key travels on to `TerminalView`, where the accumulator sees an
  unowned `Ctrl` chord and poisons. Safe direction, one capture.
- **`TryHandleCommandAssistKey` was split** into the routing decision (`TryRouteCommandAssistKey`,
  unchanged behavior) and the observation, which runs after it and returns nothing. "Command Assist
  consumed this key" and "the shell never saw it" are the same fact, so the routing result is the
  input to the classification.
- **Text that reaches the PTY without going through key handling poisons at its own call site**:
  `Ctrl+Enter` insertion (the one key Command Assist owns that *sends*), both paste paths, the
  drag-and-drop path toast, sibling-pane broadcast, the clipboard-image path, the agent host's
  A3 act surface (`AgentSessionRegistration.InputInjected`, added for this), and parser device
  replies (`AnsiParser.OnResponse` — DA1, DSR, answerback; a reply that lands in a line editor is
  literal input the same way a paste is). The accumulator's poison flag is `volatile`, because
  `InputInjected` fires on whatever thread the agent-host IPC endpoint is serving.
- **Resets**: `Enter` (after the capture read), `Ctrl+C`, any alt-screen transition in either
  direction, and session start / restart / profile switch (`InitializeSessionCore`).
- **Backspace refuses rather than guesses** when the last character is a surrogate or a combining
  mark, because "one character" is then not the same thing to the accumulator and to the shell's
  line editor. On an empty buffer it is a no-op, not a poison. There is an 8 KB cap.
- **Composition with suppression.** They are distinct and both survive. Poison is an accumulator
  fact ("I cannot describe this line"); `IsCurrentSubmissionSuppressed` is a provenance fact ("this
  text was not composed here") that `CapturePipeline` applies to *both* sources, which is why a
  paste is still not captured in an instrumented session where the grid reads it perfectly.
- Tests: `PaneMarklessCaptureTests` (pane level, 40), `MarklessSubmissionAccumulatorTests` (unit,
  42) and the `TryReadTextEndingAtCursor` block in `GridQueryReaderTests` (VT, 10).
  Mutation-checked: un-poisoning arrows fails the poison theory; letting the accumulator win over
  the grid fails the grid-wins test; **removing the echo gate fails the password test** (plus the
  partial-echo and echoed-elsewhere tests); loosening `Enter`/`Backspace` back to "no `Alt`" fails
  the modified-Enter/Backspace tests; dropping the `AltGr` carve-out fails the AltGr theory and the
  end-to-end AltGr capture.

## Phase 2 — Marks-based anchoring + SSH parity

Tasks:
1. ~~Anchor source becomes the last in-viewport `133;A` mark row when present; geometric `CommandAssistPromptHint` heuristic demoted to fallback. `CommandAssistAnchorCalculator` gains a `HasMarkAnchor` input.~~ **Done (Phase 2a).** Shipped against `133;B`, not `133;A`: `A` reaches the App as a bare `OnPromptReady` with no position, while `B` already carries a full `ShellIntegrationMark` and marks the first cell of the user's *input* — the row the bubble actually wants. `ShellMarkAnchorResolver` (VT) converts `AbsoluteRow` → viewport row against the live scroll offset, re-derived on every placement pass; out-of-viewport, generation-stale, evicted and alt-screen marks all resolve to "no anchor" and fall back to the heuristic.
2. ~~Strip the SSH mitigation stack for mark-anchored sessions: no suppression bands, no correction passes (`MaxSshAssistCorrectionPasses` path), no opacity games. Keep a single conservative fallback for markless sessions: lower-band bubble, no popup auto-open.~~ **Done (Phase 2a), by gating rather than deletion.** `IsCommandAssistPromptAnchorReliable` went from "SSH ⇒ false" to "mark ⇒ true, else the old rule"; `ShouldSuppressConservativeRemoteAssist` and `ScheduleCommandAssistPlacementCorrection` both return early for mark-anchored layouts, and the opacity suppression is cleared rather than applied. Nothing was deleted: markless SSH is still a supported session type until task 3 lands the remote snippets, and every one of those paths is still its only placement strategy. "No popup auto-open for markless sessions" was **not** done here — auto-open policy is Phase 3 task 1 and there is no auto-open to withhold yet.
3. Remote integration snippets: `assets/shell-integration/nova-integration.{sh,ps1}` + docs page + "copy install command" in Settings. Runtime detection (`_hasObservedShellIntegrationMarker`) lifts SSH restrictions: capture, fix, trusted anchor on; `FileSystemPathSuggestionProvider` stays off for remote. (Trusted anchoring already follows from task 1 with no session-type check left to lift.)
4. Delete/simplify the band-threshold constants that caused #232's 0.005 sensitivity where marks make them redundant; document surviving thresholds. **Partially done in Phase 2a — read the honest version:** no constant was deleted. All five band ratios (`UnreliableCursorBandStartRatio` 0.55, `PromptUpperBandRatio` 0.45, `ReliableShortPanePromptUpperBandRatio` 0.60, `FallbackShortPanePromptUpperBandRatio` 0.70, `ConservativeRemotePromptBandStartRatio` 0.55) are still live on the markless path, which is still reachable. What changed is that the mark path consults none of them: a known row needs a fit test, not a ratio. They become deletable when markless sessions stop being a supported anchor source, which is not this phase.

Exit criteria: SSH + instrumented remote passes smoke scenarios with zero `[Corrected]` diagnostics; markless SSH degrades gracefully; anchor calculator tests cover both anchor sources.

## Phase 3 — Visible usefulness

Tasks:
1. Auto-open policy v2: passive bubble with top-1 merged suggestion after >=2 chars, ~75 ms debounce; Escape suppresses for current command; popup still intent-only. Policy behind `CommandAssistPassiveBubbleEnabled` (default true when master flag on); M4.3-quiet behavior as fallback.
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
3. `CommandKnowledgeService` with ordered sources: (a) build-time tldr-pages-derived catalogue (>=200 commands, <2 MB, CC-BY attribution in About), (b) local probing (`man -w`, `Get-Help`, `--help`) for "open full help" actions. Replaces `LocalCommandDocsProvider` + `SeedRecipeProvider`; closes #250.
4. Snippet management UI in Settings (list/edit/delete; `ISnippetStore.RemoveAsync` gets a caller).

Exit criteria: Fix mode demo across failure classes; Help useful for arbitrary common commands; catalogue size + attribution verified.

## Phase 5 — AI seam (interfaces only)

Tasks:
1. `IAssistContentProvider` + `AssistCapabilities` + request/response records in the CommandAssist assembly; redaction enforced in the orchestrator before any provider call.
2. Adapt `HeuristicErrorInsightService` and `CommandKnowledgeService` to implement the interface; orchestrator queries providers uniformly.
3. Settings + empty states for unconfigured capabilities ("AI assist not configured"). No network code, no provider implementations.

Exit criteria: local providers run through the seam with zero behavior change; architecture test forbids network references in the assembly.

## Phase 6 — Flag flip

1. Verify all six re-enable criteria from the design doc, plus the one item Phase 1 carried here:
   smoke scenarios 1–8 re-validated. (Phase 1's other carried item, task 7's markless capture-only
   accumulator, landed in Phase 1d.)
2. Update `CommandAssistDefaultDisabledTests` → `CommandAssistDefaultEnabledTests`; flip `TerminalSettings.CommandAssistEnabled = true`; changelog + docs refresh (`CommandAssist.md` rewritten to match V2 reality, §14 keyboard table fixed).
3. New smoke checklist executed on Windows + Unix-over-SSH; record results in `docs/command-assist/`.

---

## Sequencing notes

- Phases 0–2 are strictly ordered (each builds on the previous). Phases 3 and 4 are independent of each other and can interleave after Phase 2; Phase 5 needs Phase 4's providers.
- The M4.3 plan docs (`2026-03-11-command-assist-m4-3-*.md`) are implemented but still marked "In progress" — mark them Completed when Phase 0 lands to stop the status drift.
- Known-stale figures: issue #114's "~12 bools" is actually 7, and both July triage docs repeat #114's 4,766 LOC / 959-line-controller numbers; measured at `c083803` it is ~4,192 LOC / 854 lines. Do not re-quote the stale pair when scoping.
