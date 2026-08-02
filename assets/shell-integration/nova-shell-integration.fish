# Nova Terminal remote shell integration (fish).
#
# fish is not POSIX sh and cannot source nova-shell-integration.sh: `case`,
# `$-`, `local`, function syntax and array syntax all differ. This file is the
# fish equivalent and emits exactly the same marks.
#
# WHAT IT DOES
#   OSC 7            the current directory, once per prompt
#   OSC 133;A        prompt start
#   OSC 133;B        prompt end / first cell of your input
#   OSC 133;C;<b64>  the line you submitted, base64-encoded
#   OSC 133;D;<n>;<ms>  exit code and duration of the command that just ran
#
# INSTALL (on the REMOTE host)
#   1. mkdir -p ~/.config/fish/conf.d
#   2. cat > ~/.config/fish/conf.d/nova-shell-integration.fish
#      ...paste this whole file, then press Ctrl-D...
#   3. Open a new Nova session to that host.
#   conf.d is auto-sourced by fish, so there is no loader line to add.
#
# GUARANTEES
#   - Your fish_prompt is wrapped around, never replaced.
#   - Sourcing this file twice is a no-op the second time.
#   - Non-interactive shells install nothing.
#
# Docs: docs/command-assist/RemoteShellIntegration.md

# Everything lives inside one guard block rather than behind an early exit:
# `exit` in a sourced fish file exits the SHELL, and top-level `return` is not
# portable across fish versions.
#
#   status is-interactive  - a non-interactive fish must stay byte-clean, since
#                            an OSC written into an scp/rsync stream corrupts it
#   __nova_shell_integration_loaded - conf.d is sourced once per shell, but
#                            `exec fish`, a manual `source` and a fish_config
#                            reload all re-run it; installing twice would emit
#                            every mark twice
if status is-interactive; and not set -q __nova_shell_integration_loaded
    set -g __nova_shell_integration_loaded 1
    set -g __nova_command_start_ms ""

    # Portable millisecond clock. `date +%s%N` is GNU-only; macOS/BSD `date`
    # leaves a literal "%N", which would break `math`. Detect at runtime:
    # digits-only output is nanoseconds, anything else falls back to seconds.
    #
    # -s0 on every `math` call, because fish's default scale is 6: without it
    # the division prints "1780000000123.456787" and the OSC 133;D payload
    # stops being an integer, which is exactly what Nova's parser needs it to
    # be (long.TryParse) - so durations would be dropped entirely.
    function __nova_now_ms
        set -l raw (date +%s%N 2>/dev/null)
        if string match -qr '^[0-9]+$' -- $raw
            math -s0 "$raw / 1000000"
        else
            math -s0 (date +%s) "* 1000"
        end
    end

    function __nova_emit_prompt_ready
        printf '\033]7;file://%s%s\a' (hostname) (string escape --style=url -- $PWD)
        printf '\033]133;A\a'
    end

    # fish has native fish_preexec / fish_postexec events, so hooks layer
    # cleanly without overwriting anything.
    function __nova_preexec --on-event fish_preexec
        set -l b64 (printf '%s' "$argv" | base64 | tr -d '\n')
        printf '\033]133;C;%s\a' "$b64"
        set -g __nova_command_start_ms (__nova_now_ms)
    end

    function __nova_postexec --on-event fish_postexec
        set -l exit_code $status
        if test -n "$__nova_command_start_ms"
            set -l now_ms (__nova_now_ms)
            printf '\033]133;D;%s;%s\a' $exit_code (math -s0 $now_ms - $__nova_command_start_ms)
            set -g __nova_command_start_ms ""
        end
    end

    # The fish_prompt EVENT fires before the prompt function runs, so it can
    # only carry A.
    function __nova_promptmark --on-event fish_prompt
        __nova_emit_prompt_ready
    end

    # OSC 133;B has to land after the last prompt cell and fish has no
    # post-prompt event, so the user's fish_prompt is copied aside and
    # fish_prompt is redefined as "original, then B". The copy keeps the user's
    # output byte-for-byte; we only append to it.
    #
    # Bail out entirely if fish_prompt cannot be resolved (functions -q triggers
    # autoloading, so this only fails in a genuinely broken config): degrading
    # to A-only beats replacing the user's prompt with a synthesized one.
    #
    # The `not functions -q __nova_user_fish_prompt` half is what makes a
    # re-source safe. Without it a second pass would copy the CURRENT
    # fish_prompt - already our wrapper - over __nova_user_fish_prompt, and the
    # redefinition below would then call itself forever.
    if functions -q fish_prompt; and not functions -q __nova_user_fish_prompt
        functions --copy fish_prompt __nova_user_fish_prompt
        function fish_prompt
            __nova_user_fish_prompt
            printf '\033]133;B\a'
        end
    end

    __nova_emit_prompt_ready
end
