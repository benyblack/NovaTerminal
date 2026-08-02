using System.IO;
using System.Text;

namespace NovaTerminal.CommandAssist.ShellIntegration.Bash;

public static class BashBootstrapBuilder
{
    public static string BuildScript()
    {
        const string nl = "\n";
        var b = new StringBuilder();
        b.Append("#!/usr/bin/env bash").Append(nl);
        b.Append("# Nova Terminal command-assist bootstrap. Sourced via `bash --rcfile`.").Append(nl);
        b.Append("# Source the user's bashrc first so customizations are preserved.").Append(nl);
        b.Append("if [ -f ~/.bashrc ]; then").Append(nl);
        b.Append("    . ~/.bashrc").Append(nl);
        b.Append("fi").Append(nl);
        b.Append(nl);
        b.Append("__nova_command_start_ms=\"\"").Append(nl);
        // Start armed-as-busy so the very first PROMPT_COMMAND cycle (which
        // runs before any user command is typed) cannot capture the user's
        // own PROMPT_COMMAND helpers as a phantom "accepted command".
        // __nova_arm clears this back to 0 at the end of each prompt cycle,
        // immediately before bash returns to readline for the next input.
        b.Append("__nova_command_active=1").Append(nl);
        b.Append(nl);
        // Portable millisecond clock. `date +%s%N` is GNU-only; on
        // macOS/BSD `date` it leaves a literal "%N" that breaks
        // arithmetic. Prefer the bash 5+ built-in $EPOCHREALTIME
        // (microsecond precision, no external process), fall back to
        // `date +%s` for older bash.
        b.Append("__nova_now_ms() {").Append(nl);
        b.Append("    if [ -n \"${EPOCHREALTIME:-}\" ]; then").Append(nl);
        b.Append("        local sec=\"${EPOCHREALTIME%.*}\"").Append(nl);
        b.Append("        local frac=\"${EPOCHREALTIME#*.}\"").Append(nl);
        b.Append("        printf '%s%s' \"$sec\" \"${frac:0:3}\"").Append(nl);
        b.Append("    else").Append(nl);
        b.Append("        printf '%s000' \"$(date +%s)\"").Append(nl);
        b.Append("    fi").Append(nl);
        b.Append("}").Append(nl);
        b.Append(nl);
        b.Append("__nova_url_encode_pwd() {").Append(nl);
        b.Append("    local s=\"$PWD\"").Append(nl);
        b.Append("    printf '%s' \"$s\" | LC_ALL=C awk 'BEGIN{for(i=0;i<256;i++)c[sprintf(\"%c\",i)]=i} {for(i=1;i<=length($0);i++){ch=substr($0,i,1); if(ch~/[A-Za-z0-9._~\\/-]/) printf \"%s\",ch; else printf \"%%%02X\",c[ch]}}'").Append(nl);
        b.Append("}").Append(nl);
        b.Append(nl);
        b.Append("__nova_emit_prompt_ready() {").Append(nl);
        b.Append("    printf '\\033]7;file://%s%s\\a' \"${HOSTNAME:-localhost}\" \"$(__nova_url_encode_pwd)\"").Append(nl);
        b.Append("    printf '\\033]133;A\\a'").Append(nl);
        b.Append("}").Append(nl);
        b.Append(nl);
        b.Append("__nova_emit_completion() {").Append(nl);
        b.Append("    local exit=$1").Append(nl);
        b.Append("    if [ -z \"$__nova_command_start_ms\" ]; then").Append(nl);
        b.Append("        return").Append(nl);
        b.Append("    fi").Append(nl);
        b.Append("    local now_ms duration_ms").Append(nl);
        b.Append("    now_ms=$(__nova_now_ms)").Append(nl);
        b.Append("    duration_ms=$((now_ms - __nova_command_start_ms))").Append(nl);
        b.Append("    printf '\\033]133;D;%s;%s\\a' \"$exit\" \"$duration_ms\"").Append(nl);
        b.Append("    __nova_command_start_ms=\"\"").Append(nl);
        // NOTE: deliberately do NOT clear __nova_command_active here.
        // The user's PROMPT_COMMAND statements run AFTER this function in
        // the same prompt cycle; if we cleared active here, each of those
        // statements would fire the DEBUG trap with active=0 and be
        // captured as the user's "accepted command". __nova_arm (which
        // runs LAST in PROMPT_COMMAND) is responsible for clearing it.
        b.Append("}").Append(nl);
        b.Append(nl);
        // OSC 133;B marks the END of the prompt, i.e. the cell where the
        // user's input begins. Bash prints PS1 *after* PROMPT_COMMAND has
        // run, so unlike A (emitted from __nova_precmd) B cannot be written
        // from a hook -- it has to ride along at the tail of PS1 itself.
        // \[ \] wrap it as non-printing so bash's prompt-width arithmetic
        // (and therefore readline's line wrapping) is unaffected.
        b.Append("__nova_ps1_mark='\\[\\e]133;B\\a\\]'").Append(nl);
        // Re-applied every prompt cycle rather than once at startup: themes
        // like starship/oh-my-posh rewrite PS1 from inside PROMPT_COMMAND,
        // which would drop a one-shot suffix. The containment check keeps
        // repeated application idempotent for the ordinary static-PS1 case.
        b.Append("__nova_apply_ps1_mark() {").Append(nl);
        b.Append("    case \"$PS1\" in").Append(nl);
        b.Append("        *\"$__nova_ps1_mark\"*) ;;").Append(nl);
        b.Append("        *) PS1=\"$PS1$__nova_ps1_mark\" ;;").Append(nl);
        b.Append("    esac").Append(nl);
        b.Append("}").Append(nl);
        b.Append(nl);
        b.Append("__nova_arm() {").Append(nl);
        // Runs LAST in PROMPT_COMMAND, which is also the only point where the
        // user's own PROMPT_COMMAND has finished rewriting PS1 -- so the mark
        // is (re)appended here rather than as a separate chain entry, keeping
        // the "arm last" invariant and the chain string itself unchanged.
        b.Append("    __nova_apply_ps1_mark").Append(nl);
        b.Append("    __nova_command_active=0").Append(nl);
        b.Append("}").Append(nl);
        b.Append(nl);
        // $BASH_COMMAND is the first SIMPLE COMMAND of the line, not the line:
        // `true && false` sets it to `true`, so reporting it as the accepted
        // command records the wrong text alongside the other branch's exit
        // code. bash-preexec's answer is the only one available - read the
        // line back out of history, where readline stored it verbatim before
        // execution, and strip the leading history number. The BASH_COMMAND
        // fallback covers `set +o history` and a leading-space command
        // swallowed by HISTCONTROL=ignorespace.
        b.Append("__nova_history_line() {").Append(nl);
        b.Append("    local line").Append(nl);
        b.Append("    line=$(HISTTIMEFORMAT='' builtin history 1 2>/dev/null)").Append(nl);
        b.Append("    line=\"${line#\"${line%%[![:space:]]*}\"}\"").Append(nl);
        b.Append("    line=\"${line#*[[:space:]]}\"").Append(nl);
        b.Append("    line=\"${line#\"${line%%[![:space:]]*}\"}\"").Append(nl);
        b.Append("    printf '%s' \"$line\"").Append(nl);
        b.Append("}").Append(nl);
        b.Append(nl);
        // Bash has no native preexec. We approximate it via the DEBUG trap,
        // which fires before every simple command -- including from inside
        // PROMPT_COMMAND. To capture only the user-entered command line, the
        // flag is held busy for the whole prompt cycle (__nova_precmd raises
        // it, __nova_arm lowers it) and the first DEBUG fire after that is the
        // user's line.
        //
        // Only __nova_* is filtered by name. `trap*` and `PROMPT_COMMAND*`
        // were filtered too and silently dropped any user command starting
        // with either word; the busy-for-the-whole-chain invariant is what
        // actually keeps our own hooks out, so the name patterns were both
        // unnecessary and harmful.
        b.Append("__nova_preexec() {").Append(nl);
        b.Append("    if [ \"$__nova_command_active\" = \"1\" ]; then").Append(nl);
        b.Append("        return").Append(nl);
        b.Append("    fi").Append(nl);
        b.Append("    case \"$BASH_COMMAND\" in").Append(nl);
        b.Append("        __nova_*) return ;;").Append(nl);
        b.Append("    esac").Append(nl);
        b.Append("    __nova_command_active=1").Append(nl);
        b.Append("    local cmd b64").Append(nl);
        b.Append("    cmd=$(__nova_history_line)").Append(nl);
        b.Append("    [ -n \"$cmd\" ] || cmd=\"$BASH_COMMAND\"").Append(nl);
        b.Append("    b64=$(printf '%s' \"$cmd\" | base64 | tr -d '\\n')").Append(nl);
        b.Append("    printf '\\033]133;C;%s\\a' \"$b64\"").Append(nl);
        b.Append("    __nova_command_start_ms=$(__nova_now_ms)").Append(nl);
        b.Append("}").Append(nl);
        b.Append(nl);
        b.Append("__nova_precmd() {").Append(nl);
        b.Append("    local exit=$?").Append(nl);
        // FIRST statement after the status snapshot, and that ordering is the
        // whole point: bash runs PROMPT_COMMAND after an EMPTY Enter too, and
        // on that path no user command ran, so nothing raised the flag --
        // leaving the first entry of the user's own PROMPT_COMMAND chain to be
        // captured as a phantom accepted command. Raising it here restores the
        // busy-for-the-whole-chain invariant; __nova_arm lowers it at the end.
        b.Append("    __nova_command_active=1").Append(nl);
        b.Append("    __nova_emit_completion \"$exit\"").Append(nl);
        b.Append("    __nova_emit_prompt_ready").Append(nl);
        b.Append("}").Append(nl);
        b.Append(nl);
        b.Append("trap '__nova_preexec' DEBUG").Append(nl);
        // Bracket the user's PROMPT_COMMAND with __nova_precmd (first: emit
        // the previous command's D + new prompt-ready A) and __nova_arm
        // (last: release the DEBUG-trap suppression so the user's next typed
        // command is the first one captured). The DEBUG fires inside this
        // chain stay suppressed because __nova_command_active is still 1
        // throughout -- user PROMPT_COMMAND helpers cannot masquerade as
        // accepted commands.
        b.Append("if [ -n \"$PROMPT_COMMAND\" ]; then").Append(nl);
        b.Append("    PROMPT_COMMAND=\"__nova_precmd; $PROMPT_COMMAND; __nova_arm\"").Append(nl);
        b.Append("else").Append(nl);
        b.Append("    PROMPT_COMMAND='__nova_precmd; __nova_arm'").Append(nl);
        b.Append("fi").Append(nl);
        b.Append(nl);
        // Belt and braces for the very first prompt: PROMPT_COMMAND does run
        // before it, but if the user's PROMPT_COMMAND aborts early the mark
        // would otherwise be missing until the next cycle.
        b.Append("__nova_apply_ps1_mark").Append(nl);
        b.Append("__nova_emit_prompt_ready").Append(nl);
        return b.ToString();
    }

    public static string WriteScript(string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);
        string path = Path.Combine(targetDirectory, "command-assist-bootstrap.bash");
        File.WriteAllText(path, BuildScript(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }
}
