# Ntilde remote shell integration (bash and zsh).
#
# WHAT IT DOES
#   Emits the OSC 133 shell-integration marks and OSC 7 working-directory
#   reports that Ntilde's Command Assist reads:
#     OSC 7            the current directory, once per prompt
#     OSC 133;A        prompt start
#     OSC 133;B        prompt end / first cell of your input
#     OSC 133;C;<b64>  the line you submitted, base64-encoded
#     OSC 133;D;<n>;<ms>  exit code and duration of the command that just ran
#   Ntilde cannot inject this over SSH (an --rcfile path or a ZDOTDIR override
#   does not survive the hop), so you install it on the remote host yourself.
#
# INSTALL (on the REMOTE host)
#   1. cat > ~/.ntilde-shell-integration.sh
#      ...paste this whole file, then press Ctrl-D...
#   2. Add the loader line to your rc file:
#      bash:  echo '[ -f ~/.ntilde-shell-integration.sh ] && . ~/.ntilde-shell-integration.sh' >> ~/.bashrc
#      zsh:   echo '[ -f ~/.ntilde-shell-integration.sh ] && . ~/.ntilde-shell-integration.sh' >> ~/.zshrc
#   3. Open a new Ntilde session to that host (or run the loader line once now).
#
# GUARANTEES
#   - Your prompt is appended to, never replaced.
#   - Sourcing this file twice is a no-op the second time.
#   - Non-interactive shells (scp, rsync, ssh host cmd) exit immediately.
#   - fish is a separate file: ntilde-shell-integration.fish.
#     PowerShell is a separate file: ntilde-shell-integration.ps1.
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
if [ -n "${__ntilde_shell_integration_loaded:-}" ]; then
    return 0 2>/dev/null || exit 0
fi
__ntilde_shell_integration_loaded=1

# --- shared core -----------------------------------------------------------

__ntilde_command_start_ms=""

# Portable millisecond clock. The GNU nanosecond `date` format is not portable:
# on macOS/BSD it leaves a literal "%N" that breaks the arithmetic. Prefer the
# shell's own $EPOCHREALTIME (bash 5+ builtin, zsh via zsh/datetime) and fall
# back to whole seconds.
__ntilde_now_ms() {
    if [ -n "${EPOCHREALTIME:-}" ]; then
        __ntilde_sec="${EPOCHREALTIME%.*}"
        # Both bash 5 and zsh/datetime give exactly six fractional digits, so
        # dropping the last three leaves milliseconds. POSIX suffix removal
        # rather than ${var:0:3}, which dash cannot parse - this function is
        # defined before the shell is known.
        __ntilde_frac="${EPOCHREALTIME#*.}"
        printf '%s%s' "$__ntilde_sec" "${__ntilde_frac%???}"
    else
        printf '%s000' "$(date +%s)"
    fi
}

__ntilde_url_encode_pwd() {
    printf '%s' "$PWD" | LC_ALL=C awk 'BEGIN{for(i=0;i<256;i++)c[sprintf("%c",i)]=i} {for(i=1;i<=length($0);i++){ch=substr($0,i,1); if(ch~/[A-Za-z0-9._~\/-]/) printf "%s",ch; else printf "%%%02X",c[ch]}}'
}

__ntilde_emit_prompt_ready() {
    printf '\033]7;file://%s%s\a' "${HOSTNAME:-${HOST:-localhost}}" "$(__ntilde_url_encode_pwd)"
    printf '\033]133;A\a'
}

__ntilde_emit_accepted() {
    __ntilde_b64=$(printf '%s' "$1" | base64 | tr -d '\n')
    printf '\033]133;C;%s\a' "$__ntilde_b64"
    __ntilde_command_start_ms=$(__ntilde_now_ms)
}

__ntilde_emit_completion() {
    if [ -z "$__ntilde_command_start_ms" ]; then
        return
    fi
    __ntilde_now=$(__ntilde_now_ms)
    printf '\033]133;D;%s;%s\a' "$1" "$((__ntilde_now - __ntilde_command_start_ms))"
    __ntilde_command_start_ms=""
}

# --- bash wiring -----------------------------------------------------------

if [ -n "${BASH_VERSION:-}" ]; then

    # Start armed-as-busy so the first PROMPT_COMMAND cycle - which runs before
    # the user has typed anything - cannot capture the user's own
    # PROMPT_COMMAND helpers as a phantom accepted command. __ntilde_arm clears
    # it at the end of each cycle, immediately before bash returns to readline.
    __ntilde_command_active=1

    # OSC 133;B marks the END of the prompt, i.e. the cell where input begins.
    # bash prints PS1 *after* PROMPT_COMMAND runs, so unlike A this cannot come
    # from a hook - it has to ride at the tail of PS1 itself. \[ \] wrap it as
    # non-printing so bash's prompt-width arithmetic (and readline's wrapping)
    # is unaffected.
    __ntilde_ps1_mark='\[\e]133;B\a\]'

    # Re-applied every prompt cycle rather than once at load: starship,
    # oh-my-posh and friends rewrite PS1 from inside PROMPT_COMMAND, which would
    # drop a one-shot suffix. The containment check keeps repeated application
    # idempotent for the ordinary static-PS1 case.
    __ntilde_apply_ps1_mark() {
        case "$PS1" in
            *"$__ntilde_ps1_mark"*) ;;
            *) PS1="$PS1$__ntilde_ps1_mark" ;;
        esac
    }

    __ntilde_arm() {
        __ntilde_apply_ps1_mark
        __ntilde_command_active=0
    }

    # $BASH_COMMAND is the first SIMPLE COMMAND of the line, not the line:
    # `true && false` sets it to `true`, and reporting that as the accepted
    # command records the wrong text with the wrong exit code. bash-preexec's
    # answer is the only one that works - read the line back out of history,
    # where readline has already stored it verbatim, and strip the history
    # number. The BASH_COMMAND fallback covers a shell with history disabled
    # (`set +o history`) and a leading-space command swallowed by
    # HISTCONTROL=ignorespace; in both cases the first simple command is still
    # better than nothing.
    __ntilde_history_line() {
        __ntilde_hist=$(HISTTIMEFORMAT='' builtin history 1 2>/dev/null)
        # "  512  true && false" -> "true && false", using only POSIX
        # parameter expansion so the same idiom reads the same in both shells.
        __ntilde_hist="${__ntilde_hist#"${__ntilde_hist%%[![:space:]]*}"}"
        __ntilde_hist="${__ntilde_hist#*[[:space:]]}"
        __ntilde_hist="${__ntilde_hist#"${__ntilde_hist%%[![:space:]]*}"}"
        printf '%s' "$__ntilde_hist"
    }

    # bash has no native preexec. The DEBUG trap fires before every simple
    # command - including from inside PROMPT_COMMAND - so a one-shot flag held
    # busy for the whole prompt cycle and released only at the very end of it
    # isolates the user-entered line.
    #
    # Only __ntilde_* is filtered here. `trap*` and `PROMPT_COMMAND*` used to be
    # filtered too, which silently dropped any command the user typed that
    # began with either word; the busy-for-the-whole-chain invariant
    # (__ntilde_precmd raises the flag, __ntilde_arm lowers it) is what actually
    # keeps our own hooks out, so the name patterns were both unnecessary and
    # harmful.
    __ntilde_preexec() {
        if [ "$__ntilde_command_active" = "1" ]; then
            return
        fi
        case "$BASH_COMMAND" in
            __ntilde_*) return ;;
        esac
        __ntilde_command_active=1
        __ntilde_line=$(__ntilde_history_line)
        [ -n "$__ntilde_line" ] || __ntilde_line="$BASH_COMMAND"
        __ntilde_emit_accepted "$__ntilde_line"
    }

    __ntilde_precmd() {
        # FIRST statement, before $? is read for anything else: this is what
        # restores the busy-for-the-whole-chain invariant. bash runs
        # PROMPT_COMMAND after an EMPTY Enter too, and on that path no user
        # command ran, so nothing raised the flag - leaving the first entry of
        # the user's own PROMPT_COMMAND chain to be captured as a phantom
        # accepted command. __ntilde_arm lowers it again at the end of the chain.
        __ntilde_status=$?
        __ntilde_command_active=1
        __ntilde_emit_completion "$__ntilde_status"
        __ntilde_emit_prompt_ready
    }

    # Not-already-wrapped guard. Without it a second source would leave
    # "__ntilde_precmd; __ntilde_precmd; ...; __ntilde_arm; __ntilde_arm" in the chain.
    # (The load guard at the top already covers the common case; this one covers
    # a chain rebuilt by a framework that captured PROMPT_COMMAND before us.)
    case "${PROMPT_COMMAND:-}" in
        *__ntilde_precmd*) ;;
        *)
            # bash 5.1+ lets PROMPT_COMMAND be an array, and a framework may
            # have declared it as one. Assigning a string to an array variable
            # would set element 0 and silently drop every other entry.
            case "$(declare -p PROMPT_COMMAND 2>/dev/null)" in
                "declare -a"*|"typeset -a"*)
                    PROMPT_COMMAND=(__ntilde_precmd "${PROMPT_COMMAND[@]}" __ntilde_arm)
                    ;;
                *)
                    if [ -n "${PROMPT_COMMAND:-}" ]; then
                        PROMPT_COMMAND="__ntilde_precmd; $PROMPT_COMMAND; __ntilde_arm"
                    else
                        PROMPT_COMMAND='__ntilde_precmd; __ntilde_arm'
                    fi
                    ;;
            esac
            ;;
    esac

    trap '__ntilde_preexec' DEBUG

    # Belt and braces for the very first prompt: PROMPT_COMMAND does run before
    # it, but a user PROMPT_COMMAND that aborts early would otherwise leave the
    # mark missing until the next cycle.
    __ntilde_apply_ps1_mark
    __ntilde_emit_prompt_ready

# --- zsh wiring ------------------------------------------------------------

elif [ -n "${ZSH_VERSION:-}" ]; then

    # zsh's native datetime module, for $EPOCHREALTIME in __ntilde_now_ms.
    # +p: because EPOCHREALTIME is a PARAMETER. `zmodload -F` with an unknown
    # feature name fails, and with the error swallowed the module never loads:
    # $EPOCHREALTIME stays unset, __ntilde_now_ms silently falls back to `date
    # +%s`, and every duration is reported as a whole number of seconds.
    zmodload -F zsh/datetime +p:EPOCHREALTIME 2>/dev/null || true

    # OSC 133;B has to be the last thing in PROMPT: precmd runs before PROMPT is
    # expanded, so B cannot be printed from a hook the way A is. %{...%} tells
    # zsh the sequence occupies zero columns, keeping prompt-width arithmetic
    # and ZLE redraw correct.
    __ntilde_prompt_mark=$'%{\e]133;B\a%}'

    # Strip-then-append rather than skip-if-present. zsh has no "arm last"
    # invariant like bash's __ntilde_arm, so a precmd hook registered after ours
    # can append to PROMPT and leave the mark buried mid-prompt, where it would
    # report the input cell several columns early. Removing a trailing match
    # (a no-op when absent) and re-appending is idempotent AND self-correcting.
    __ntilde_apply_prompt_mark() {
        PROMPT="${PROMPT%$__ntilde_prompt_mark}$__ntilde_prompt_mark"
    }

    # zsh has native preexec/precmd hooks and passes preexec the command as $1,
    # so no DEBUG-trap one-shot guard is needed.
    __ntilde_zsh_preexec() {
        __ntilde_emit_accepted "$1"
    }

    # Two precmd hooks rather than one, at opposite ends of the array, because
    # the two jobs want opposite positions. $? in a precmd hook is the status of
    # whatever ran immediately before it, which for an APPENDED hook is the
    # previous precmd hook - not the user's command - so the exit code has to be
    # snapshotted by a hook that runs FIRST. The prompt mark, conversely, has to
    # be re-applied by a hook that runs LAST, after any theme has finished
    # rewriting PROMPT.
    __ntilde_last_status=0

    __ntilde_zsh_status_snapshot() {
        __ntilde_last_status=$?
    }

    __ntilde_zsh_precmd() {
        __ntilde_emit_completion "$__ntilde_last_status"
        __ntilde_emit_prompt_ready
        __ntilde_apply_prompt_mark
    }

    typeset -ag precmd_functions preexec_functions
    case " ${precmd_functions[*]} " in
        *" __ntilde_zsh_status_snapshot "*) ;;
        *) precmd_functions=(__ntilde_zsh_status_snapshot "${precmd_functions[@]}") ;;
    esac
    case " ${precmd_functions[*]} " in
        *" __ntilde_zsh_precmd "*) ;;
        *) precmd_functions+=(__ntilde_zsh_precmd) ;;
    esac
    case " ${preexec_functions[*]} " in
        *" __ntilde_zsh_preexec "*) ;;
        *) preexec_functions+=(__ntilde_zsh_preexec) ;;
    esac

    # Some zsh configurations expand the first prompt before any precmd runs.
    __ntilde_apply_prompt_mark
    __ntilde_emit_prompt_ready

else

    # Neither bash nor zsh. Emitting A and B without a preexec hook would give
    # Ntilde a prompt anchor and no command lifecycle, which is worse than
    # nothing: the command-input window would open and never close, and the
    # grid reader would serve a running command's output as a command line.
    # Degrade to doing nothing at all, silently - an rc file is not the place
    # for a banner.
    :

fi
