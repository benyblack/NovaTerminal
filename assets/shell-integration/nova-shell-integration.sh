# Nova Terminal remote shell integration (bash and zsh).
#
# WHAT IT DOES
#   Emits the OSC 133 shell-integration marks and OSC 7 working-directory
#   reports that Nova Terminal's Command Assist reads:
#     OSC 7            the current directory, once per prompt
#     OSC 133;A        prompt start
#     OSC 133;B        prompt end / first cell of your input
#     OSC 133;C;<b64>  the line you submitted, base64-encoded
#     OSC 133;D;<n>;<ms>  exit code and duration of the command that just ran
#   Nova cannot inject this over SSH (an --rcfile path or a ZDOTDIR override
#   does not survive the hop), so you install it on the remote host yourself.
#
# INSTALL (on the REMOTE host)
#   1. cat > ~/.nova-shell-integration.sh
#      ...paste this whole file, then press Ctrl-D...
#   2. Add the loader line to your rc file:
#      bash:  echo '[ -f ~/.nova-shell-integration.sh ] && . ~/.nova-shell-integration.sh' >> ~/.bashrc
#      zsh:   echo '[ -f ~/.nova-shell-integration.sh ] && . ~/.nova-shell-integration.sh' >> ~/.zshrc
#   3. Open a new Nova session to that host (or run the loader line once now).
#
# GUARANTEES
#   - Your prompt is appended to, never replaced.
#   - Sourcing this file twice is a no-op the second time.
#   - Non-interactive shells (scp, rsync, ssh host cmd) exit immediately.
#   - fish is a separate file: nova-shell-integration.fish.
#     PowerShell is a separate file: nova-shell-integration.ps1.
#
# Docs: docs/command-assist/RemoteShellIntegration.md

# --- bail-out guards -------------------------------------------------------

# Non-interactive shells must stay byte-clean: an OSC written into an scp or
# rsync stream corrupts the transfer.
case "$-" in
    *i*) ;;
    *) return 0 2>/dev/null || exit 0 ;;
esac

# Idempotence. Re-sourcing (a second rc pass, `exec bash`, a manual `.`) must
# not chain the hooks twice, which would emit every mark twice and, in bash,
# wrap the prompt around itself.
if [ -n "${__nova_shell_integration_loaded:-}" ]; then
    return 0 2>/dev/null || exit 0
fi
__nova_shell_integration_loaded=1

# --- shared core -----------------------------------------------------------

__nova_command_start_ms=""

# Portable millisecond clock. The GNU nanosecond `date` format is not portable:
# on macOS/BSD it leaves a literal "%N" that breaks the arithmetic. Prefer the
# shell's own $EPOCHREALTIME (bash 5+ builtin, zsh via zsh/datetime) and fall
# back to whole seconds.
__nova_now_ms() {
    if [ -n "${EPOCHREALTIME:-}" ]; then
        __nova_sec="${EPOCHREALTIME%.*}"
        # Both bash 5 and zsh/datetime give exactly six fractional digits, so
        # dropping the last three leaves milliseconds. POSIX suffix removal
        # rather than ${var:0:3}, which dash cannot parse - this function is
        # defined before the shell is known.
        __nova_frac="${EPOCHREALTIME#*.}"
        printf '%s%s' "$__nova_sec" "${__nova_frac%???}"
    else
        printf '%s000' "$(date +%s)"
    fi
}

__nova_url_encode_pwd() {
    printf '%s' "$PWD" | LC_ALL=C awk 'BEGIN{for(i=0;i<256;i++)c[sprintf("%c",i)]=i} {for(i=1;i<=length($0);i++){ch=substr($0,i,1); if(ch~/[A-Za-z0-9._~\/-]/) printf "%s",ch; else printf "%%%02X",c[ch]}}'
}

__nova_emit_prompt_ready() {
    printf '\033]7;file://%s%s\a' "${HOSTNAME:-${HOST:-localhost}}" "$(__nova_url_encode_pwd)"
    printf '\033]133;A\a'
}

__nova_emit_accepted() {
    __nova_b64=$(printf '%s' "$1" | base64 | tr -d '\n')
    printf '\033]133;C;%s\a' "$__nova_b64"
    __nova_command_start_ms=$(__nova_now_ms)
}

__nova_emit_completion() {
    if [ -z "$__nova_command_start_ms" ]; then
        return
    fi
    __nova_now=$(__nova_now_ms)
    printf '\033]133;D;%s;%s\a' "$1" "$((__nova_now - __nova_command_start_ms))"
    __nova_command_start_ms=""
}

# --- bash wiring -----------------------------------------------------------

if [ -n "${BASH_VERSION:-}" ]; then

    # Start armed-as-busy so the first PROMPT_COMMAND cycle - which runs before
    # the user has typed anything - cannot capture the user's own
    # PROMPT_COMMAND helpers as a phantom accepted command. __nova_arm clears
    # it at the end of each cycle, immediately before bash returns to readline.
    __nova_command_active=1

    # OSC 133;B marks the END of the prompt, i.e. the cell where input begins.
    # bash prints PS1 *after* PROMPT_COMMAND runs, so unlike A this cannot come
    # from a hook - it has to ride at the tail of PS1 itself. \[ \] wrap it as
    # non-printing so bash's prompt-width arithmetic (and readline's wrapping)
    # is unaffected.
    __nova_ps1_mark='\[\e]133;B\a\]'

    # Re-applied every prompt cycle rather than once at load: starship,
    # oh-my-posh and friends rewrite PS1 from inside PROMPT_COMMAND, which would
    # drop a one-shot suffix. The containment check keeps repeated application
    # idempotent for the ordinary static-PS1 case.
    __nova_apply_ps1_mark() {
        case "$PS1" in
            *"$__nova_ps1_mark"*) ;;
            *) PS1="$PS1$__nova_ps1_mark" ;;
        esac
    }

    __nova_arm() {
        __nova_apply_ps1_mark
        __nova_command_active=0
    }

    # bash has no native preexec. The DEBUG trap fires before every simple
    # command - including from inside PROMPT_COMMAND - so a one-shot flag armed
    # in PROMPT_COMMAND and disarmed on the first DEBUG fire after it isolates
    # the user-entered line.
    __nova_preexec() {
        if [ "$__nova_command_active" = "1" ]; then
            return
        fi
        case "$BASH_COMMAND" in
            __nova_*|trap*|PROMPT_COMMAND*) return ;;
        esac
        __nova_emit_accepted "$BASH_COMMAND"
        __nova_command_active=1
    }

    __nova_precmd() {
        __nova_emit_completion "$?"
        __nova_emit_prompt_ready
    }

    # Not-already-wrapped guard. Without it a second source would leave
    # "__nova_precmd; __nova_precmd; ...; __nova_arm; __nova_arm" in the chain.
    # (The load guard at the top already covers the common case; this one covers
    # a chain rebuilt by a framework that captured PROMPT_COMMAND before us.)
    case "${PROMPT_COMMAND:-}" in
        *__nova_precmd*) ;;
        *)
            # bash 5.1+ lets PROMPT_COMMAND be an array, and a framework may
            # have declared it as one. Assigning a string to an array variable
            # would set element 0 and silently drop every other entry.
            case "$(declare -p PROMPT_COMMAND 2>/dev/null)" in
                "declare -a"*|"typeset -a"*)
                    PROMPT_COMMAND=(__nova_precmd "${PROMPT_COMMAND[@]}" __nova_arm)
                    ;;
                *)
                    if [ -n "${PROMPT_COMMAND:-}" ]; then
                        PROMPT_COMMAND="__nova_precmd; $PROMPT_COMMAND; __nova_arm"
                    else
                        PROMPT_COMMAND='__nova_precmd; __nova_arm'
                    fi
                    ;;
            esac
            ;;
    esac

    trap '__nova_preexec' DEBUG

    # Belt and braces for the very first prompt: PROMPT_COMMAND does run before
    # it, but a user PROMPT_COMMAND that aborts early would otherwise leave the
    # mark missing until the next cycle.
    __nova_apply_ps1_mark
    __nova_emit_prompt_ready

# --- zsh wiring ------------------------------------------------------------

elif [ -n "${ZSH_VERSION:-}" ]; then

    # zsh's native datetime module, for $EPOCHREALTIME in __nova_now_ms.
    zmodload -F zsh/datetime +b:EPOCHREALTIME 2>/dev/null || true

    # OSC 133;B has to be the last thing in PROMPT: precmd runs before PROMPT is
    # expanded, so B cannot be printed from a hook the way A is. %{...%} tells
    # zsh the sequence occupies zero columns, keeping prompt-width arithmetic
    # and ZLE redraw correct.
    __nova_prompt_mark=$'%{\e]133;B\a%}'

    # Strip-then-append rather than skip-if-present. zsh has no "arm last"
    # invariant like bash's __nova_arm, so a precmd hook registered after ours
    # can append to PROMPT and leave the mark buried mid-prompt, where it would
    # report the input cell several columns early. Removing a trailing match
    # (a no-op when absent) and re-appending is idempotent AND self-correcting.
    __nova_apply_prompt_mark() {
        PROMPT="${PROMPT%$__nova_prompt_mark}$__nova_prompt_mark"
    }

    # zsh has native preexec/precmd hooks and passes preexec the command as $1,
    # so no DEBUG-trap one-shot guard is needed.
    __nova_zsh_preexec() {
        __nova_emit_accepted "$1"
    }

    __nova_zsh_precmd() {
        __nova_emit_completion "$?"
        __nova_emit_prompt_ready
        __nova_apply_prompt_mark
    }

    typeset -ag precmd_functions preexec_functions
    case " ${precmd_functions[*]} " in
        *" __nova_zsh_precmd "*) ;;
        *) precmd_functions+=(__nova_zsh_precmd) ;;
    esac
    case " ${preexec_functions[*]} " in
        *" __nova_zsh_preexec "*) ;;
        *) preexec_functions+=(__nova_zsh_preexec) ;;
    esac

    # Some zsh configurations expand the first prompt before any precmd runs.
    __nova_apply_prompt_mark
    __nova_emit_prompt_ready

else

    # Neither bash nor zsh. Emitting A and B without a preexec hook would give
    # Nova a prompt anchor and no command lifecycle, which is worse than
    # nothing: the command-input window would open and never close, and the
    # grid reader would serve a running command's output as a command line.
    # Degrade to doing nothing at all, silently - an rc file is not the place
    # for a banner.
    :

fi
