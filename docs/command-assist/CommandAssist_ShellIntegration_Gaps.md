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

## Current Limitations
- shell integration is local-only; SSH launch plans skip provider injection
  because env-var overrides do not propagate across SSH
- providers bail out (`IsIntegrated: false`) when the user forces an
  incompatible startup mode (PowerShell `-File`; bash `-c`/`--rcfile`/`--init-file`;
  zsh `-c`/`--no-rcs`/`-f`; fish `-c`/`--no-config`/`-N`); those sessions fall
  back to heuristic capture
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
