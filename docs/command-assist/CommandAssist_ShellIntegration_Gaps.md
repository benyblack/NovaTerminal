# Command Assist Shell Integration Gaps

## Implemented In M3
- generic shell integration contract
- App-layer launch-plan selection
- PowerShell bootstrap integration with full structured command capture
  (`OSC 133;A`, `OSC 133;C;<base64>`, `OSC 133;D;<exit>;<duration>`, `OSC 7`)
  (V2 Phase 1a added `OSC 133;B` to all four bootstraps — see "Added In V2 Phase 1a" below)
- Bash provider via `--rcfile` (DEBUG trap preexec, `PROMPT_COMMAND` precmd)
- Zsh provider via `ZDOTDIR` env-override (native `precmd_functions` /
  `preexec_functions` hooks; user prompt ownership preserved)
- Fish provider via `XDG_CONFIG_HOME` env-override (native `fish_preexec` /
  `fish_postexec` / `fish_prompt` event hooks)
- environment-variable override plumbing through `ShellIntegrationLaunchPlan`,
  `RustPtySession`, and the `pty_spawn_with_envs` Rust FFI (used by Zsh and Fish)
- structured exit-code and duration enrichment for command history
- heuristic fallback when structured integration is unavailable or not yet confirmed

## Added In V2 Phase 1a
- `OSC 133;B` (prompt end / start of user input) is now emitted by all four bootstraps,
  closing the "no B mark, so `ShellIntegrationEventType.CommandStarted` is dead code" gap.
  Because bash/zsh/pwsh emit `A` *before* the prompt is printed, `B` cannot come from the
  same hook — it has to sit at the tail of the prompt itself:
  - **bash**: `\[\e]133;B\a\]` appended to `PS1`, re-applied from `__nova_arm` (last entry in
    the `PROMPT_COMMAND` chain, so themes that rewrite `PS1` there cannot drop it)
  - **zsh**: `%{...%}`-wrapped suffix appended to `PROMPT`, re-applied from `__nova_precmd`
  - **fish**: `fish_prompt` is copied to `__nova_user_fish_prompt` and re-defined as
    "original, then `B`" (the `fish_prompt` *event* fires before the prompt renders, so it
    can only carry `A`)
  - **PowerShell**: appended to the string the wrapped `prompt` function returns (anything the
    function writes would land *before* the prompt text)
- The parser reports the mark with the cursor position at parse time
  (`AnsiParser.OnCommandStarted` now takes a `ShellIntegrationMark`), which reaches
  Command Assist as `ShellIntegrationEvent.MarkPosition`. `AbsoluteRow` is the
  eviction-stable identity; `Row`/`Column` are the immediate buffer coordinates.
- Nothing consumes the position yet — the grid query reader lands in Phase 1b.

## Added In V2 Phase 1b

`NovaTerminal.VT.GridQueryReader.TryReadCommandLine(buffer, mark, out GridCommandLine)` reads the
live command line straight out of the grid: the cells from the newest `OSC 133;B` mark to the
cursor. It lives in `NovaTerminal.VT` rather than the CommandAssist assembly the plan first
sketched, because the work is pure buffer walking (wrap flags, paged scrollback, wide-cell
continuations, deferred autowrap) and the layering tests forbid CommandAssist from referencing
VT; the App-side seam is the internal `TerminalPane.TryGetGridCommandLine`, which pairs the
reader with the newest mark and the buffer's read lock. Nothing consumes the seam yet —
Phase 1c does that and deletes the keystroke shadow buffer.

**Contract.** The reader never throws and never guesses: it returns `false` for a mark from a
dead coordinate generation, a marked line that aged out of scrollback, an alt-screen mark or an
active alt screen, a cursor above or left of the mark, a mark position outside the buffer, and a
span larger than 512 rows. `GridCommandLine` carries `Text`, `CursorOffset` (always a valid index
into `Text`; the cursor is routinely mid-line after arrow keys), `IsMultiline`,
`RightPromptTrimmed`, and the span's `StartRow`/`EndRow`. Soft-wrapped rows are joined with no
separator and are followed *past* the cursor row, so a logical line wrapped over three physical
rows comes back whole no matter which row the cursor is on. The result is only meaningful between
`OSC 133;B` and the following `OSC 133;C` — the reader cannot distinguish "still typing" from
"the command ran and this is its output", so lifecycle gating is the consumer's job.

**Multiline decision (b), raw plus flag.** A hard line break inside the span becomes a single
`'\n'` and sets `IsMultiline`. The text is returned raw, which means it still contains whatever
the shell painted as a continuation prompt (`PS2`, `PROMPT2`, fish's `> `): nothing marks those
cells as prompt rather than input, so the reader cannot strip them and instead flags the text as
untrustworthy-as-prefix. Consumers may use multiline text for history and display but must never
treat it as a typed prefix. The alternative — returning only the first logical line — was
rejected because it silently loses text and makes `CursorOffset` ambiguous, and downstream
refuses prefix-dependent features on multiline input anyway. Documented gap: if the cursor sits
on an *earlier* logical line of a continuation entry, the span stops at the end of that line and
`IsMultiline` stays clear. Extending across hard breaks whenever the row below has content would
close that gap but misfire on every zsh tab completion, which prints its listing directly below
the input line.

**Right-prompt (RPROMPT) decision.** zsh's `RPROMPT`, fish's `fish_right_prompt` and starship's
right prompt all paint right-aligned text on the input's own row, and a naive "mark column to
last non-blank cell" read swallows it. Stopping at the cursor is not an option because the cursor
is mid-line whenever the user has pressed an arrow key. Trailing cells are excluded only on the
final row of the span, only when the cursor is on that row, and only when all five of the
following hold. The row is read as `[input][gap][badge]`:

1. the trailing content ends within 2 columns of the right edge (right-aligned text does; typed
   input generally does not, and `ZLE_RPROMPT_INDENT` defaults to 1);
2. the gap starts at or after the cursor, so nothing left of the cursor is ever discarded;
3. the gap is the *widest* run of blank cells in that region — the row's dominant slack, which is
   what a right-aligned paint produces;
4. the gap is at least 2 cells wide **and strictly wider than the badge it separates**;
5. the badge is at most `Cols / 3` columns wide.

Conditions 4 and 5 are load-bearing, and an earlier revision of this document was wrong about
why. Condition 2 does *not* on its own make a double space inside typed input safe: it protects
only what is left of the cursor. With the cursor at the start of the line (Home) and input that
happens to reach the right edge — `echo aaaa...aa  bbbb` — every interior gap is at or after the
cursor, and the `bbbb` was silently deleted. Condition 4 is what stops that: two blanks in front
of four characters is a typo, not a right prompt.

Condition 3 also fixes multi-segment right prompts. Taking the rightmost qualifying run cut
`12:34  ok` at its own internal gap, keeping the wide blank run and `12:34` — worse than not
trimming at all. Taking the widest run trims the whole right-aligned group.

The failure mode is deliberately asymmetric. An unrecognised right prompt comes back as extra
text, which a consumer can survive; a mis-recognised one deletes what the user typed. So a gap
followed by content that stops well short of the right edge is kept, a badge wider than the gap
in front of it is kept, and a badge wider than a third of the row is kept.

**Mark lifecycle at the App seam.** `TerminalPane` drops `_latestCommandStartMark` on `OSC 133;D`
(command finished), so between one command's end and the next prompt's `B` the seam returns
`false` instead of serving command output as a command line. It is deliberately *not* dropped on
`OSC 133;C`: C fires the instant the user submits, while the input line is still on screen and
still exactly what the mark describes, and Phase 1c reads the final command text on that edge.
`GridQueryReader.MaxSpanRows` stays as a backstop for shells that emit `B` without a matching `D`,
rather than being the only guard.

## Added In V2 Phase 1c — the consumption contract

Phase 1b made the reader trustworthy. Phase 1c is what consumes it, and the reader's contract is
only half of the story: a caller that reads whenever it likes, and acts on whatever comes back,
gets wrong answers from a correct reader. Three rules, all enforced on the Command Assist side.

**1. Lifecycle gate — read only between `B` and `C`.** The reader cannot self-gate: the cells
between the mark and the cursor look identical whether the user is editing a command line or the
command has run and those are the first lines of its output. Only the OSC 133 stream distinguishes
them, so consumption is gated on `AssistSessionContext.IsAcceptingCommandInput` — opened by
`CommandStarted` (`133;B`), closed by `CommandAccepted` (`133;C`), by `CommandFinished` (`133;D`)
and by an alt-screen switch. Nothing else opens it; a prompt repaint re-emits `B`, so the gate
reopens on evidence rather than on assumption.

The gate is deliberately *not* conditioned on `IsShellIntegrationEnabled`, which records only
whether we injected the bootstrap. A shell that emits `B` is instrumented whether we did it or the
user did, and Phase 2's instrumented-remote work depends on believing the marks. (Today that is
theory rather than practice: `ShellLifecycleTracker` is armed only by
`TerminalPane.ApplyShellIntegrationLaunchPlan`, so a session we did not instrument delivers no
events at all and stays fully degraded. Arming it on observed marks is Phase 2 task 3.)

The gate and the mark are two independent facts and both are required. The gate can be open while
the mark has aged out of scrollback or its coordinate generation has been reset; the mark can be
live while the command it anchors is halfway through printing its output.

*Known edge, currently unreachable:* the parser raises `OnCommandAccepted` only for a `133;C`
carrying a decodable base64 payload, so a bare `133;C` does not close the gate — `133;D` then has
to. All four bootstraps emit `133;C;<base64>`, and a session we did not instrument has no tracker
armed, so no reachable configuration hits this. It becomes real the moment Phase 2 arms the tracker
for third-party integrations, and should be closed there.

**2. Settled-boundary reads, not per-keystroke reads.** The buffer takes its write lock per written
character, so a read racing a prompt repaint (`\r`, erase-to-end-of-line, reprint) can legitimately
observe a half-erased line — and acting on that produces suggestion flicker on every `Ctrl+U`,
history recall and tab completion. The read therefore happens inside `SuggestionOrchestrator`'s
refresh pass, on the worker the pass already runs on, not on the keystroke that triggered it. A
keystroke is a trigger carrying no text; the pass resolves its own query. Passes supersede each
other through the existing per-pass `CancellationTokenSource`, so a burst of keystrokes applies one
read, the last one. That is coalescing by supersession, not by timing: there is no debounce, which
is a Phase 3 policy decision.

The window that remains is a read that beats the shell's echo of the character just typed, and it is
worth restating because an earlier revision of this document understated it as "ranks a
one-character-stale query". For *ranking*, that is the whole of it. For *insertion* it was a
corruption bug. The stale read is internally perfect — `git st` with the cursor at offset 6, every
planner guard satisfied — while the PTY already holds `git sta`; and because the stale text is always
a strict **prefix** of the true line, no `StartsWith`-style check can ever catch it. `Ctrl+Enter` on a
row ranked from `git st` would append `atus` to a line that already reads `git sta`, and the line
becomes `git staatus`. Guarded now: `TerminalPane` tracks `_hasUnechoedInput` — set by
`TextInputObserved` / `BackspaceObserved`, cleared once session bytes have been parsed into the
grid — and `TryInsertSelectedCommandAssistSuggestion` refuses while it is set, which is the same
refusal-on-doubt rule the planner's four conditions follow. Ranking is deliberately left unguarded: a
marginally worse row for one keystroke does not justify going quiet, and the next trigger corrects it.
The clear is approximate in one direction only — unrelated session output can clear the flag early,
leaving the original window open — but never the other, because output that has been parsed is
output that is in the grid.

**3. Insertion refuses rather than guesses.** `CommandAssistInsertionPlanner` keeps the V1 rule that
insertion is additive — send only the characters the suggestion adds, never delete, never move the
cursor — but it now computes against `AssistQuerySnapshot` and refuses outright when the append
assumption does not hold:

- **no snapshot** (markless session, or gate closed): the command line cannot be seen, so appending
  a whole command to an unknown prefix is how `git sgit status` happens;
- **cursor not at the end of the text**: sent text lands at the cursor, so the "suffix" would be
  spliced into the middle of the command;
- **`IsMultiline`**: the text contains continuation-prompt cells (`PS2`, `PROMPT2`, fish's `> `)
  that the user never typed, so it is not a prefix even when it starts one;
- **`RightPromptTrimmed`**: the reader's RPROMPT trim is conservative, but conservative means "over-
  returns rather than deletes"; when it did fire, the *tail* of the line is an inference, and the
  tail is exactly what an append attaches to.

An observed-empty line is not a refusal: the grid was read and the line is empty, so the whole
command is sent. That distinction — "empty" versus "unknown" — is why the query crosses the
boundary as a nullable snapshot rather than a string.

### Degraded mode after Phase 1c

A session with no OSC 133 marks — a non-integrated local shell, a shell whose bootstrap bailed out,
an un-instrumented SSH host — has no query at all. Not an empty query it might act on: no query.
The shadow keystroke buffer is deleted, not kept as a fallback, because a fallback that desyncs is
worse than no fallback (it was the fallback that made V1's history and insertions wrong). History
capture got a replacement in Phase 1d, but a capture-only one that refuses rather than guesses; it
is never consulted as a query. What degraded mode costs, concretely:

- **no passive suggestions.** With no query the path provider has no command token and no
  path-shaped fragment to work from, so it returns nothing. Degraded passive suggestions are empty
  by construction rather than by a special case.
- **no help token from the command line.** `Ctrl+Alt+H` with nothing selected finds nothing.
  Explain-selection still works, because a selection is an explicit input the grid is not needed
  for, and Fix still works, because it analyses the command that failed rather than the one being
  typed.
- **no insertion.** Rows can be browsed; nothing may be spliced into a command line nobody can see.
- **Enter-time history capture, but only for a straight-through-typed line.** This is the one that
  was worth arguing about. V1's heuristic capture read the shadow buffer, so for any line the user
  had edited with keys the mirror could not observe — `Ctrl+U`, arrows, history recall, tab
  completion — it wrote a command the user never ran into persistent history. Recording nothing is
  recoverable; recording something false is not. Instrumented sessions were never affected: the
  first command is captured from the grid at Enter (which is strictly better than the mirror ever
  was, since the grid survives all of those edits), and every command after it comes from the
  `133;C` payload.

  Phase 1c shipped with markless sessions capturing nothing at all, which was a hole rather than a
  resting state — `cmd.exe`, every shell whose bootstrap bailed out, and *every* SSH session, since
  SSH launch plans skip provider injection. **Shipped in Phase 1d (ii-strict), Phase 1 task 7.**
  The contract in one line: **in a session with no marks, a command typed straight through with no
  editing is captured verbatim; anything else is captured not at all.**

  The mechanism is deliberately not the mirror. `MarklessSubmissionAccumulator` is **poisoned** by
  everything it cannot model, and it is consulted at Enter *only* when the grid has nothing — never
  as the query. `TextInputObserved` appends, `BackspaceObserved` chops one character. The key
  classification is an allow-list, so an unrecognised key poisons. What leaves it alone:

  - modifier keys on their own;
  - `Enter` and `Backspace` **with no modifiers at all**. Not merely "without `Alt`": with the kitty
    keyboard protocol's disambiguate tier active, `TerminalView` encodes a *modified* Enter or
    Backspace as CSI u (`Ctrl+Backspace` → `CSI 127;5u`, `Shift+Enter` → `CSI 13;2u`) and returns
    early, so `EnterObserved` / `BackspaceObserved` never fire — the accumulator would keep every
    character while a kitty-aware editor deleted a word. The modifier is not visible downstream, so
    the classifier refuses it up front. Cost: one lost capture per `Ctrl+Backspace`;
  - `Ctrl+C`, which resets rather than poisons;
  - keys Command Assist consumed (see the `Ctrl+Shift+P` note below);
  - printable keys with no `Ctrl`/`Alt`/`Meta` — **and the `AltGr` exception**: on Windows, Avalonia
    reports `AltGr` as `Control|Alt`, so a literal reading of that rule poisons every `@`, `{`, `[`,
    `\`, `|` and `~` typed on a German, French, Nordic, Turkish or Polish layout, i.e. captures
    nothing at all for those users. `Ctrl`+`Alt` plus a *text-producing* key is therefore allowed.
    That stays fail-closed because `TerminalView` sends **nothing** to the PTY for that combination
    — `EncodeKittyKey` returns null on the `Control|Alt` pair, `EncodeAltKey` returns null for it,
    and the legacy `Ctrl` branch requires `!Alt` — so the only route to the shell is the composed
    `WM_CHAR`, which arrives as `OnTextInput` and is appended. `Ctrl`+`Alt` plus a *non*-text key
    (`Enter`, `Backspace`, `Tab`, `Escape`, arrows) still poisons: those do reach the shell.

  Arrows, `Home`, `End`, `Delete`, `Tab`, page keys, `Insert`, `Escape`, F-keys and every other
  unowned chord poison it, as does any text that reaches the PTY without going through key handling:
  both paste paths, `Ctrl+Enter` insertion, the drop toast, sibling-pane broadcast, the
  clipboard-image path, the agent host's act surface, and parser device replies (`OnResponse`: DA1,
  DSR, answerback). It resets on `Enter` (after the capture read), `Ctrl+C`, any alt-screen
  transition, and session start / restart / profile switch.

  `Ctrl+Shift+P` is only *conditionally* assist-consumed: the window's shortcut handler calls
  `TerminalPane.TryToggleCommandAssistPinShortcut`, which routes without observing, and if no
  selection can be pinned the route returns false and the key continues on to `TerminalView`, where
  the accumulator sees it as an unowned `Ctrl` chord and poisons. That is the safe direction — a
  keypress that may or may not have reached the shell is treated as if it did — and it costs at most
  one capture.

  **The echo gate.** The accumulator alone is not enough, for a reason that is a security bug rather
  than a correctness one. `TerminalView.OnTextInput` fires per keystroke unconditionally: it does not
  know whether the shell is echoing. So `ssh host` / Enter / `hunter2` / Enter, all inside one
  markless session, leaves a clean unpoisoned `hunter2` in the accumulator at the hidden `password:`
  prompt with no grid snapshot to outrank it — and `SecretsFilter` is pattern-based, so a bare secret
  sails through it into `history.jsonl`. Before the accumulator's answer is used, the pane therefore
  requires that exact text to be **visible on the grid, ending at the cursor**
  (`GridQueryReader.TryReadTextEndingAtCursor`, reading the cursor row and its soft-wrapped
  predecessors under the buffer read lock, comparing text rather than columns so wide characters
  count once). In any visible markless prompt the typed command *is* on screen — only the `B` mark is
  missing — so a correct capture pays nothing; at a no-echo prompt the strings do not match and
  nothing is captured. Every doubt resolves the same way: no buffer, an alt screen, an unresolvable
  cursor, an echo that has not landed, a partially echoed line — no capture.

  Its outcomes are therefore "exactly the characters the user typed, in order, and visibly on
  screen" or "nothing" — the same bar the grid reader is held to, reached by a different mechanism —
  and it is deletable in one commit once Phase 2 gives instrumented remotes real marks.

  **It composes with suppression rather than replacing it.** Poison is an accumulator fact ("I
  cannot describe this line"); `AssistSessionStateMachine.IsCurrentSubmissionSuppressed` is a
  provenance fact ("this text was not composed here") that `CapturePipeline` applies to both capture
  sources. A paste in a markless session is stopped twice over; a paste in an *instrumented* session,
  where the grid reads it perfectly well, is stopped only by suppression. And grid truth always wins
  where it exists, including when it reads the line as empty: "observed empty" and "unknown" are
  different answers and only the second falls through to the accumulator.

**`Ctrl+R` in a degraded session** still opens and still helps, because history is per user rather
than per session: with no query to filter on, Search shows the recency list, which includes
everything captured in instrumented sessions. It is browse-only — see the insertion rule above. It
also says so: the bubble is labelled **`History - recent`** rather than `History` when no query can
be read, because an identical-looking filter box that silently cannot filter reads as a bug the
first time a keystroke fails to narrow it.

### Overlay anchoring after Phase 2a

A second thing the marks buy, beyond the query: the overlay knows where the prompt *is*. Phase 2a
makes the `133;B` mark the anchor source when one is live and on screen, and demotes the geometric
`CommandAssistPromptHint` heuristic — cursor row plus band ratios — to the markless fallback.

`133;A` was the design doc's nominal choice and is not the one shipped: `A` reaches the App as a
bare `OnPromptReady` notification with no position, while `B` is already stamped with a full
`ShellIntegrationMark` (row, column, eviction-stable `AbsoluteRow`, generation, alt-screen flag) for
the grid reader. `B` is also the semantically better row — it is where the user's *input* starts,
which is what the bubble sits next to — and it re-fires on every prompt repaint. `A` would need a
mark of its own to be usable at all.

What the mark path skips, and why each one was a hedge against not knowing the row:

- the anchor-reliability guess (`SSH ⇒ untrusted`): an instrumented remote emits the same marks a
  local shell does, so the session type stops being the question;
- the band ratios in `CommandAssistAnchorCalculator` (0.45 / 0.55 / 0.60 / 0.70), including the
  0.55 whose 0.005-wide margin produced #232's font-dependent flakiness;
- the short-pane suppression that hid the overlay entirely on remote panes;
- the render-priority placement-correction passes and the opacity flicker they cost.

What it keeps: the size clamps, the compact bubble/popup thresholds, and the popup flip/side rules.
Those are pane-geometry facts, not prompt-position guesses.

None of that stack is deleted, because markless sessions are not gone — an un-instrumented remote is
still the common SSH case until Phase 2 task 3 ships the remote snippets. Everything above is
*gated* on the anchor source and stays reachable for sessions without marks.

Limits of the mark anchor: it is only usable while the marked row is on screen, so scrolling the
prompt out of the viewport, a scrollback reset (generation bump), eviction, or the alt screen all
drop back to the heuristic mid-session. The conversion is re-derived on every placement pass rather
than cached, because the scroll offset is an input to it.

## Current Limitations
- shell integration is local-only; SSH launch plans skip provider injection
  because env-var overrides do not propagate across SSH; **as of Phase 2a a remote that emits
  OSC 133 by other means (manual instrumentation today, the Phase 2 task 3 snippets later) gets
  trusted overlay anchoring automatically** — nothing about anchoring is keyed off the session type
  any more, only off whether a mark is live
- providers bail out (`IsIntegrated: false`) when the user forces an
  incompatible startup mode (PowerShell `-File`; bash `-c`/`--rcfile`/`--init-file`;
  zsh `-c`/`--no-rcs`/`-f`; fish `-c`/`--no-config`/`-N`); those sessions fall
  back to the markless capture-only accumulator, which records a
  straight-through-typed command and nothing else
- `BashBootstrapBuilder` uses a one-shot guard around the DEBUG trap to filter
  out internal hook calls, but commands run inside `PROMPT_COMMAND` itself can
  still race the guard in pathological user configurations
- prompt preservation is best-effort and depends on each shell's native prompt
  ownership conventions; the `OSC 133;B` suffix is *appended* to the user's prompt
  (never a template assignment), but a prompt framework that rebuilds the prompt after
  our hook runs would still drop it for that cycle
- fish integration re-defines `fish_prompt` around a copy of the user's function; a config
  that re-defines `fish_prompt` again *after* the bootstrap loads loses the `B` mark
  (`A`/`C`/`D` are unaffected)
- fish integration works by pointing `XDG_CONFIG_HOME` at our own directory, which also
  moves `$__fish_config_dir/functions` — so fish's autoloader no longer finds the user's
  `~/.config/fish/functions/fish_prompt.fish`. We source the user's `config.fish`
  explicitly, but an autoloaded `fish_prompt` is not part of it: for those users the
  function we wrap is fish's *default* prompt, not theirs. The marks are correct; the
  prompt appearance is not
- `ShellMarkPosition.AbsoluteRow` is stable across scrollback eviction but **not** across a
  scrollback reset (CSI 3J / RIS / clear-buffer / reflow), which zeroes the buffer's row
  counters. `ShellMarkPosition.Generation` carries the coordinate-space epoch so a consumer
  can tell the two apart; a negative derived row only means "aged out" within one generation
- the mark records the cursor's column but not the deferred-autowrap bit, so a prompt that ends
  exactly on the last column leaves `GridQueryReader` starting one cell early and picking up the
  prompt's final character. Recording pending-wrap on the mark would fix it; not worth the
  cross-layer churn for a prompt that exactly fills the terminal width

## Deferred Follow-Up Areas
- richer shell-specific prompt contracts beyond the current wrapper approach
- SSH-side bootstrap injection (would require remote shell-kind detection and
  remote env-var control)
- additional setup UX in settings or profile surfaces

## Non-Goals Of M3
- AI assistance
- help/fix/documentation surfaces from later milestones
- terminal-grid inline suggestion rendering
- VT/render-core refactors
