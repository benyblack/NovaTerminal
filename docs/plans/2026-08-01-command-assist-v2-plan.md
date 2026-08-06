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
3. ~~Remote integration snippets: `assets/shell-integration/nova-integration.{sh,ps1}` + docs page + "copy install command" in Settings. Runtime detection (`_hasObservedShellIntegrationMarker`) lifts SSH restrictions: capture, fix, trusted anchor on; `FileSystemPathSuggestionProvider` stays off for remote. (Trusted anchoring already follows from task 1 with no session-type check left to lift.)~~ **Done (Phase 2b).** Three snippets rather than two — `nova-shell-integration.{sh,fish,ps1}`, where the `.sh` dispatches bash/zsh at load time and fish gets its own file because its syntax is not POSIX sh. Shipped as files under `assets/shell-integration/` and embedded into `NovaTerminal.CommandAssist` for the Settings copy action (`RemoteShellIntegrationSnippets`). The Settings affordance copies the **whole snippet**, not a generated one-liner: a heredoc installer cannot be written in fish, a base64 one cannot be read before it is run, and the snippet's own header comment carries the two install commands so they survive the trip to the remote host. Runtime detection landed as a pane-side latch (`TerminalPane._hasObservedShellIntegrationMark`, fed back through `UpdateCommandAssistContext`) *plus* an independent deduction on the assist side (`AssistSessionContext.IsShellIntegrationLive`); both are needed, see the PR body. Arming is unconditional for SSH panes rather than lazy-on-first-mark, because a tracker armed after the first `133;B` would miss the mark that opens the first command-input window.
4. Delete/simplify the band-threshold constants that caused #232's 0.005 sensitivity where marks make them redundant; document surviving thresholds. **Partially done in Phase 2a — read the honest version:** no constant was deleted. All five band ratios (`UnreliableCursorBandStartRatio` 0.55, `PromptUpperBandRatio` 0.45, `ReliableShortPanePromptUpperBandRatio` 0.60, `FallbackShortPanePromptUpperBandRatio` 0.70, `ConservativeRemotePromptBandStartRatio` 0.55) are still live on the markless path, which is still reachable. What changed is that the mark path consults none of them: a known row needs a fit test, not a ratio. They become deletable when markless sessions stop being a supported anchor source, which is not this phase. **Phase 2b did not change that either, and the reason is worth stating rather than deferring again:** shipping the snippets does not make markless SSH unreachable, it makes it *opt-out-able*. A host the user has not instrumented — which is every host until they do — still needs a placement strategy, and so does `cmd.exe` and every bailed-out bootstrap. The constants stay live and stay documented here until markless placement is deleted outright, which needs a decision this plan does not make.

Phase 2b notes (the parts that were not in the task line):

- **The bare-`133;C` edge is closed**, as Phase 1c's note asked. `AnsiParser` now raises `OnCommandAccepted` for every `C`, with `null` text when the payload is absent or unusable, so `C` reliably closes the query gate on third-party integrations. Payload classification: base64 when it decodes to plausible text, plain text when it does not decode but is printable and is not a FinalTerm `key=value` attribute, `null` otherwise. `CapturePipeline` sets `HasObservedStructuredCommandCaptureMarker` only for a `C` **carrying text** — a bare-`C` shell must keep the heuristic path running or it captures nothing at all.
- **`HandleCommandAssistCompletionAsync` is now keyed on the tracker, not on `_isShellIntegrationActive`.** For a remote session the latter is false while the tracker is armed, and the old condition would have run the host-side exit-code patch and the structured one against the same entry on every SSH command, losing the duration.
- Tests: `Osc133AcceptedPayloadTests` (VT), `RemoteShellIntegrationSnippetTests` and the bare-C block in `CapturePipelineTests` (assist), `PaneRemoteShellIntegrationTests` (pane, SSH end-to-end).

Exit criteria: SSH + instrumented remote passes smoke scenarios with zero `[Corrected]` diagnostics; markless SSH degrades gracefully; anchor calculator tests cover both anchor sources.

## Phase 3 — Visible usefulness

**Status: complete.** Tasks 2 and 3 shipped in Phase 3a (PR #290, hotfix #291) along with the three
owner reports pulled forward into it; tasks 1, 4, 5, 6 and 7 in Phase 3b.

**Split into 3a and 3b after owner dogfooding.** Phases 0–2 were merged and tested by the product
owner, who reported three things that no task in this phase covered: `Ctrl+R` listed history but
"does no action when I select" (local *and* SSH); the list mixed every session's and tab's commands
together; and in a tab split into two SSH panes the assist did not appear on one of them at all.
Those are the whole of what a user sees, so they were pulled forward into **Phase 3a** together with
the two tasks below that they touch (2, and 3 in full). Everything else is **Phase 3b**.

Tasks:
1. ~~Auto-open policy v2: passive bubble with top-1 merged suggestion after >=2 chars, ~75 ms debounce; Escape suppresses for current command; popup still intent-only. Policy behind `CommandAssistPassiveBubbleEnabled` (default true when master flag on); M4.3-quiet behavior as fallback.~~ **Done (Phase 3b), and it is a policy reversal rather than a feature addition — worth naming as such.** The passive Suggest scope was paths-only, with a comment in `SuggestionOrchestrator` arguing that unasked-for history rows were V1's noisiest part; that argument is M4.3's and the design doc reverses it deliberately. Three mechanisms carry the noise concern instead of the scope: the two-character floor (`MinPassiveQueryLength`, checked on the *query the pass read off the grid* rather than on a keystroke count, so a `Ctrl+U` or a history recall is measured correctly), the debounce, and Escape. Escape needed a new suppression concept — `AssistSessionStateMachine.IsPassiveSurfaceSuppressed`, set by a new `DismissForCurrentCommand()` transition and cleared only by `CompleteSubmission()` — because the existing `Dismiss()` is also what an accept and a host teardown call, and neither of those means "not on this line". Snippets stayed out of the passive scope: a one-row bubble whose content alternates between a hand-pinned snippet and a ranked history match is unpredictable for no gain. The debounce is injectable (`Func<TimeSpan, CancellationToken, Task>`) so the coalescing is tested as a cancellation property rather than with wall-clock sleeps.
2. ~~Bind `ShortcutHintText` in `CommandAssistBubbleView`; verify content reflects rebound shortcuts.~~ **Done (Phase 3a).** Bound in the bubble *and* repeated in the popup footer. Content is state-dependent rather than a constant, which the task line did not anticipate: after the accept-model change below, a fixed strip would advertise `Enter` in states where the shell owns it. `CommandAssistBarViewModel` computes it from one predicate (`AssistSessionStateMachine.AllowsAcceptOnEnter`) that the key router also consults, via a probe the controller installs — so the hint and the routing cannot disagree. "Reflects rebound shortcuts" is **not** done, because the bindings are not in the shortcut catalogue yet; that is task 4.
3. ~~Popup interactivity: rows become selectable (mouse hover + click-to-accept), `ScrollViewer` + scroll-into-view, remove hard-coded `maxResults: 5` in favor of scrolling cap.~~ **Done (Phase 3a).** Hover via `:pointerover`, single click selects, double click (or a click on the already-selected row) accepts through the same gate `Ctrl+Enter` uses. `ScrollViewer` + `ContainerFromIndex(...).BringIntoView()` on selection change; cap 5 → 50 with the history recall pool widened to 200. Two things the task line missed: the rows had to stop being immutable records (a rebuild per selection move destroys the containers under the pointer), and `CommandAssistOverlayHost` had `IsHitTestVisible="False"`, which excludes the whole subtree — no click could ever have reached a row. Deliberately *not* a `ListBox`: it would take keyboard focus off `TerminalView` on the first click.
4. ~~Shortcuts: move pin off `Ctrl+Shift+P` to a new catalogued binding; add `Esc`/`Up`/`Down`/`Ctrl+Enter` to catalog under `ShortcutScope.CommandAssist`; migration for existing shortcut config.~~ **Done (Phase 3b), six entries not five — plain `Enter` as the note anticipated.** Pin went to `Ctrl+Shift+S`, not the design doc's suggested `Ctrl+Alt+P`: `TerminalView.HandleKeyDownCore` encodes any `Alt`+key as an ESC-prefixed sequence for the PTY and marks the event handled, so an `Alt` chord never bubbles to `MainWindow`'s shortcut handler — the whole `Ctrl+Shift+<letter>` class works precisely because that branch declines `Shift`. Pin also stopped being an assist-routed key: it is dispatched from the window like every other catalogue entry, which is what lets it be bound to the whole key space instead of the five keys `AssistKey` models. The router gained `AssistKeyBindings` + an `AssistKeyAction` result, so `TerminalPane` switches on the resolved action rather than re-deriving it from a second key cascade; a side effect is that modifiers are now matched *exactly* for every in-surface key, where before only `Enter` was (`Ctrl+Down` and `Alt+Up` used to be swallowed). Migration turned out to need no code: `ShortcutBindingResolver` iterates definitions and looks overrides up by id, so new ids take defaults and stale ids are ignored — pinned by a test rather than assumed. The one honest limitation: an override naming a key `AssistKey` does not model falls back to the default, because passing it through as `None` would match either everything or nothing.
5. ~~Gating decoupled: master flag alone gates the feature; history flag gates capture only. Wire `CommandAssistAutoHideInAltScreen` for real or delete it (decide at phase kickoff).~~ **Done (Phase 3b).** `IsCommandAssistFeatureEnabled` is the master flag alone; the history flag became `AssistSessionContext.IsHistoryEnabled` and gates exactly two things, both inside the assist assembly — `CapturePipeline`'s two write paths and `SuggestionOrchestrator.ResolveScope`'s history term. **Decision on the phantom flag: deleted.** Alt-screen hiding is unconditional and correct; a setting whose only effect would be to let the overlay paint over `vim` is a footgun, and wiring it would have meant inventing a use case to justify a field nobody had ever read. Removed from `TerminalSettings`, from the pane's effective-settings copy, and from the MCP `SettingsTools` field lists and docs. Not a breaking change in practice: unknown keys are ignored on load, so an existing `settings.json` carrying it is unaffected.
6. ~~Settings UI group: master, history + Clear history button (`IHistoryStore.ClearAsync` finally called), shell integration, passive bubble.~~ **Done (Phase 3b).** Four rows following the Agent-access group's indented-sub-row convention, with the Phase 2b remote-snippet copy affordance left where it was. Clear history arms on the first click and clears on the second rather than opening a modal — this window has none, and an arm/confirm button cannot be double-clicked through. It goes through the *live* `JsonlHistoryStore` injected by `MainWindow.OpenSettings`, not a second instance over the same file: the store caches an index and a physical line count and compacts from that cache, which is the same argument `CommandAssistServices.ApplyHistoryRetentionLimit` documents for the retention cap.
7. ~~Perf benchmark (new, spec §12 targets): first-paint <16 ms, incremental <30 ms, no typing jank; wire into CI as a regression guard.~~ **Done (Phase 3b), as a tripwire and named as one.** `tests/NovaTerminal.App.Tests/Performance/CommandAssistPerformanceTests.cs`, run by CI with the rest of the suite. The repo *does* have BenchmarkDotNet (`tests/NovaTerminal.Benchmarks`) and this is deliberately not it: that project is not in CI, and the requirement here was a guard that fails loudly on an order-of-magnitude regression. Four measurements with thresholds well above the targets; baselines in the PR body. Two honest caveats recorded in the file: rendering is not measured (a headless test cannot time an Avalonia draw pass, so "first paint" is really "keystroke to view-model"), and the end-to-end figure needs a spin-wait rather than a polling wait, because `Task.Delay(1)` on Windows is ~15.6 ms and a poll made the number read as 15.5 ms regardless of what the code did.

Phase 3a also shipped four things that were not tasks here at all — the three owner reports, plus the
insertion narrowing they forced:

- **`Enter` accepts while browsing.** Accept was `Ctrl+Enter`-only, so `Ctrl+R` → arrow → `Enter`
  submitted the (empty) line and the submission reset dismissed the popup: nothing inserted, surface
  gone. `Enter` is now assist-owned in exactly one state — popup open, row selected, mode Suggest or
  Search, overlay actually rendered — and falls through to the shell when the insertion is refused, so a
  refusal is never a dead key. Documented in `CommandAssist.md` §14, which was rewritten to describe
  shipped reality (the Phase 6 task line asking for that is correspondingly smaller now).
  The PR #290 review added two conditions to this: the rendered-overlay term (a passive popup the pane had
  hidden or dimmed could otherwise own `Enter` at zero pixels) and the arrow asymmetry — `Down` browses
  suggestions while typing, `Up` stays the shell's history recall, and both arrows are owned only in an
  open list or on a surface the user summoned.
- **Explicit intent is never hidden.** `ShouldSuppressConservativeRemoteAssist` hid the overlay
  outright on short markless-SSH panes whose prompt sat high in the pane — and a split makes both
  panes short. It applied to `Ctrl+R` as readily as to a passive bubble, which is the "does not show
  up on one of them" report. A user-requested surface (`AssistSessionState`:
  ExplicitBubble/ExplicitPopup/HistorySearch/Help/FixPopup) now bypasses it, and worst-case placement
  is the safe lower band. The correction stack still *runs* for those surfaces; what it may not do is
  drop them to zero opacity while it settles.
- **Context-scoped history.** The `Ctrl+R` path was `GetRecentAsync` — pure recency, no context, and
  truncated to 5 candidates so no ranking rule could have helped. Entries matching the pane's context
  (host id for SSH, localness for local; profile as a secondary term) now rank first, and the rest
  follow rather than being hidden. Ranking stays in `CommandAssistSuggestionEngine`; the stores remain
  recall gates.
- **Degraded-session insertion, narrowed rather than dropped.** #286's browse-only rule refused all
  insertion without a grid snapshot. It now allows it when the pane can *prove* the line is empty:
  the #287 markless accumulator is unpoisoned and empty, and no keystroke is awaiting echo. This is a
  documented contract change — see the gaps doc.

Exit criteria: type-two-chars-see-value demo works on all four shells; benchmark in CI; updated smoke checklist passes. **(Phase 3b; Phase 3a's own bar was the three owner reports plus green per-project suites.)**

**Phase 3b exit status.** Tripwire in CI: done. Type-two-chars-see-value: verified on `pwsh` by driving
the built app and screenshotting the bubble, the Escape suppression, the settings group and the command
palette on its reclaimed chord; the other three shells are not installed on the dev box, and the
mark-driven path they share is the same code, so this is honestly "one shell verified, three inferred".
The updated smoke checklist is **not** done and stays where Phase 1 left it — it is a Phase 6 deliverable
(`docs/command-assist/` results on Windows + Unix-over-SSH), and writing a checklist in 3b that Phase 6
must re-run would duplicate rather than close it.

## Phase 4 — Real content

**Status: complete.** Tasks 1 and 2 landed in Phase 4a; tasks 3 and 4 in Phase 4b.

Tasks:
1. ~~Stderr capture: buffer the `133;C`→`133;D` output region (bounded: last 40 lines / 8 KB), redact via `SecretsFilter`, populate `CommandFailureContext.ErrorOutput` (removes the hard-coded `null` at the TerminalPane call site).~~ **Done (Phase 4a), with one rename the task line did not anticipate.** `ErrorOutput` became `OutputTail`, because a terminal has one grid: stdout and stderr are interleaved on it and nothing in the byte stream distinguishes them, so the old name promised a separate stream that has never existed at this layer. The reader is `CommandOutputReader` in `NovaTerminal.VT`, next to `GridQueryReader` and for the same layering reason. Three things worth carrying forward: (a) the region *start* can only be established at `C` — one frame later the shell has echoed a newline and nothing on the grid says where output began — so the pane records it there and reads at `D`, both synchronously on the parse thread, because a UI-thread hop reads a grid the next prompt has already painted over; (b) soft-wrapped physical rows are joined **without** a separator and the 40-line budget counts *logical* lines, or a recogniser matching a phrase that wraps would pass on a wide pane and fail on a narrow one; (c) an evicted region start **clamps** to the oldest surviving row rather than refusing — the request was for the last 40 lines and those are still there — while a `Generation` mismatch returns nothing, since that is the case where the rows are someone else's. Redaction runs at the single capture site, after the cap.
2. ~~Expand `HeuristicErrorInsightService`: per-shell command-not-found patterns, permission-denied, `./` hint (now reachable), git/docker/npm/dotnet failure signatures. Target: useful suggestion for top-10 failure classes.~~ **Done (Phase 4a).** Fifteen recognisers in a table (`CommandErrorRecognizers.All`), each a pure function over a pre-chewed `CommandErrorSignal`; the service asks all of them and concatenates, because a command-not-found on a script file legitimately produces both "did you mean" and "run it with `./`". Three findings the task line could not have known: **(i)** the typo corrector needed a *transposition* term — under plain Levenshtein `gti` is two edits from `git`, outside the budget a three-character token gets, so the single most recognisable typo in the world produced nothing; **(ii)** only two confidence values cross the 0.8 Fix threshold, and both are cases with no inference in them (a one-edit typo of a known name with the shell saying it could not resolve it, and the tool printing the exact fix — `git push --set-upstream …`, `The most similar command is / status`). Everything else is an explanation and rides into Suggest mode, where it costs nothing to ignore; **(iii)** the old reachable branch published a blind typo guess at 0.82 against a threshold of 0.8, and that is now a *ladder*: the table's answer when a recogniser matched, 0.40 when output was captured and matched nothing (the command ran — "did you mean git?" for a working `git` is noise), and the old capped guess only when no output could be captured at all. The known-commands list stays small and deliberately is **not** the Phase 4b catalogue: a typo corrector's precision falls as its vocabulary grows. Samples are live-captured where the box could produce the failure and transcribed where it could not; `CommandErrorRecognizerTests` records which is which, and no transcribed pattern is allowed over the threshold.
3. ~~`CommandKnowledgeService` with ordered sources: (a) build-time tldr-pages-derived catalogue (>=200 commands, <2 MB, CC-BY attribution in About), (b) local probing (`man -w`, `Get-Help`, `--help`) for "open full help" actions. Replaces `LocalCommandDocsProvider` + `SeedRecipeProvider`; closes #250.~~ **Done (Phase 4b).** 585 commands / 2,714 examples / 825 KB, generated by `scripts/generate-command-catalogue.ps1` from a tldr-pages checkout and committed under `assets/command-knowledge/`, embedded into `NovaTerminal.CommandAssist`. Four deviations from the task line, all deliberate: **(i)** *"build-time"* is **not** what shipped — the generator is run by hand and its output is committed, because a build-time step would put a network fetch (or a submodule) between a clone and a compile for content that changes on tldr's schedule, not on ours. Regeneration is documented in the script header. **(ii)** *Attribution is in the Help popup footer, not in About* — there is no About dialog in this app, and inventing one to hold a licence line would put the credit where nobody looks; it now appears under the rows it covers, whenever Help is open. **(iii)** *Two entries are not tldr content.* tldr has no page for `Get-Process` or `Get-Service`, both of which the design doc names; they are hand-authored in a supplement file the generator merges and marks `"o": "nova"`, so the CC-BY-SA line stays a true statement about exactly the rows it covers. **(iv)** *The example floor is one, not three.* A handful of genuinely simple tools (`head`, `pwd`) have a single tldr example, and dropping them would be a strictly worse catalogue; the six-example cap is what the "3-6" range was really about. Local probing landed at the App boundary behind `ICommandHelpProbe` (`LocalCommandHelpProbe`), as existence checks against `PATH`/`MANPATH` only — never a spawned process — cached per token and shell.
4. ~~Snippet management UI in Settings (list/edit/delete; `ISnippetStore.RemoveAsync` gets a caller).~~ **Done (Phase 4b).** A "Saved snippets" section in the Command assistant group: one row per snippet with an editable name and command plus Delete, and an Add button. The rules are in `SnippetEditor` in the Avalonia-free assembly rather than in `SettingsWindow`, because no test in this repo constructs that window and the rules (what a blank name means, what an edit must not destroy) are worth testing against a real store. Rows commit on focus loss rather than through the window's Save button: snippets are their own store, not part of `settings.json`.

Exit criteria: Fix mode demo across failure classes; Help useful for arbitrary common commands; catalogue size + attribution verified. **(Task 3 verified live on `pwsh` by driving the built app: `ssh` returns the tldr summary plus insertable examples, `git rebase` resolves to the two-token subcommand entry rather than to `git`, an unknown command (`frobnicate`) degrades to the probe's "Open full help: Get-Help frobnicate" row, and the CC BY-SA credit renders in the popup footer under all three. Catalogue size, count and attribution are additionally pinned by `CommandCatalogueAssetTests` against the committed asset, so a bad regeneration fails the suite rather than shipping. Task 4's rows were verified live too, and one bug was found that way and fixed: the three controls in a snippet row all landed in `Grid.Column` 0, because Avalonia defaults the attached property and a `Children` initializer does not assign it. The only thing the live pass could not photograph is the right-hand column of the Settings page — Delete, Add snippet and every toggle beside them sit past the right edge of a window that will not shrink below its content width on this display; that is a pre-existing property of the page, not of this row. Tasks 1-2 remain with the Phase 4a author.)**

## Phase 5 — AI seam (interfaces only)

**Status: complete.** All three tasks landed together; the phase is small and the three are one seam.

Tasks:
1. ~~`IAssistContentProvider` + `AssistCapabilities` + request/response records in the CommandAssist assembly; redaction enforced in the orchestrator before any provider call.~~ **Done, and "enforced" became a type rather than a rule — that is the phase's one architecturally load-bearing decision.** `src/NovaTerminal.CommandAssist/Providers/`. The request carries free text only as `RedactedText`, which has no public constructor, no conversion from `string`, and one `internal` factory that takes an `ISecretsFilter` as a parameter: you cannot produce one without having a filter in hand and running it. `AssistContentRequest`'s constructor is `internal` on top of that, so a provider in another assembly can neither mint redacted text nor fabricate a request around text it got some other way. The single construction site is `AssistContentRequestFactory`, pinned by `AssistSeamStructureTests` (a source scan — the thing being asserted is "one file contains this", which is what a reviewer checks). **No field is exempted for being structured enough**, including the working directory: an exemption is a judgement call, and a rule with no exceptions needs neither judgement nor argument. Redaction is unconditional even where the caller already redacted (the pane filters the output tail at the VT boundary and the factory filters it again) — a guarantee that holds only when every upstream caller remembered is not a guarantee, and the second pass is idempotent.
2. ~~Adapt `HeuristicErrorInsightService` and `CommandKnowledgeService` to implement the interface; orchestrator queries providers uniformly.~~ **Done by adapters, not by re-basing.** `LocalErrorInsightProvider` (SuggestFix) and `LocalCommandKnowledgeProvider` (EnrichDocs) wrap the Phase 4 services, which keep their own interfaces and their test suites untouched. Both adapters translate the request *back* into the shape the Phase 4 code wants — which is the seam demonstrating its own guarantee, since the `CommandFailureContext` the recognisers now see is built from redacted text only. `CommandKnowledgeService`'s two interfaces are one provider rather than two, because Help has always asked docs and examples in the same breath and splitting them would invite a composition where the doc row and the example rows come from different catalogues. The controller no longer holds an `ICommandDocsProvider`/`IRecipeProvider`/`IErrorInsightService`; it holds an `AssistContentProviderRegistry`, and the three legacy parameters stay on its constructor only so every existing caller keeps compiling and keeps meaning the same thing. The three private `Empty*Provider` stubs are deleted — see task 3 for what they were hiding.
3. ~~Settings + empty states for unconfigured capabilities ("AI assist not configured"). No network code, no provider implementations.~~ **Done, with a deliberate decision on each half.**
   - **Settings: the shape is reserved and documented, the key is not added.** `AssistProviderPolicy` *is* the config shape the AI milestone will deserialize into (a provider-id allow-list per capability) and it is live today as the registry's gate. What is not added is the `settings.json` key or a UI row, because with only local providers shipped every value of that key would be the empty object, and a persisted setting that cannot change observable behavior is exactly the phantom flag Phase 3b deleted. Unknown keys are ignored on load, so adding it later is not breaking. The intended JSON is written out in `AssistProviderPolicy`'s remarks and in `CommandAssist.md` §8.
   - **Opt-in is an obligation on the provider, not a settings key it could be registered around.** `IAssistContentProvider.RequiresExplicitOptIn` — a provider that can send the request off this machine says so in its own type, and the registry refuses to query it until the policy names its id *for that capability*. Local providers return false and cannot be switched off: "turn off the bundled command catalogue" is not a privacy control, it is a way to break Help.
   - **Empty states distinguish "we looked" from "nobody is configured to look".** The three deleted `Empty*Provider` stubs made those the same sentence. `AssistEmptyStates.ForMissingProvider` returns "No help provider is configured." / "No fix provider is configured." / "AI assist is not configured." — the last for `NlToCommand` and `Explain`, which are the capabilities only an AI provider could serve. **Honest about reach:** in the shipped app every capability the UI can ask for has a local provider registered at the composition root, so the not-configured strings are unreachable through the UI; they are reachable, and tested, for a controller composed without those providers. **No Ask-AI entry point was invented** to make the `NlToCommand` string visible — a button that can only ever say "not configured" is a dead end being called a feature.
   - **No network code.** `LayeringTests.CommandAssist_must_not_depend_on_networking` (IL type dependencies), `CommandAssist_assembly_references_no_networking_assemblies` (the reference table, which catches a `using` with no live call site) and `ProjectFileLayeringTests.CommandAssist_csproj_must_have_no_networking_package_references` (the package landing a commit before the code that uses it). When an AI provider is built it does not relax these: it goes in its own assembly behind `IAssistContentProvider`, which is what the seam is for.

Exit criteria: local providers run through the seam with zero behavior change; architecture test forbids network references in the assembly. **Both met. Zero behavior change is pinned by the Phase 3/4 Help and Fix suites passing unmodified — no existing test needed a mechanical edit, because the controller's constructor signature did not change (the registry is an optional trailing parameter). Verified live on `pwsh` besides: `gti` opens the Fix popup with the `git` correction, `Ctrl+Shift+H` on `ssh` returns the tldr summary plus insertable examples with the CC BY-SA credit in the footer. Mutation-checked: making the request factory pass raw text instead of calling the filter fails `RequestFactory_RedactsEveryFreeTextFieldBeforeAProviderCanSeeIt` and the controller-level `Controller_WhenACommandFails_TheProviderNeverSeesRawText`.**

One thing found rather than built, recorded so it is not re-discovered: **"No likely local fix found." has never been on screen and still is not.** Reaching Fix mode requires a confidence >= 0.8, which requires at least one row; the Suggest branch returns early when there are none. The string is now computed as the right one of two rather than hard-coded as the convenient one, so a future router change shows the honest sentence, but nothing in this phase made it reachable and nothing should pretend otherwise.

## Phase 6 — Flag flip

**Step 1's blocker is cleared.** Phase 3b dogfooding found that the *first prompt of a local `pwsh`
session was fully degraded* — no passive bubble, no grid-truth query, no structured capture — with
everything working from the second prompt on. That was the last pre-flag-flip item, and it is fixed.

Root cause, from live probe logging rather than from reading: the first `OSC 133;B` **is** recorded,
on time, against a tracker armed before the first byte (so the two standing hypotheses were half
right and half wrong — the mark is not lost, and the ordering was never the problem). What kills it
is the resize the user makes as soon as the window is up: a width change reflows the buffer, which
rebuilds the absolute-row coordinate space and bumps `ScrollbackPages.Generation`, and every reader
correctly refuses a mark from a dead epoch. The assumption that made that refusal look benign —
written into `ShellIntegrationMark`'s own remarks — was that "every shell re-prints its prompt after
a resize, and the repaint carries a fresh B". **It does not:** measured on PSReadLine 2.3, a resize
repaints the *input line* without re-running the prompt function. So the session went markless for
the rest of that command line and only recovered at the next real prompt. The "first prompt" framing
is a symptom of when people resize, not a property of the first prompt.

The fix does **not** relax the generation check, which is the only guard against a confidently wrong
row. The buffer now owns the live marks (`TerminalBuffer.CommandStartMark` /
`CommandOutputStartMark`) and the reflow **re-anchors** them by logical-line index plus
offset-in-logical-line — the same mapping it already applies to the cursor, the saved cursors and
inline images — so a mark comes out of a reflow with new coordinates *and* the new generation, and
there is no stale epoch left to reject. A caller holding its own pre-reflow copy is still refused,
and `GridQueryReaderTests.ReflowingResize_RefusesAMarkCopyTakenBeforeIt` pins that. Caveats
(unplaceable marks are cleared not guessed at, `CSI 3J` still kills the mark, alt-screen marks are
never re-anchored, the write-back is a compare-and-swap against a concurrent fresh `B`) are in the
gaps doc under "Added In V2 Phase 6 prep". Verified live on `pwsh` before and after; the other three
shells share the VT code path and are not installed on the dev box.

1. Verify all six re-enable criteria from the design doc, plus the one item Phase 1 carried here:
   smoke scenarios 1–8 re-validated. (Phase 1's other carried item, task 7's markless capture-only
   accumulator, landed in Phase 1d. The first-prompt blocker above is cleared.)
2. Update `CommandAssistDefaultDisabledTests` → `CommandAssistDefaultEnabledTests`; flip `TerminalSettings.CommandAssistEnabled = true`; changelog + docs refresh (`CommandAssist.md` rewritten to match V2 reality, §14 keyboard table fixed).
3. New smoke checklist executed on Windows + Unix-over-SSH; record results in `docs/command-assist/`.

---

## Sequencing notes

- Phases 0–2 are strictly ordered (each builds on the previous). Phases 3 and 4 are independent of each other and can interleave after Phase 2; Phase 5 needs Phase 4's providers.
- The M4.3 plan docs (`2026-03-11-command-assist-m4-3-*.md`) are implemented but still marked "In progress" — mark them Completed when Phase 0 lands to stop the status drift.
- Known-stale figures: issue #114's "~12 bools" is actually 7, and both July triage docs repeat #114's 4,766 LOC / 959-line-controller numbers; measured at `c083803` it is ~4,192 LOC / 854 lines. Do not re-quote the stale pair when scoping.
