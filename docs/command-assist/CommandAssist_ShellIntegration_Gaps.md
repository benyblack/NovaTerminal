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
